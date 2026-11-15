# README screenshots, step 1: a demo data folder, nothing of yours in it. The NCS songs of songs.json from their Deezer links
# (cover, lyrics, waveform), then the profile "Alex" (seed\Program.cs). Needs src\Mp3Cli built (Debug).
#   .\tools\readme-shots\demo.ps1 [-Folder <path>]      (default %TEMP%\ump-readme; the steps after it take the same folder)
param([string]$Folder = (Join-Path $env:TEMP "ump-readme"))
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")
$cli = Join-Path $repo "src\Mp3Cli\bin\Debug\net8.0\Mp3Cli.exe"
if (-not (Test-Path $cli)) { throw "Build src\Mp3Cli first (Debug)." }
$dotnet = Join-Path $env:LOCALAPPDATA "dotnet-sdk\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

New-Item -ItemType Directory -Force (Join-Path $Folder "data"), (Join-Path $Folder "music") | Out-Null
$env:UMP_DATA = Join-Path $Folder "data"
foreach ($song in (Get-Content (Join-Path $here "songs.json") -Raw -Encoding UTF8 | ConvertFrom-Json)) {
    $result = & $cli download $song.link --out (Join-Path $Folder "music") 2>&1 | Select-String "Downloaded|ERRORE|ERROR"
    "$($song.artist) - $($song.title): $result"
}
& $dotnet run --project (Join-Path $here "seed\seed.csproj") -c Debug -v q -- $Folder
