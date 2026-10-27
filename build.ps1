# Build script.
#   .\build.ps1               portable app in .\app plus a Desktop shortcut
#   .\build.ps1 -NoShortcut   same, without touching the Desktop
#   .\build.ps1 -Installer    only the installers: installer\Output\UltimateMP3Player-windows-x64-setup.exe and
#                             -windows-arm64-setup.exe (Windows 11 on ARM)
#   .\build.ps1 -Release      the files for a GitHub release in .\dist: the installers UltimateMP3Player-windows-x64-setup.exe
#                             and -windows-arm64-setup.exe (enough on their own), UltimateMP3Player-windows-x64.exe and
#                             -windows-arm64.exe (optional: if attached, updates download 70-80 MB instead of the whole setup)
#                             and dist\legacy\UltimateMP3Player.exe, the x64 exe under the name the versions up to 3.6.x look
#                             for (attach it too for a few releases, so they update), the Linux and macOS packages
#                             (build-unix.ps1) and the Android APKs (build-android.ps1); -WindowsOnly skips both, -NoAndroid
#                             only the APKs
# -Installer and -Release first update yt-dlp/gallery-dl in .\engines and need Inno Setup 6.
# Engines are copied into .\app\engines only when missing, so versions updated by the app are kept.
param([switch]$NoShortcut, [switch]$Installer, [switch]$Release, [switch]$WindowsOnly, [switch]$NoAndroid)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'app'
$csproj = "$root\src\UltimateMP3Player\UltimateMP3Player.csproj"
# ReadyToRun: the code comes already compiled, so the start and the first pages don't wait for the JIT. Not "composite" (a
# single file's default since .NET 7: the whole runtime, WPF included, compiled again into one image): .NET's own libraries
# come already compiled, only the app's are (measured: publish 42 → 14 s, exe 92 → 76 MB).
$publishArgs = @('-c', 'Release', '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=false', '-p:DebugType=none', '--nologo', '-v', 'q')
# The exe on its own (portable, updates) is compressed by .NET; the one inside the setup isn't: Inno Setup's LZMA does much
# better on it (setup 17 MB smaller), and uncompressed it starts faster and uses less memory (see build-unix.ps1).
$compressed = '-p:EnableCompressionInSingleFile=true'
$uncompressed = '-p:EnableCompressionInSingleFile=false'

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

    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
    $outDir = if ($Release) { Join-Path $root 'dist' } else { Join-Path $root 'installer\Output' }
    if ($Release -and (Test-Path $outDir)) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force $outDir | Out-Null

    # One app and setup per processor. The ARM one gets the same x64 engines: Windows 11 on ARM runs them emulated.
    $staging = "$root\installer\staging"
    foreach ($arch in 'x64', 'arm64') {
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        Write-Host "Publishing $arch..."
        & $dotnet publish $csproj @publishArgs $uncompressed -r "win-$arch" -o $staging
        if ($LASTEXITCODE -ne 0) { throw "Publish failed ($arch)" }
        New-Item -ItemType Directory -Force "$staging\engines" | Out-Null
        Copy-Item "$root\engines\*" "$staging\engines" -Recurse -Exclude README.md
        Get-ChildItem "$staging\engines" -Filter '*.old' | Remove-Item -Force

        Write-Host "Building installer $version ($arch)..."
        & $iscc /Q "/DAppVersion=$version" "/DArch=$arch" "/O$outDir" "$root\installer\UltimateMP3Player.iss"
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($arch)" }
        if ($Release) {
            # The same build compressed: only the bundle is made again (a few seconds).
            $single = "$staging-exe"
            & $dotnet publish $csproj @publishArgs $compressed -r "win-$arch" -o $single
            if ($LASTEXITCODE -ne 0) { throw "Publish failed ($arch, exe)" }
            Copy-Item "$single\UltimateMP3Player.exe" "$outDir\UltimateMP3Player-windows-$arch.exe"
            # Up to 3.6.x the updater only knows this name (and x64 only).
            if ($arch -eq 'x64') {
                New-Item -ItemType Directory -Force "$outDir\legacy" | Out-Null
                Copy-Item "$single\UltimateMP3Player.exe" "$outDir\legacy\UltimateMP3Player.exe"
            }
            Remove-Item $single -Recurse -Force
        }
    }
    Remove-Item $staging -Recurse -Force
    Get-ChildItem "$outDir\*.exe", "$outDir\legacy\*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host ("{0} ({1:N0} MB)  sha256 {2}" -f $_.FullName, ($_.Length / 1MB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())
    }
    if ($Release -and -not $WindowsOnly) { & "$root\build-unix.ps1" -Out $outDir }
    if ($Release -and -not $WindowsOnly -and -not $NoAndroid) { & "$root\build-android.ps1" -Out $outDir }
    if ($Release) { Write-Host "`nNew GitHub release tagged v$version with the two setups (x64, arm64; optionally also UltimateMP3Player-windows-x64.exe and -arm64.exe for lighter updates, and legacy\UltimateMP3Player.exe so 3.6.x installs update), the Linux/macOS packages and the Android APKs." }
    return
}

# Self-contained single file: runs on any Windows 10/11 x64 without installing .NET.
& $dotnet publish $csproj @publishArgs $compressed -r win-x64 -o $app
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
