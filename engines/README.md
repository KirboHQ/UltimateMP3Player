# engines

External programs used by the app and bundled by the installer. They are not in git (too big).

| File | From |
|---|---|
| `yt-dlp.exe` | https://github.com/yt-dlp/yt-dlp/releases/latest |
| `gallery-dl.exe` | https://codeberg.org/mikf/gallery-dl/releases |
| `ffmpeg.exe`, `ffprobe.exe` + DLLs | https://github.com/yt-dlp/FFmpeg-Builds/releases (`ffmpeg-master-latest-win64-gpl-shared.zip`, the `bin` folder) |
| `deno.exe` | https://github.com/denoland/deno/releases/latest (`deno-x86_64-pc-windows-msvc.zip`) |

Shortcut: run the app once without them; it downloads whatever is missing into its own `engines` folder, which you can copy here.
