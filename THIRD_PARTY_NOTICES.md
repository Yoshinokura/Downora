# Third-party notices

## yt-dlp

Downora downloads the official Windows standalone `yt-dlp.exe` into the current user's local application data directory on first video-download use. It is fetched from the official yt-dlp GitHub release and checked against the release asset's SHA-256 digest before execution.

The PyInstaller-built standalone executable is distributed by the yt-dlp project under GPL-3.0-or-later, with additional third-party components and notices. The executable is not bundled into the Downora installer or linked into the Downora process; it is launched as a separate process.

- Project and source: https://github.com/yt-dlp/yt-dlp
- License: https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE
- Third-party notices: https://github.com/yt-dlp/yt-dlp/blob/master/THIRD_PARTY_LICENSES.txt
- Release used by this integration: https://github.com/yt-dlp/yt-dlp/releases/tag/2026.11.23

Users should review the upstream license and applicable platform terms before using the video downloader.

## Deno JavaScript runtime

For YouTube URLs, Downora downloads the official Deno Windows x64 runtime into the same local tools directory. The ZIP archive is verified against the SHA-256 digest published in the official Deno GitHub release before extraction.

- Project and source: https://github.com/denoland/deno
- License: https://github.com/denoland/deno/blob/main/LICENSE
- Release used by this integration: https://github.com/denoland/deno/releases/tag/v2.9.8


## FFmpeg

For video downloads, Downora downloads the Windows x64 LGPL shared build from the FFmpeg-Builds project (a Windows build provider linked by the official FFmpeg download page). The ZIP asset is pinned to a specific upstream release and verified against its GitHub SHA-256 asset digest before the executables and required DLLs are extracted. FFmpeg runs as a separate process and is not bundled into the Downora installer.

- FFmpeg project and license information: https://ffmpeg.org/legal.html
- Official Windows build-provider links: https://ffmpeg.org/download.html
- Build project and source: https://github.com/FFmpeg-Builds/FFmpeg-Builds
- Pinned build release: https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/autobuild-2026-11-26-12-56
- Selected asset: `ffmpeg-n9.0.2-982-g836df4e858-win64-lgpl-shared-9.0.zip`

The selected archive is the LGPL variant. Review the included upstream license files and build configuration when redistributing or modifying this runtime.
