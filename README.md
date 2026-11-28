# Downora

**Downora** is a Windows download manager with a clean Google-inspired Material 3 interface and English / 简体中文 localization.

> Status: early development. The current prototype implements HTTP/HTTPS downloads and authenticated FTP/explicit FTPS downloads, with a three-task concurrency limit. It can parse and validate eD2k file links, submit them to a separately installed local aMule 3.2.0+ engine, and synchronize its queue, progress, speed, source count, pause/resume and cancellation controls through the REST API. HTTP Range and FTP REST resume are attempted when supported by the server. Magnet link parsing and an embedded MonoTorrent engine are integrated into the main window. Users can add Magnet links or browse for .torrent files, view progress, transfer speeds and peer counts, pause/resume tasks, and remove tasks while keeping downloaded data. Engine state is saved on exit and restored at startup; live-swarm validation is still pending. Initial video-platform integration uses the official yt-dlp Windows binary to extract metadata and download supported Bilibili, YouTube, Vimeo, Dailymotion and Twitch URLs. Built-in eD2k engine functionality, M3U8, browser integration and advanced scheduling are also not yet implemented.

## Features

- Windows 10/11 x64 desktop app (WPF, .NET 8)
- Material-inspired layout, light-first palette
- English / 简体中文 language switch
- HTTP/HTTPS downloads with progress, pause/resume (server Range support required), cancel, queue and speed display
- Automatic four-way segmented HTTP downloads for files of 16 MiB or larger when the server advertises and honors byte ranges; segment fragments are retained for pause/resume
- Bounded automatic retries for transient HTTP connection errors and HTTP 408 / 429 / 5xx responses
- FTP and explicit FTPS (AUTH TLS, port 21 by default) downloads with username/password stored in Windows Credential Manager; pause/resume where the server supports REST; implicit FTPS (port 990) is not supported
- eD2k file-link parsing and validation, with aMule 3.2.0+ REST API integration for live queue/progress/speed/source synchronization and pause/resume/cancel controls
- Local JSON queue persistence; unfinished tasks are restored as paused when the app starts and can be resumed
- Automatic category folders (Videos, Documents, Archives, Other)
- Completion notification and automatic SHA-256 checksum display
- FTP / FTPS credentials stored in Windows Credential Manager
- Safe filename handling, collision avoidance and rejection of credential-bearing URLs
- Video-platform URL detection and metadata extraction for Bilibili, YouTube, Vimeo, Dailymotion and Twitch through yt-dlp; progress and cancellation are shown in a dedicated video queue. The pinned Windows binary is downloaded on first use and SHA-256 verified. Initial format selection uses the best single-file format up to 1080p; separate-track merging (FFmpeg), browser-cookie login and persistent video-task recovery are not implemented yet. Some YouTube videos may require the optional JavaScript runtime and EJS components expected by current yt-dlp releases. See [third-party notices](THIRD_PARTY_NOTICES.md) for licensing details.\n- BitTorrent magnet URI parsing (40-character hexadecimal and 32-character base32 info hashes, display names and HTTP/HTTPS/UDP tracker extraction)
- Embedded MonoTorrent 3.0.2 engine with main-window task list for Magnet / .torrent tasks, progress, transfer speeds, peer counts, pause/resume and removal; state save on exit and automatic task restoration at startup are implemented; live swarm validation remains pending

## Windows one-click installer

Download the **Downora-Windows-Setup** artifact from the latest successful [Windows build](https://github.com/Yoshinokura/Downora/actions/workflows/windows-build.yml), run `Downora-Setup.exe`, and follow the installer. The app is published self-contained, so users do not need to install the .NET runtime or SDK separately. The installer supports Windows 10/11 x64, per-user installation, Start menu shortcuts and an optional desktop shortcut.

The CI installer is currently an unsigned build artifact (not yet a signed public release). The separate aMule engine is not bundled into the Downora installer; users can set it up on demand with the in-app **Set up eD2k** button. The setup helper tries two geographically routed official SourceForge download endpoints, then the official GitHub release as a fallback. Every source is checked against the same pinned upstream SHA-256; failed or mismatched downloads are discarded. Actual mainland China speed still depends on the user's ISP and current international routing, so no fixed speed is guaranteed.

## Run from source

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
- [x] Queue persistence and recovery after app restart
- [x] Four-way HTTP segmented downloads for range-capable servers (16 MiB+ files)
- [x] FTP / explicit FTPS downloads and Windows Credential Manager authentication
- [x] eD2k link parsing and submission to aMule REST API
- [x] One-click Windows x64 aMule setup (official package download, SHA-256 verification, loopback API configuration, Windows Credential Manager integration)
- [x] Self-contained Windows x64 installer generated by CI
- [x] eD2k queue / progress / speed / source synchronization and pause / resume / cancel controls
- [x] Magnet URI parsing and info-hash validation
- [x] Embedded MonoTorrent engine adapter foundation
- [x] BitTorrent task-list UI integration for Magnet and .torrent tasks, progress/speed/peer display and pause/resume/removal
- [x] BitTorrent engine state save and task restoration at app restart
- [ ] Live BitTorrent swarm validation
- [ ] M3U8 download and remux workflow
- [ ] Browser handoff / extension
- [ ] Clipboard monitoring, scheduler and publisher-checksum comparison
- [ ] Signed Windows release

## eD2k engine setup

On Windows x64, click **Set up eD2k** in Downora. The bundled PowerShell helper downloads the official aMule 3.2.0 portable package, verifies its SHA-256 checksum, extracts it under `%LOCALAPPDATA%\\Downora\\Engine\\aMule`, configures the API to listen only on `127.0.0.1:4713`, generates random credentials, stores the API password in Windows Credential Manager, and starts the engine. Downora starts the installed daemon automatically on later launches.

If aMule is already running, close it before setup so active transfers are not interrupted. Existing `amule.conf` settings are backed up once as `amule.conf.downora-backup`. The helper does not require administrator rights. The engine is downloaded on demand and is not bundled with Downora.

aMule remains a separate GPL-2.0 application. Downora does not link its code; the setup helper retrieves the official portable distribution and verifies the published package hash. The helper targets Windows x64 and pins aMule 3.2.0, with its published SHA-256 checked before extraction. Setup backs up an existing `amule.conf` once and asks you to close a running aMule process rather than interrupting active transfers.

## Legal & safety

Only download content you own or are authorized to access. Downora will not bypass DRM, paywalls, authentication controls or access restrictions. P2P modules must respect the licenses of their upstream engines and the rights of content owners.

## License

MIT. The embedded MonoTorrent library is MIT-licensed and its license notices must be retained with distributions. Third-party engines retain their own licenses and notices.
