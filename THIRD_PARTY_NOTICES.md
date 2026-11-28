# Third-party notices

## yt-dlp

Downora downloads the official Windows standalone `yt-dlp.exe` into the current user's local application data directory on first video-download use. It is fetched from the official yt-dlp GitHub release and checked against the release asset's SHA-256 digest before execution.

The PyInstaller-built standalone executable is distributed by the yt-dlp project under GPL-3.0-or-later, with additional third-party components and notices. The executable is not bundled into the Downora installer or linked into the Downora process; it is launched as a separate process.

- Project and source: https://github.com/yt-dlp/yt-dlp
- License: https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE
- Third-party notices: https://github.com/yt-dlp/yt-dlp/blob/master/THIRD_PARTY_LICENSES.txt
- Release used by this integration: https://github.com/yt-dlp/yt-dlp/releases/tag/2026.11.23

Users should review the upstream license and applicable platform terms before using the video downloader.
