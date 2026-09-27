<p align="center"><img src="assets/logo.png" width="112" alt="Ultimate MP3 Player"></p>

<h1 align="center">Ultimate MP3 Player</h1>

<p align="center">An offline, ad-free music player for Windows that downloads songs and whole playlists from YouTube Music,
Spotify, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp and hundreds of other sites.</p>

<p align="center"><b>English</b> · <a href="README.it.md">Italiano</a></p>

## Download

Get **`UltimateMP3Player-Setup-x.y.z.exe`** from the [latest release](../../releases/latest) and run it: no administrator
rights and nothing else to install. The setup asks for the language (English or Italian, changeable later in Settings).
The app then updates itself from the releases.

> Windows SmartScreen may say the app is unrecognized, because it is not signed with a paid certificate:
> click **More info → Run anyway**.

## Features

- **Paste a link** (or drag it into the window): songs, albums and playlists, saved into a playlist of yours in the
  original order, optionally with the video. Songs you already have are never downloaded twice.
- **Spotify-like player**: waveform seek bar, fair shuffle, repeat, crossfade, loudness normalization (-14 LUFS),
  10-band equalizer with presets and a live response curve, media keys and Windows media controls.
- **Up next** you can edit (drag to reorder, jump, remove) and that is restored when you reopen the app.
- **Profiles** (like Chrome): playlists, favorites, history, equalizer and theme per person; songs are shared.
- **Themes**, English/Italian, light animations, smooth scrolling, 3D tilting cover, video with a blurred backdrop.
- **Discord Rich Presence**: song, artist, cover and progress bar on your Discord profile.
- **Bulk edit** of artist names, a "Not in a playlist" view to tidy up, right-click menus everywhere.
- Downloads handle site **rate limits** by themselves: they pause, then resume more slowly.
- Closing the window keeps the music playing in the tray with very little memory.

## Build

Requirements: .NET 8 SDK, and for the installer Inno Setup 6. Put the external programs in `engines\` (see
[engines/README.md](engines/README.md)).

| Command | Result |
|---|---|
| `.\build.ps1` | Portable app in `app\` and a Desktop shortcut |
| `.\build.ps1 -Installer` | `installer\Output\UltimateMP3Player-Setup-<version>.exe` |
| `.\build.ps1 -Release` | `dist\`: the setup to attach to a GitHub release (plus `UltimateMP3Player.exe`, optional, for lighter updates) |

Two settings in `src\UltimateMP3Player\UltimateMP3Player.csproj`:

- `<GitHubRepo>owner/repo</GitHubRepo>`: where the app looks for updates. The latest release must contain the setup;
  if it also contains `UltimateMP3Player.exe`, updates download only that (70 MB instead of the whole setup).
- `<DiscordAppId>…</DiscordAppId>`: the Discord application used for Rich Presence.

`UMP_DATA=<folder>` runs a test copy with its own data next to the normal one. `src\Mp3Cli` is a command line harness
for the core (downloads, import, queue, shuffle).

| Folder | Content |
|---|---|
| `src\UltimateMP3Player.Core` | Downloads (yt-dlp, gallery-dl, ffmpeg), library, profiles, queue, analysis, translations |
| `src\UltimateMP3Player` | WPF interface, audio engine (NAudio/WASAPI), tray, media keys, updater, Discord |
| `installer` | Inno Setup script and wizard images (`tools\make-images.ps1` renders them from `Logo.xaml`) |

## Data

| Path | Content |
|---|---|
| `Music\Ultimate MP3 Player` | Downloaded songs (MP3/M4A with tags and cover) and videos |
| `%LOCALAPPDATA%\Ultimate MP3 Player` | Library, covers, profiles, settings, `errori.log` |

## Third-party software

[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), [gallery-dl](https://github.com/mikf/gallery-dl) (GPL-2.0),
[FFmpeg](https://ffmpeg.org) (GPL, builds from [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)),
[Deno](https://deno.com) (MIT), [NAudio](https://github.com/naudio/NAudio) (MIT).
Download only content you have the right to download.
