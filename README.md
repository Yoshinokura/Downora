# Downora

**Downora** is a Windows download manager with a clean Google-inspired Material 3 interface and English / 简体中文 localization.

> Status: early development. The current prototype implements HTTP/HTTPS downloads with a three-task concurrency limit and HTTP Range-based resume when supported by the server. FTP, BitTorrent/Magnet, eD2k, M3U8, browser integration and advanced scheduling are planned modules, not yet implemented.

## Features

- Windows 10/11 x64 desktop app (WPF, .NET 8)
- Material-inspired layout, light-first palette
- English / 简体中文 language switch
- HTTP/HTTPS downloads with progress, pause/resume (server Range support required), cancel, queue and speed display
- Local JSON queue persistence; unfinished tasks are restored as paused when the app starts and can be resumed
- Automatic category folders (Videos, Documents, Archives, Other)
- Completion notification
- Safe filename handling and collision avoidance

## Run

1. Install the .NET 8 SDK on Windows.
2. Open `src/Downora/Downora.csproj` in Visual Studio 2022 (or newer).
3. Build and run.

Or from PowerShell:

```powershell
dotnet run --project .\src\Downora\Downora.csproj
```

## Roadmap

- [x] HTTP/HTTPS single-file downloads with progress
- [x] Queue, pause/resume and cancel controls (HTTP Range resume where supported)
- [x] English / 简体中文 UI
- [x] Category-based save folders
- [x] Basic queue persistence and recovery after app restart (unfinished tasks restore paused)
- [ ] Multi-connection segmented downloads
- [ ] FTP / FTPS adapter
- [ ] BitTorrent / Magnet adapter (libtorrent)
- [ ] eD2k adapter (compatible, legally distributable engine)
- [ ] M3U8 download and remux workflow
- [ ] Browser handoff / extension
- [ ] Clipboard monitoring, scheduler, checksum verification
- [ ] Installer and signed Windows release

## Legal & safety

Only download content you own or are authorized to access. Downora will not bypass DRM, paywalls, authentication controls or access restrictions. P2P modules must respect the licenses of their upstream engines and the rights of content owners.

## License

MIT. Third-party engines, if added, retain their own licenses and notices.
