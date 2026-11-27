using System.IO;
using MonoTorrent;
using MonoTorrent.Client;

namespace Downora;

public sealed record BitTorrentTaskSnapshot(
    string InfoHash,
    string Name,
    double Progress,
    long SizeBytes,
    long DownloadSpeedBytesPerSecond,
    long UploadSpeedBytesPerSecond,
    int ConnectedPeers,
    string State,
    bool IsComplete);

/// <summary>
/// Owns the embedded MonoTorrent engine and provides a small adapter surface
/// for the Downora UI. DHT and peer exchange are enabled; automatic router
/// port forwarding is disabled to avoid silently changing router settings.
/// </summary>
public sealed class BitTorrentEngine : IAsyncDisposable
{
    private readonly ClientEngine _engine;
    private readonly string _downloadDirectory;
    private bool _disposed;

    public BitTorrentEngine(string applicationDataDirectory, string downloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);

        _downloadDirectory = Path.GetFullPath(downloadDirectory);
        var cacheDirectory = Path.Combine(Path.GetFullPath(applicationDataDirectory), "BitTorrentCache");
        Directory.CreateDirectory(_downloadDirectory);
        Directory.CreateDirectory(cacheDirectory);

        var engineSettings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowPortForwarding = false
        }.ToSettings();

        _engine = new ClientEngine(engineSettings);
    }

    public IReadOnlyList<BitTorrentTaskSnapshot> GetTasks() =>
        _engine.Torrents.Select(CreateSnapshot).ToArray();

    public async Task<BitTorrentTaskSnapshot> AddMagnetAsync(
        string magnetUri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!MagnetLink.TryParse(magnetUri, out _, out var validationError))
            throw new ArgumentException(validationError, nameof(magnetUri));

        var parsed = MonoTorrent.MagnetLink.Parse(magnetUri);
        var manager = await _engine.AddAsync(parsed, _downloadDirectory).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await manager.StartAsync().ConfigureAwait(false);
        return CreateSnapshot(manager);
    }

    public async Task<BitTorrentTaskSnapshot> AddTorrentFileAsync(
        string torrentFilePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(torrentFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(torrentFilePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected .torrent file does not exist.", fullPath);

        var torrent = await Torrent.LoadAsync(fullPath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var manager = await _engine.AddAsync(torrent, _downloadDirectory).ConfigureAwait(false);
        await manager.StartAsync().ConfigureAwait(false);
        return CreateSnapshot(manager);
    }

    public async Task PauseAsync(string infoHash)
    {
        var manager = FindManager(infoHash);
        await manager.PauseAsync().ConfigureAwait(false);
    }

    public async Task ResumeAsync(string infoHash)
    {
        var manager = FindManager(infoHash);
        await manager.StartAsync().ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string infoHash, bool deleteDownloadedData = false)
    {
        var manager = FindManager(infoHash);
        await manager.StopAsync().ConfigureAwait(false);
        return await _engine.RemoveAsync(
            manager,
            deleteDownloadedData ? RemoveMode.DownloadedDataOnly : RemoveMode.KeepAllData
        ).ConfigureAwait(false);
    }

    public async Task SaveStateAsync(string stateFilePath)
    {
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(stateFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await _engine.SaveStateAsync(fullPath).ConfigureAwait(false);
    }

    private TorrentManager FindManager(string infoHash)
    {
        ThrowIfDisposed();
        var manager = _engine.Torrents.FirstOrDefault(x =>
            GetInfoHash(x).Equals(infoHash, StringComparison.OrdinalIgnoreCase));
        return manager ?? throw new KeyNotFoundException("The BitTorrent task could not be found.");
    }

    private static string GetInfoHash(TorrentManager manager) =>
        manager.Torrent?.InfoHashes.V1OrV2.ToString() ?? manager.MagnetLink.InfoHashes.V1OrV2.ToString();

    private static BitTorrentTaskSnapshot CreateSnapshot(TorrentManager manager)
    {
        var infoHash = GetInfoHash(manager);
        var size = manager.Torrent?.Size ?? manager.MagnetLink.Size ?? 0;
        return new BitTorrentTaskSnapshot(
            infoHash,
            string.IsNullOrWhiteSpace(manager.Name) ? infoHash : manager.Name,
            manager.Progress,
            size,
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.OpenConnections,
            manager.State.ToString(),
            manager.Complete);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _engine.StopAllAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        finally
        {
            _engine.Dispose();
        }
    }
}
