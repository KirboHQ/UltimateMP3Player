# Draws the "Highlights" cards of the READMEs as SVG, in English and Italian, for GitHub's light and dark theme, two
# per row for computers and one per row for phones: assets\readme\highlights-<en|it>-<light|dark>[-mobile].svg.
# Text is wrapped here (measured with Segoe UI, with room to spare for the Mac and Linux fonts), so the cards have the
# same padding on every side and the same height on each row.
# Run: powershell -File .\tools\make-readme-cards.ps1
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'assets\readme'
New-Item -ItemType Directory -Force $out | Out-Null

# ------------------------------------------------------------------ content

$cards = @{
    en = @(
        @{ Icon = 'link'; Title = 'Paste a link, get the music'
           Text = 'Songs, albums and whole playlists from hundreds of sites, in their original order into a playlist of yours, or found with the online search. Songs you already have are never added twice.' }
        @{ Icon = 'cloud'; Title = 'Songs in the cloud'
           Text = 'Add a whole playlist in seconds without downloading it: the songs keep cover, lyrics and statistics and play from their link. Save on your device the ones you want offline.' }
        @{ Icon = 'headphones'; Title = 'A player that feels like Spotify'
           Text = "Waveform seek bar, fair shuffle, crossfade, speed from 0.5× to 2×, loudness normalization, a 10-band equalizer, media keys and the system's media controls." }
        @{ Icon = 'mic'; Title = 'Synced lyrics'
           Text = 'Found by themselves for your songs: the line being sung lights up over the blurred cover, a click jumps there, and over a music video they turn into subtitles.' }
        @{ Icon = 'queue'; Title = 'A queue that fills itself'
           Text = 'Start a song and the next ones from its playlist line up by themselves, topped up after every song. Or play similar songs found online, ready just before their turn.' }
        @{ Icon = 'vinyl'; Title = 'DJ mode'
           Text = 'Two decks with scrolling waveforms, BPM detection, sync, key lock, a 3-band EQ with kills and a crossfader. Record your mix straight into the library.' }
        @{ Icon = 'tag'; Title = 'Playlists, favorites and tags'
           Text = 'Colored tags like Discord roles, filters, multi-select in every list, listening statistics, and .ump packs to share your playlists with friends.' }
        @{ Icon = 'devices'; Title = 'On all your devices'
           Text = 'Windows, Linux, macOS and Android, with the same look. A profile for each person, themes, English and Italian, a 3D tilting cover and Discord Rich Presence.' }
        @{ Icon = 'together'; Title = 'Listen together'; Wide = $true
           Text = 'Rooms on your network (or over Radmin VPN): everyone hears the same song at the same moment, with a chat, a shared queue and permissions decided by the host. Songs are downloaded by each computer from their link, or sent over by someone in the room.' }
    )
    it = @(
        @{ Icon = 'link'; Title = 'Incolli un link, hai la musica'
           Text = 'Brani, album e playlist intere da centinaia di siti, nello stesso ordine in una tua playlist, o trovati con la ricerca online. I brani che hai già non vengono mai aggiunti due volte.' }
        @{ Icon = 'cloud'; Title = 'Brani nel cloud'
           Text = 'Aggiungi una playlist intera in pochi secondi senza scaricarla: i brani hanno copertina, testo e statistiche e si ascoltano dal loro link. Salva sul dispositivo quelli che vuoi offline.' }
        @{ Icon = 'headphones'; Title = 'Un lettore come Spotify'
           Text = "Forma d'onda per spostarsi nel brano, casuale vero, dissolvenza, velocità da 0,5× a 2×, volume normalizzato, equalizzatore a 10 bande, tasti e controlli multimediali del sistema." }
        @{ Icon = 'mic'; Title = 'Testi sincronizzati'
           Text = 'Trovati da soli per i tuoi brani: la riga cantata si illumina sulla copertina sfocata, un clic ci salta, e sopra un video diventano sottotitoli.' }
        @{ Icon = 'queue'; Title = 'Una coda che si riempie da sola'
           Text = 'Avvii un brano e i successivi della sua playlist si mettono in fila da soli, rimpiazzati a ogni brano. Oppure fai partire brani simili trovati online, pronti poco prima del loro turno.' }
        @{ Icon = 'vinyl'; Title = 'Modalità DJ'
           Text = "Due tracce con forme d'onda scorrevoli, BPM rilevati, sync, key lock, EQ a 3 bande con kill e crossfader. Registra il mix direttamente nella libreria." }
        @{ Icon = 'tag'; Title = 'Playlist, preferiti e tag'
           Text = 'Tag colorati come i ruoli di Discord, filtri, selezione multipla in ogni lista, statistiche di ascolto e pacchetti .ump per condividere le playlist con gli amici.' }
        @{ Icon = 'devices'; Title = 'Su tutti i tuoi dispositivi'
           Text = 'Windows, Linux, macOS e Android, con lo stesso aspetto. Un profilo per persona, temi, italiano e inglese, copertina 3D e stato su Discord.' }
        @{ Icon = 'together'; Title = 'Ascolta insieme'; Wide = $true
           Text = "Stanze sulla tua rete (o tramite Radmin VPN): tutti sentono lo stesso brano nello stesso momento, con la chat, una coda condivisa e i permessi decisi dall'host. Ogni computer scarica i brani dal loro link, oppure glieli manda qualcuno nella stanza." }
    )
}

# 24×24 line icons, drawn white on the gradient tile.
$icons = @{
    link       = '<path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1"/><path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1"/>'
    headphones = '<path d="M4 15v-3a8 8 0 0 1 16 0v3"/><rect x="3" y="14" width="4.5" height="7" rx="1.5"/><rect x="16.5" y="14" width="4.5" height="7" rx="1.5"/>'
    queue      = '<path d="M4 6h12M4 11h12M4 16h7"/><path d="M15 14.5v6l5-3z" fill="#fff"/>'
    vinyl      = '<circle cx="10.5" cy="13" r="7.5"/><circle cx="10.5" cy="13" r="2" fill="#fff"/><path d="M20.5 3v7.5l-3.5 3.5"/>'
    tag        = '<path d="M3 4.5v6.8a1.5 1.5 0 0 0 .44 1.06l8.2 8.2a1.5 1.5 0 0 0 2.12 0l6.8-6.8a1.5 1.5 0 0 0 0-2.12l-8.2-8.2A1.5 1.5 0 0 0 11.3 3H4.5A1.5 1.5 0 0 0 3 4.5z"/><circle cx="8" cy="8" r="1.4" fill="#fff"/>'
    people     = '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20.5a6.5 6.5 0 0 1 13 0"/><path d="M16 4.6a3.5 3.5 0 0 1 0 6.8"/><path d="M21.5 20.5a6.5 6.5 0 0 0-3.5-5.8"/>'
    cloud      = '<path d="M5.75 20a5 5 0 0 1-.63-9.96A7.5 7.5 0 0 1 18.88 8.75a5.63 5.63 0 0 1-.63 11.25z"/>'
    mic        = '<rect x="9" y="2.5" width="6" height="11.5" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/>'
    devices    = '<rect x="2" y="5" width="11" height="8.5" rx="1.5"/><path d="M1 17h13"/><rect x="16.5" y="8" width="6" height="12" rx="1.5"/>'
    together   = '<circle cx="12" cy="12" r="2" fill="#fff"/><path d="M8.5 8.5a5 5 0 0 0 0 7M15.5 8.5a5 5 0 0 1 0 7M5.4 5.4a9.3 9.3 0 0 0 0 13.2M18.6 5.4a9.3 9.3 0 0 1 0 13.2"/>'
}

# GitHub's own greys, so the cards sit well on its pages.
$themes = @{
    light = @{ Card = '#f6f8fa'; Border = '#d1d9e0'; Title = '#1f2328'; Text = '#59636e'; Tint = 0.06 }
    dark  = @{ Card = '#151b23'; Border = '#30363d'; Title = '#f0f6fc'; Text = '#9198a1'; Tint = 0.10 }
}

# ------------------------------------------------------------------ layout

$Width = 880; $Gap = 16; $Pad = 26; $Tile = 44; $Radius = 16
$TitleSize = 17; $TextSize = 14.5; $Line = 23
$Font = "'Segoe UI', -apple-system, BlinkMacSystemFont, 'Helvetica Neue', Helvetica, Arial, sans-serif"
# Lines are cut at this share of the room: Segoe UI is measured, other systems' fonts are a little wider.
$Fill = 0.93

$family = New-Object Windows.Media.FontFamily('Segoe UI')
$regular = New-Object Windows.Media.Typeface($family, [Windows.FontStyles]::Normal, [Windows.FontWeights]::Normal, [Windows.FontStretches]::Normal)

function TextWidth([string]$s) {
    $ft = New-Object Windows.Media.FormattedText($s, [Globalization.CultureInfo]::InvariantCulture, [Windows.FlowDirection]::LeftToRight,
        $regular, $TextSize, [Windows.Media.Brushes]::Black)
    $ft.WidthIncludingTrailingWhitespace
}

function Wrap([string]$text, [double]$width) {
    $lines = WrapAt $text $width
    # No last line of a lone word: words come down from the line before until it looks like a line.
    $n = $lines.Count
    while ($n -gt 1 -and (TextWidth $lines[$n - 1]) -lt $width * $Fill * 0.3) {
        $words = $lines[$n - 2] -split ' '
        if ($words.Count -lt 4) { break }
        $lines[$n - 2] = ($words[0..($words.Count - 2)]) -join ' '
        $lines[$n - 1] = $words[-1] + ' ' + $lines[$n - 1]
    }
    return , $lines
}

function WrapAt([string]$text, [double]$width) {
    $lines = New-Object Collections.Generic.List[string]
    $cur = ''
    foreach ($word in $text -split ' ') {
        $try = if ($cur) { "$cur $word" } else { $word }
        if ($cur -and (TextWidth $try) -gt $width * $Fill) { $lines.Add($cur); $cur = $word }
        else { $cur = $try }
    }
    if ($cur) { $lines.Add($cur) }
    return , $lines
}

function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }

function CardHeight([int]$lines) { $FirstBaseline + ($lines - 1) * $Line + 6 + $Pad }

function Card($c, [double]$x, [double]$y, [double]$w, [double]$h, [string[]]$lines, $t) {
    $s = New-Object Text.StringBuilder
    [void]$s.Append("  <g transform=`"translate($x $y)`">`n")
    [void]$s.Append("    <rect x=`"0.5`" y=`"0.5`" width=`"$($w - 1)`" height=`"$($h - 1)`" rx=`"$Radius`" fill=`"$($t.Card)`" stroke=`"$($t.Border)`"/>`n")
    if ($c.Wide) {
        # The flagship one: tinted with the app's colours, gradient border.
        [void]$s.Append("    <rect x=`"0.5`" y=`"0.5`" width=`"$($w - 1)`" height=`"$($h - 1)`" rx=`"$Radius`" fill=`"url(#accent)`" fill-opacity=`"$($t.Tint)`" stroke=`"url(#accent)`" stroke-opacity=`"0.7`"/>`n")
    }
    [void]$s.Append("    <rect x=`"$Pad`" y=`"$Pad`" width=`"$Tile`" height=`"$Tile`" rx=`"12`" fill=`"url(#accent)`"/>`n")
    $io = $Pad + ($Tile - 24) / 2
    [void]$s.Append("    <g transform=`"translate($io $io)`" fill=`"none`" stroke=`"#fff`" stroke-width=`"2`" stroke-linecap=`"round`" stroke-linejoin=`"round`">$($icons[$c.Icon])</g>`n")
    $ty = $Pad + $Tile / 2 + 6
    [void]$s.Append("    <text class=`"title`" x=`"$($Pad + $Tile + 16)`" y=`"$ty`">$(Esc $c.Title)</text>`n")
    [void]$s.Append("    <text class=`"body`" x=`"$Pad`" y=`"$FirstBaseline`">")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $dy = if ($i -eq 0) { 0 } else { $Line }
        [void]$s.Append("<tspan x=`"$Pad`" dy=`"$dy`">$(Esc $lines[$i])</tspan>")
    }
    [void]$s.Append("</text>`n  </g>`n")
    $s.ToString()
}

# Two layouts: two cards side by side for computers, one per row for phones (the 880-wide image shrunk to a phone's
# width made the text tiny). The README's <picture> picks one by the screen's width.
$layouts = @(
    @{ Suffix = ''; Width = 880; Columns = 2; Pad = 26 }
    @{ Suffix = '-mobile'; Width = 380; Columns = 1; Pad = 22 }
)
foreach ($layout in $layouts) {
    $Width = $layout.Width; $Pad = $layout.Pad; $cols = $layout.Columns
    $FirstBaseline = $Pad + $Tile + 18 + 11
    $colWidth = ($Width - ($cols - 1) * $Gap) / $cols
    foreach ($lang in 'en', 'it') {
        $list = $cards[$lang]
        # Rows: the cards side by side (same height), the wide one alone.
        $rows = @()
        $pending = @()
        foreach ($c in $list) {
            if ($c.Wide) { $rows += , @($c); continue }
            $pending += $c
            if ($pending.Count -eq $cols) { $rows += , $pending; $pending = @() }
        }
        if ($pending.Count) { $rows += , $pending }

        foreach ($theme in 'light', 'dark') {
            $t = $themes[$theme]
            $body = New-Object Text.StringBuilder
            $y = 0
            foreach ($row in $rows) {
                $w = if ($row.Count -eq 1 -and $row[0].Wide) { $Width } else { $colWidth }
                $wrapped = New-Object Collections.Generic.List[object]
                $h = 0
                foreach ($c in $row) {
                    $lines = Wrap $c.Text ($w - 2 * $Pad)
                    $wrapped.Add($lines)
                    $h = [Math]::Max($h, (CardHeight $lines.Count))
                }
                for ($i = 0; $i -lt $row.Count; $i++) {
                    [void]$body.Append((Card $row[$i] ($i * ($colWidth + $Gap)) $y $w $h $wrapped[$i] $t))
                }
                $y += $h + $Gap
            }
            $Height = $y - $Gap
            $label = Esc (($list | ForEach-Object { $_.Title }) -join ' · ')
            $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="$Width" height="$Height" viewBox="0 0 $Width $Height" role="img" aria-label="$label">
  <defs>
    <linearGradient id="accent" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#7C5CFF"/>
      <stop offset="1" stop-color="#FF4FA3"/>
    </linearGradient>
  </defs>
  <style>
    .title { font: 600 ${TitleSize}px $Font; fill: $($t.Title); }
    .body { font: 400 ${TextSize}px $Font; fill: $($t.Text); }
  </style>
$($body.ToString())</svg>
"@
            $file = Join-Path $out "highlights-$lang-$theme$($layout.Suffix).svg"
            [IO.File]::WriteAllText($file, $svg, (New-Object Text.UTF8Encoding($false)))
            Write-Host "$file  ($Width x $Height)"
        }
    }
}
