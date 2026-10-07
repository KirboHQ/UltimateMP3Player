# The engines of the Android app, into src\UltimateMP3Player.Android\Engines (not in git: about 50 MB per processor).
# Python with yt-dlp's libraries, ffmpeg and QuickJS (YouTube's JavaScript) come from the youtubedl-android libraries on
# Maven Central (the ones of the Seal app, GPL-3.0 like their Termux packages), pinned by version and SHA-256; yt-dlp itself
# is the latest release (checked against its SHA2-256SUMS), the app updates it by itself afterwards.
# Run: powershell -File .\tools\android-deps.ps1   (-Force: download again)
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src\UltimateMP3Player.Android\Engines'
$cache = Join-Path $root 'tools\bin\android'
New-Item -ItemType Directory -Force $out, $cache | Out-Null

$version = '0.18.1'
$libs = @(
    @{ Name = 'library'; Sha = '579B5FB480892B1ABC2B218C2089699D52759CC8D7BA256BF876453F0365FAEF' },
    @{ Name = 'ffmpeg';  Sha = '0A87FFA6CF912B0FE76C1A99B9107F543EE2F247935FAE2C71F0822EB7BC5F49' }
)
$abis = 'arm64-v8a', 'armeabi-v7a', 'x86_64'
$stamp = Join-Path $out ".youtubedl-android-$version"

if ($Force -or -not (Test-Path $stamp)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($abi in $abis) {
        $d = Join-Path $out $abi
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
        New-Item -ItemType Directory -Force $d | Out-Null
    }
    foreach ($lib in $libs) {
        $aar = Join-Path $cache "$($lib.Name)-$version.aar"
        if (-not (Test-Path $aar) -or (Get-FileHash $aar -Algorithm SHA256).Hash -ne $lib.Sha) {
            Write-Host "Download di youtubedl-android $($lib.Name) $version..."
            Invoke-WebRequest "https://repo1.maven.org/maven2/io/github/junkfood02/youtubedl-android/$($lib.Name)/$version/$($lib.Name)-$version.aar" -OutFile $aar -UseBasicParsing
        }
        $hash = (Get-FileHash $aar -Algorithm SHA256).Hash
        if ($hash -ne $lib.Sha) { throw "SHA-256 di $($lib.Name)-$version.aar non corrisponde ($hash)" }
        $zip = [IO.Compression.ZipFile]::OpenRead($aar)
        try {
            foreach ($e in $zip.Entries) {
                $parts = $e.FullName.Split('/')
                if ($parts.Count -eq 3 -and $parts[0] -eq 'jni' -and $abis -contains $parts[1] -and $parts[2].EndsWith('.so')) {
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $out "$($parts[1])\$($parts[2])"), $true)
                }
            }
        }
        finally { $zip.Dispose() }
    }
    Set-Content -Path $stamp -Value $version
}

# yt-dlp: the platform-independent program (a zip that Python runs).
$ytdlp = Join-Path $out 'yt-dlp'
if ($Force -or -not (Test-Path $ytdlp) -or (Get-Item $ytdlp).LastWriteTime -lt (Get-Date).AddDays(-7)) {
    Write-Host 'Download di yt-dlp...'
    $base = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download'
    $tmp = "$ytdlp.part"
    Invoke-WebRequest "$base/yt-dlp" -OutFile $tmp -UseBasicParsing
    $sums = (Invoke-WebRequest "$base/SHA2-256SUMS" -UseBasicParsing).Content
    if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
    $line = ($sums -split "`n") | Where-Object { $_ -match '\s\*?yt-dlp\s*$' } | Select-Object -First 1
    if (-not $line) { throw 'SHA2-256SUMS di yt-dlp senza la riga di yt-dlp' }
    $want = ($line -split '\s+')[0]
    $got = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($got -ne $want) { Remove-Item $tmp; throw "SHA-256 di yt-dlp non corrisponde ($got)" }
    Move-Item -Force $tmp $ytdlp
}

Get-ChildItem $out -Recurse -File | Where-Object Name -ne ".youtubedl-android-$version" |
    Select-Object @{ n = 'File'; e = { $_.FullName.Substring($out.Length + 1) } }, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } }
