<p align="center">
  <img src="assets/logo.png" width="128" alt="Ultimate MP3 Player logo">
</p>

<h1 align="center">Ultimate MP3 Player</h1>

<p align="center">
  <b>Your music, offline and ad-free.</b><br>
  Paste a link from YouTube Music, Spotify, Apple Music, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp or
  hundreds of other sites: the songs land in your library with covers and tags, ready to play.
</p>

<p align="center">
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/KirboHQ/UltimateMP3Player?style=flat-square&color=7C5CFF&label=version"></a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/KirboHQ/UltimateMP3Player/total?style=flat-square&color=FF4FA3"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows&logoColor=white">
  <img alt="Linux" src="https://img.shields.io/badge/Linux-x64%20%7C%20arm64-FCC624?style=flat-square&logo=linux&logoColor=black">
  <img alt="macOS 12 or later" src="https://img.shields.io/badge/macOS-12%2B-000000?style=flat-square&logo=apple&logoColor=white">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white">
</p>

<p align="center">
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Download for Windows" src="https://img.shields.io/badge/Download_for_Windows-7C5CFF?style=for-the-badge&logo=windows&logoColor=white" height="42">
  </a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Download for Linux" src="https://img.shields.io/badge/Linux-7C5CFF?style=for-the-badge&logo=linux&logoColor=white" height="42">
  </a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Download for macOS" src="https://img.shields.io/badge/macOS-7C5CFF?style=for-the-badge&logo=apple&logoColor=white" height="42">
  </a>
</p>

<p align="center"><b>English</b> · <a href="README.it.md">Italiano</a></p>

---

## ✨ Highlights

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/readme/highlights-en-dark.svg">
  <img src="assets/readme/highlights-en-light.svg" width="100%" alt="Highlights: paste a link, get the music · a player that feels like Spotify · a queue that fills itself · DJ mode · playlists, favorites and tags · profiles and themes · listen together">
</picture>

## 🆕 What's new in 3.4.0

- **Linux and macOS**: the same app, with the same look and the same features, on Linux (x64 and arm64) and macOS
  (Intel and Apple Silicon). Media controls of the system, tray icon, `.ump` packs, automatic updates included.
- **Statistics**: a play counts once you've heard 75 % of the song (at 2× speed half the time is enough), time listened
  counts from the first second, and both update live while you listen. *Select the never played* works again from any
  sort and filter.
- **Windows media controls**: the app shows its name and icon instead of *Unknown app*
- A suggested song saved and then deleted while it plays doesn't skip any more: it finishes from the cache
- The Settings search box always stays at the top
- Dialogs (like the terms of use): the buttons no longer flicker under the mouse

## 📥 Install

**Windows**

1. Download **`UltimateMP3Player-Setup-x.y.z.exe`** from the [latest release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Run it: no administrator rights and nothing else to install. It asks for your language (English or Italiano,
   you can change it later in Settings).
3. That's it: the app keeps itself up to date from the releases.

> [!NOTE]
> Windows SmartScreen may say the app is unrecognized because it isn't signed with a paid certificate:
> click **More info → Run anyway**.

**Linux** (x64 or arm64, any recent distribution with a desktop)

1. Download **`UltimateMP3Player-linux-x64.tar.gz`** (or `-arm64`) from the
   [latest release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Extract it to a folder of yours (not a system one like `/opt`: the app updates itself there), for example:
   ```sh
   mkdir -p ~/.local/opt
   tar -xzf UltimateMP3Player-linux-x64.tar.gz -C ~/.local/opt
   ```
3. Start it once with `~/.local/opt/UltimateMP3Player/UltimateMP3Player`: from then on it's in the applications menu,
   and `.ump` packs open with it. On the first start it downloads yt-dlp, ffmpeg and the other engines by itself.

> [!NOTE]
> On plain GNOME the tray icon needs the *AppIndicator* extension (Ubuntu has it already); without it, closing the
> window quits the app. If the app doesn't start and mentions ICU, install your distribution's `libicu` package.

**macOS** (12 Monterey or later)

1. Download **`UltimateMP3Player-macos-arm64.zip`** for Apple Silicon (M1 and later) or `-x64` for Intel Macs from the
   [latest release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Open the zip and drag **Ultimate MP3 Player** into *Applications*.
3. The first time, macOS says it can't check the app because it isn't signed with a paid Apple certificate: open
   **System Settings → Privacy & Security** and click **Open Anyway** (on older versions: right-click the app →
   **Open**).

> [!NOTE]
> If macOS says the app "is damaged", run once in the Terminal:
> `xattr -dr com.apple.quarantine "/Applications/Ultimate MP3 Player.app"`

## 🎵 Features

**Downloads**
- Links from YouTube Music, Spotify, Apple Music, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp and
  hundreds of other sites, powered by yt-dlp and gallery-dl
- MP3 at 320 kbps or the original format, videos up to 4K, several downloads at a time
- Duplicates are recognized (same source or same song), and site rate limits are handled by themselves: downloads
  pause, then resume more gently
- Optional browser cookies for private playlists and age-restricted content
- Music you already have: drag files or folders into the window

**Player**
- Waveform seek bar, fair shuffle, repeat all / repeat one, crossfade from 1 to 12 seconds
- Loudness normalization (−14 LUFS) and a 10-band equalizer with presets and a live response curve
- Media keys, the system's media controls (Windows, MPRIS on Linux, Now Playing on macOS) and the tray: close the
  window and the music keeps playing with very little memory
- Songs with a video show it in the song screen, framed on a blurred copy of itself

**Up next**
- Drag to reorder, double-click to jump, drop on the bin to remove
- Automatic queue, *Generate* and *Clear*, a panel you can hide to the side
- Similar songs found online after a song: downloaded just before their turn (how many ahead is up to you), kept with
  a right-click
- Restored exactly where you left it when you reopen the app

**Library**
- Playlists with custom covers, Favorites, recently played, and a *Not in a playlist* view to tidy up
- Tags with colors, multi-tag filters (all / any) and `#tag` in the search box
- Rename an artist on all their songs at once; edit title, artist, album, BPM and cover
- Listening statistics per profile: plays, time listened and last played of every song, the least played to clean up

**DJ**
- Two decks with scrolling waveforms and overviews, CUE, nudge, tempo range ±8 / 16 / 50 %, key lock
- A song browser for each deck: library, playlists, tags, the song playing or a file
- Automatic BPM detection, TAP on each deck (also with the T key), beat grid and SYNC of tempo and phase
- Mixer with 3-band EQ and kills, channel faders and crossfader
- Record the mix and save it as an MP3 in your library, even into a new playlist

**Listen together**
- Rooms found by themselves on the same network (Wi-Fi or cable); from afar, everyone joins the same Radmin VPN
  network. Rooms can have a password and a maximum number of people; you can also join by address.
- Everyone hears the same song at the same point: the host's clock leads, each computer follows it (volume and
  equalizer stay your own)
- Each computer gets the playing song and the next two: from its library, from the room cache, from the song's link,
  or sent by someone in the room. A song starts when everyone has it (or after a few seconds for those who have it);
  late ones join in at the right point.
- The host decides who can add, remove and move, skip, pause, and seek; greyed-out buttons for the others. In a room
  the play buttons of your lists become **+**, and a double click adds the song to the room.
- Chat, who added each song, everyone's download progress, kick, hand over the host. If the host leaves or their PC
  goes off, the room passes to the person with the most permissions (on a tie, who joined first).
- Songs heard in rooms stay in a cache (10 by default, shared with the suggested songs); save the ones you like to
  your library or a playlist
- P2P can be switched off in Settings: songs then come only from your library, the cache or their link

> [!TIP]
> The first time, Windows asks whether the app may use the network: allow it. With Radmin VPN also tick *Public
> networks*, or use **Allow through the firewall** in the app. Radmin VPN exists only for Windows: with friends on
> Linux or macOS use ZeroTier or Tailscale, which work everywhere (also on Windows).

**And also**
- Profiles like in Chrome, themes, English and Italian, smooth animations and scrolling
- Discord Rich Presence with song, artist, cover and progress bar
- Automatic updates from the GitHub releases

## 💬 Help & feedback

Found a bug or have an idea? Open an [issue](https://github.com/KirboHQ/UltimateMP3Player/issues), or use
**Settings → Help & support** in the app: it fills in the app and Windows versions for you.

## 🛠️ Build from source

<details>
<summary>Requirements, commands and project layout</summary>
<br>

Requirements: .NET 8 SDK, and for the installer Inno Setup 6. Put the external programs in `engines\` (see
[engines/README.md](engines/README.md)).

| Command | Result |
|---|---|
| `.\build.ps1` | Portable app in `app\` and a Desktop shortcut |
| `.\build.ps1 -Installer` | `installer\Output\UltimateMP3Player-Setup-<version>.exe` |
| `.\build.ps1 -Release` | `dist\`: the files to attach to a GitHub release: the setup (plus `UltimateMP3Player.exe`, optional, for lighter updates) and the Linux and macOS packages (`-WindowsOnly` skips them) |
| `.\build-unix.ps1` | Only the Linux and macOS packages in `dist\`: `UltimateMP3Player-linux-x64.tar.gz`, `-linux-arm64.tar.gz`, `UltimateMP3Player-macos-x64.zip`, `-macos-arm64.zip` (`-Targets linux-x64,…` for some of them) |

The Linux and macOS packages are made on Windows too. The macOS app is signed "ad hoc" (without an Apple certificate)
with [rcodesign](https://github.com/indygreg/apple-platform-rs), downloaded once into `tools\bin`: Apple Silicon Macs
don't run apps without any signature. Keep the version in `src\UltimateMP3Player.Avalonia\UltimateMP3Player.Avalonia.csproj`
the same as the Windows one.

Two settings in `src\UltimateMP3Player\UltimateMP3Player.csproj` (and the same two in the Avalonia project):

- `<GitHubRepo>owner/repo</GitHubRepo>`: where the app looks for updates and opens issues. The latest release must
  contain the setup; if it also contains `UltimateMP3Player.exe`, updates download only that (70 MB instead of the
  whole setup).
- `<DiscordAppId>…</DiscordAppId>`: the Discord application used for Rich Presence.

`UMP_DATA=<folder>` runs a test copy with its own data next to the normal one. `src\Mp3Cli` is a command line harness
for the core (downloads, import, queue, shuffle).

| Folder | Content |
|---|---|
| `src\UltimateMP3Player.Core` | Downloads (yt-dlp, gallery-dl, ffmpeg), library, profiles, queue, analysis, translations |
| `src\UltimateMP3Player` | WPF interface, audio engine (NAudio/WASAPI), DJ engine (SoundTouch), tray, media keys, updater, Discord |
| `src\UltimateMP3Player.Avalonia` | Linux and macOS: the same interface drawn with Avalonia; it shares the view models, the audio and DJ engines and most services with `src\UltimateMP3Player` (audio out through SDL3, MPRIS, macOS Now Playing, updater) |
| `installer` | Inno Setup script and wizard images (`tools\make-images.ps1` renders them, and the macOS icons, from `Logo.xaml`) |

</details>

## 📁 Where your data lives

| Path | Content |
|---|---|
| `Music\Ultimate MP3 Player` | Downloaded songs (MP3/M4A with tags and cover) and videos |
| `%LOCALAPPDATA%\Ultimate MP3 Player` | Library, covers, profiles, settings, `errori.log` |
| `%LOCALAPPDATA%\Ultimate MP3 Player\ascolta-insieme` | Songs of *Listen together* rooms and suggested songs kept for next time |

On Linux the data is in `~/.local/share/Ultimate MP3 Player` and on macOS in `~/Library/Application Support/Ultimate MP3 Player`
(there also the engines, in `engines`); the songs go to `~/Music/Ultimate MP3 Player`.

## 🙏 Third-party software

[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), [gallery-dl](https://github.com/mikf/gallery-dl) (GPL-2.0),
[FFmpeg](https://ffmpeg.org) (GPL, builds from [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds), on
Linux and macOS from [martin-riedl.de](https://ffmpeg.martin-riedl.de)),
[Deno](https://deno.com) (MIT), [NAudio](https://github.com/naudio/NAudio) (MIT),
[SoundTouch.Net](https://github.com/owoudenberg/soundtouch.net) (LGPL-2.1).
Linux and macOS: [Avalonia](https://avaloniaui.net) (MIT), [SDL3](https://www.libsdl.org) (zlib) through
[SDL3-CS](https://github.com/ppy/SDL3-CS) (MIT), [Tmds.DBus](https://github.com/tmds/Tmds.DBus) (MIT),
[Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT) and
[Selawik](https://github.com/microsoft/Selawik) (OFL-1.1), in place of Segoe UI and its icons.

<sub>Download only content you have the right to download.</sub>
