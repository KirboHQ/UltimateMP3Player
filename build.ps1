# Build script.
#   .\build.ps1               portable app in .\app plus a Desktop shortcut
#   .\build.ps1 -NoShortcut   same, without touching the Desktop
#   .\build.ps1 -Installer    only the installer: installer\Output\UltimateMP3Player-Setup-<version>.exe
#   .\build.ps1 -Release      the files for a GitHub release in .\dist: the installer (enough on its own),
#                             UltimateMP3Player.exe (optional: if attached, updates download 70 MB instead of the whole setup)
#                             the Linux and macOS packages (build-unix.ps1) and the Android APKs (build-android.ps1);
#                             -WindowsOnly skips both, -NoAndroid only the APKs
# -Installer and -Release first update yt-dlp/gallery-dl in .\engines and need Inno Setup 6.
# Engines are copied into .\app\engines only when missing, so versions updated by the app are kept.
param([switch]$NoShortcut, [switch]$Installer, [switch]$Release, [switch]$WindowsOnly, [switch]$NoAndroid)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'app'
$csproj = "$root\src\UltimateMP3Player\UltimateMP3Player.csproj"
# ReadyToRun: the code comes already compiled, so the start and the first pages don't wait for the JIT.
$publishArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:PublishReadyToRun=true', '-p:DebugType=none', '--nologo', '-v', 'q')

# A .NET 8 SDK from PATH, otherwise the local copy in %LOCALAPPDATA%\dotnet-sdk.
$dotnet = 'dotnet'
$hasSdk = $false
try { $hasSdk = [bool](& dotnet --list-sdks 2>$null | Select-String '^8\.') } catch { }
if (-not $hasSdk) { $dotnet = Join-Path $env:LOCALAPPDATA 'dotnet-sdk\dotnet.exe' }
if (-not $hasSdk -and -not (Test-Path $dotnet)) { throw ".NET 8 SDK not found: https://dotnet.microsoft.com/download/dotnet/8.0" }

$props = [xml](Get-Content $csproj)
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if ($Installer -or $Release) {
    foreach ($exe in 'yt-dlp.exe', 'gallery-dl.exe') {
        $p = Join-Path $root "engines\$exe"
        if (Test-Path $p) {
            Write-Host "Updating $exe..."
            # gallery-dl writes to stderr: don't let PowerShell 5.1 turn it into errors.
            $ErrorActionPreference = 'Continue'
            $out = & $p -U 2>&1 | ForEach-Object { "$_" }
            $ErrorActionPreference = 'Stop'
            $out | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" }
        }
    }
    $missing = 'yt-dlp.exe', 'gallery-dl.exe', 'ffmpeg.exe', 'ffprobe.exe', 'deno.exe' | Where-Object { -not (Test-Path "$root\engines\$_") }
    if ($missing) { throw "Missing engines in .\engines: $($missing -join ', ')" }

    $staging = "$root\installer\staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    & $dotnet publish $csproj @publishArgs -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    New-Item -ItemType Directory -Force "$staging\engines" | Out-Null
    Copy-Item "$root\engines\*" "$staging\engines" -Recurse -Exclude README.md
    Get-ChildItem "$staging\engines" -Filter '*.old' | Remove-Item -Force

    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
    $outDir = if ($Release) { Join-Path $root 'dist' } else { Join-Path $root 'installer\Output' }
    if ($Release -and (Test-Path $outDir)) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force $outDir | Out-Null
    Write-Host "Building installer $version..."
    & $iscc /Q "/DAppVersion=$version" "/O$outDir" "$root\installer\UltimateMP3Player.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    if ($Release) { Copy-Item "$staging\UltimateMP3Player.exe" $outDir }
    Remove-Item $staging -Recurse -Force
    Get-ChildItem "$outDir\*.exe" | ForEach-Object {
        Write-Host ("{0} ({1:N0} MB)  sha256 {2}" -f $_.FullName, ($_.Length / 1MB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())
    }
    if ($Release -and -not $WindowsOnly) { & "$root\build-unix.ps1" -Out $outDir }
    if ($Release -and -not $WindowsOnly -and -not $NoAndroid) { & "$root\build-android.ps1" -Out $outDir }
    if ($Release) { Write-Host "`nNew GitHub release tagged v$version with the setup (optionally also UltimateMP3Player.exe for lighter updates), the Linux/macOS packages and the Android APKs." }
    return
}

# Self-contained single file: runs on any Windows 10/11 x64 without installing .NET.
& $dotnet publish $csproj @publishArgs -o $app
if ($LASTEXITCODE -ne 0) { throw 'Build failed (is Ultimate MP3 Player running? Close it, also from the tray icon, and retry)' }

$engines = Join-Path $app 'engines'
New-Item -ItemType Directory -Force $engines | Out-Null
if (Test-Path "$root\engines") {
    Get-ChildItem "$root\engines" -File -Exclude README.md | ForEach-Object {
        $dst = Join-Path $engines $_.Name
        if (-not (Test-Path $dst)) { Copy-Item $_.FullName $dst }
    }
}

if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $desktop 'Ultimate MP3 Player.lnk'))
    $lnk.TargetPath = Join-Path $app 'UltimateMP3Player.exe'
    $lnk.WorkingDirectory = $app
    $lnk.IconLocation = (Join-Path $app 'UltimateMP3Player.exe') + ',0'
    $lnk.Description = 'Ultimate MP3 Player'
    $lnk.Save()
}

$exe = Get-Item (Join-Path $app 'UltimateMP3Player.exe')
$total = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("App:    {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB))
Write-Host ("Folder: {0} ({1:N0} MB with the engines)" -f $app, ($total / 1MB))
if (-not $NoShortcut) { Write-Host "Shortcut: Desktop\Ultimate MP3 Player.lnk" }
