# Downora

**Downora** is a Windows download manager with a clean Google-inspired Material 3 interface and English / 简体中文 localization.

> Status: early development. The current prototype implements HTTP/HTTPS downloads and anonymous FTP/explicit FTPS downloads, with a three-task concurrency limit. HTTP Range and FTP REST resume are attempted when supported by the server. BitTorrent/Magnet, eD2k, M3U8, browser integration and advanced scheduling are not yet implemented.

## Features

- Windows 10/11 x64 desktop app (WPF, .NET 8)
- Material-inspired layout, light-first palette
- English / 简体中文 language switch
- HTTP/HTTPS downloads with progress, pause/resume (server Range support required), cancel, queue and speed display
- Automatic four-way segmented HTTP downloads for files of 16 MiB or larger when the server advertises and honors byte ranges; segment fragments are retained for pause/resume
- Bounded automatic retries for transient HTTP connection errors and HTTP 408 / 429 / 5xx responses
- FTP and explicit FTPS (AUTH TLS, port 21 by default) downloads with username/password stored in Windows Credential Manager; pause/resume where the server supports REST; implicit FTPS (port 990) is not supported
- Local JSON queue persistence; unfinished tasks are restored as paused when the app starts and can be resumed
- Automatic category folders (Videos, Documents, Archives, Other)
- Completion notification
- Automatic SHA-256 checksum calculation and display after successful downloads
- Rejects credential-bearing URLs so passwords are not written into the persistent download queue
- Stores FTP / FTPS credentials in Windows Credential Manager rather than the queue file
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
- [x] Four-way HTTP segmented downloads for range-capable servers (16 MiB+ files)
- [x] Basic anonymous FTP / explicit FTPS downloads
- [x] Reject credential-bearing URLs to protect persisted queue data
- [x] Authenticated FTP / FTPS support using Windows Credential Manager
- [ ] BitTorrent / Magnet adapter (libtorrent)
- [ ] eD2k adapter (compatible, legally distributable engine)
- [ ] M3U8 download and remux workflow
- [ ] Browser handoff / extension
- [x] SHA-256 checksum calculation after completed downloads
- [ ] Clipboard monitoring, scheduler, comparison against publisher-provided checksums
- [ ] Installer and signed Windows release

## Legal & safety

Only download content you own or are authorized to access. Downora will not bypass DRM, paywalls, authentication controls or access restrictions. P2P modules must respect the licenses of their upstream engines and the rights of content owners.

## License

MIT. Third-party engines, if added, retain their own licenses and notices.
