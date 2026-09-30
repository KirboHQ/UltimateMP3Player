<p align="center">
  <img src="assets/logo.png" width="128" alt="Ultimate MP3 Player logo">
</p>

<h1 align="center">Ultimate MP3 Player</h1>

<p align="center">
  <b>Your music, offline and ad-free.</b><br>
  Paste a link from YouTube Music, Spotify, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp or hundreds of
  other sites: the songs land in your library with covers and tags, ready to play.
</p>

<p align="center">
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/KirboHQ/UltimateMP3Player?style=flat-square&color=7C5CFF&label=version"></a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/KirboHQ/UltimateMP3Player/total?style=flat-square&color=FF4FA3"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows&logoColor=white">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white">
</p>

<p align="center">
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Download for Windows" src="https://img.shields.io/badge/Download_for_Windows-7C5CFF?style=for-the-badge&logo=windows&logoColor=white" height="42">
  </a>
</p>

<p align="center"><b>English</b> · <a href="README.it.md">Italiano</a></p>

---

## ✨ Highlights

<table>
<tr>
<td width="50%" valign="top">

### 🔗 Paste a link, get the music
Songs, albums and whole playlists, saved in their original order into a playlist of yours, with the video if you
want it. Songs you already have are never downloaded twice.

</td>
<td width="50%" valign="top">

### 🎧 A player that feels like Spotify
Waveform seek bar, fair shuffle, crossfade, loudness normalization, a 10-band equalizer, media keys and Windows
media controls.

</td>
</tr>
<tr>
<td valign="top">

### ♾️ A queue that fills itself
Start a song and the next ones from its playlist line up by themselves (up to 50), topped up after every song. Or
switch it off and play one song at a time.

</td>
<td valign="top">

### 🎛️ DJ mode
Two decks with scrolling waveforms, BPM detection, sync, key lock, a 3-band EQ with kills and a crossfader. Record
your mix straight into the library.

</td>
</tr>
<tr>
<td valign="top">

### 🏷️ Playlists, favorites and tags
Colored tags like Discord roles, filters, a page for every tag, bulk edits and right-click menus everywhere.

</td>
<td valign="top">

### 👥 Profiles and themes
One profile per person with their own playlists, history, equalizer and theme; songs are shared. English and
Italian, a 3D tilting cover and Discord Rich Presence.

</td>
</tr>
<tr>
<td colspan="2" valign="top">

### 🎉 Listen together
Rooms on your network (or over Radmin VPN): everyone hears the same song at the same moment, with a chat, a shared
queue and permissions decided by the host. Songs are downloaded by each computer from their link, or sent over by
someone in the room.

</td>
</tr>
</table>

## 🆕 What's new in 3.0.0

- **Listen together**: create a room or join one found on the network; synced playback, chat, shared queue, the host
  decides who can add, remove, skip, pause or seek (and can kick people). If the host leaves, the room moves on to
  someone else. Songs you like can be saved to your library in one click.
- **DJ**: a new song browser (covers, BPM, playlists and tags, a clear selection) and **TAP** on both decks
- **Smooth scrolling** rewritten after Firefox's: one continuous glide, in every list, menu and window
- **Discord**: covers that didn't show (huge SoundCloud artwork, missing YouTube thumbnails) now do, with the app
  icon as a fallback
- The sidebar splits its room: playlists two thirds, tags one third, both shrinking with the window

## 📥 Install

1. Download **`UltimateMP3Player-Setup-x.y.z.exe`** from the [latest release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Run it: no administrator rights and nothing else to install. It asks for your language (English or Italiano,
   you can change it later in Settings).
3. That's it: the app keeps itself up to date from the releases.

> [!NOTE]
> Windows SmartScreen may say the app is unrecognized because it isn't signed with a paid certificate:
> click **More info → Run anyway**.

## 🎵 Features

**Downloads**
- Links from YouTube Music, Spotify, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp and hundreds of other
  sites, powered by yt-dlp and gallery-dl
- MP3 at 320 kbps or the original format, videos up to 4K, several downloads at a time
- Duplicates are recognized (same source or same song), and site rate limits are handled by themselves: downloads
  pause, then resume more gently
- Optional browser cookies for private playlists and age-restricted content
- Music you already have: drag files or folders into the window

**Player**
- Waveform seek bar, fair shuffle, repeat all / repeat one, crossfade from 1 to 12 seconds
- Loudness normalization (−14 LUFS) and a 10-band equalizer with presets and a live response curve
- Media keys, Windows media controls and the tray: close the window and the music keeps playing with very little memory
- Songs with a video show it in the song screen, framed on a blurred copy of itself

**Up next**
- Drag to reorder, double-click to jump, drop on the bin to remove
- Automatic queue, *Generate* and *Clear*, a panel you can hide to the side
- Restored exactly where you left it when you reopen the app

**Library**
- Playlists with custom covers, Favorites, recently played, and a *Not in a playlist* view to tidy up
- Tags with colors, multi-tag filters (all / any) and `#tag` in the search box
- Rename an artist on all their songs at once; edit title, artist, album, BPM and cover

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
- Songs heard in rooms stay in a cache (10 by default); save the ones you like to your library or a playlist

> [!TIP]
> The first time, Windows asks whether the app may use the network: allow it. With Radmin VPN also tick *Public
> networks*, or use **Allow through the firewall** in the app.

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
| `.\build.ps1 -Release` | `dist\`: the setup to attach to a GitHub release (plus `UltimateMP3Player.exe`, optional, for lighter updates) |

Two settings in `src\UltimateMP3Player\UltimateMP3Player.csproj`:

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
| `installer` | Inno Setup script and wizard images (`tools\make-images.ps1` renders them from `Logo.xaml`) |

</details>

## 📁 Where your data lives

| Path | Content |
|---|---|
| `Music\Ultimate MP3 Player` | Downloaded songs (MP3/M4A with tags and cover) and videos |
| `%LOCALAPPDATA%\Ultimate MP3 Player` | Library, covers, profiles, settings, `errori.log` |
| `%LOCALAPPDATA%\Ultimate MP3 Player\ascolta-insieme` | Songs of *Listen together* rooms kept for next time |

## 🙏 Third-party software

[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), [gallery-dl](https://github.com/mikf/gallery-dl) (GPL-2.0),
[FFmpeg](https://ffmpeg.org) (GPL, builds from [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)),
[Deno](https://deno.com) (MIT), [NAudio](https://github.com/naudio/NAudio) (MIT),
[SoundTouch.Net](https://github.com/owoudenberg/soundtouch.net) (LGPL-2.1).

<sub>Download only content you have the right to download.</sub>
