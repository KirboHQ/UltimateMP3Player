# Linux and macOS builds (src\UltimateMP3Player.Avalonia), made from Windows.
#   .\build-unix.ps1                        all four in .\dist, with the names the app's updater looks for:
#                                           UltimateMP3Player-linux-x64.tar.gz, UltimateMP3Player-linux-arm64.tar.gz,
#                                           UltimateMP3Player-macos-x64.zip, UltimateMP3Player-macos-arm64.zip
#   .\build-unix.ps1 -Targets linux-x64     only some (linux-x64, linux-arm64, macos-x64, macos-arm64)
#   .\build-unix.ps1 -Out C:\somewhere      somewhere else
# (.\build.ps1 -Release runs this too.)
# Linux: a folder "UltimateMP3Player" with the program, which on its first start adds itself to the applications menu.
# macOS: "Ultimate MP3 Player.app". It's signed "ad hoc" (no Apple certificate) with rcodesign, downloaded once into
# tools\bin: without any signature Apple Silicon Macs don't run it at all. Gatekeeper still asks the first time.
# The engines (yt-dlp, ffmpeg, deno, gallery-dl) aren't included: the app downloads the right ones on its first start.
param([string[]]$Targets = @('linux-x64', 'linux-arm64', 'macos-x64', 'macos-arm64'), [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'dist' }
$csproj = "$root\src\UltimateMP3Player.Avalonia\UltimateMP3Player.Avalonia.csproj"
$assets = "$root\src\UltimateMP3Player.Avalonia\Assets"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# A .NET 8 SDK from PATH, otherwise the local copy in %LOCALAPPDATA%\dotnet-sdk.
$dotnet = 'dotnet'
$hasSdk = $false
try { $hasSdk = [bool](& dotnet --list-sdks 2>$null | Select-String '^8\.') } catch { }
if (-not $hasSdk) { $dotnet = Join-Path $env:LOCALAPPDATA 'dotnet-sdk\dotnet.exe' }
if (-not $hasSdk -and -not (Test-Path $dotnet)) { throw ".NET 8 SDK not found: https://dotnet.microsoft.com/download/dotnet/8.0" }

$props = [xml](Get-Content $csproj)
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# ------------------------------------------------------------------ archives with Unix permissions

# .tar.gz (ustar): entries are @{ Name = 'dir/' or 'dir/file'; Source = path or $null; Mode = 0755 octal as int }.
function Write-TarGz([string]$path, $entries) {
    $file = [IO.File]::Create($path)
    $gz = New-Object IO.Compression.GZipStream $file, ([IO.Compression.CompressionLevel]::Optimal)
    $mtime = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    foreach ($e in $entries) {
        $isDir = $e.Name.EndsWith('/')
        $data = if ($isDir) { New-Object byte[] 0 } else { [IO.File]::ReadAllBytes($e.Source) }
        $h = New-Object byte[] 512
        $put = {
            param([int]$at, [string]$text)
            $b = [Text.Encoding]::UTF8.GetBytes($text)
            [Array]::Copy($b, 0, $h, $at, $b.Length)
        }
        if ([Text.Encoding]::UTF8.GetByteCount($e.Name) -gt 100) { throw "Name too long for tar: $($e.Name)" }
        & $put 0 $e.Name
        & $put 100 ([Convert]::ToString($e.Mode, 8).PadLeft(7, '0'))
        & $put 108 '0000000'
        & $put 116 '0000000'
        & $put 124 ([Convert]::ToString([long]$data.Length, 8).PadLeft(11, '0'))
        & $put 136 ([Convert]::ToString([long]$mtime, 8).PadLeft(11, '0'))
        & $put 148 '        '
        & $put 156 $(if ($isDir) { '5' } else { '0' })
        & $put 257 "ustar"
        & $put 263 '00'
        & $put 265 'root'
        & $put 297 'root'
        $sum = 0
        foreach ($b in $h) { $sum += $b }
        & $put 148 (([Convert]::ToString($sum, 8).PadLeft(6, '0')) + [char]0 + ' ')
        $gz.Write($h, 0, 512)
        if ($data.Length -gt 0) {
            $gz.Write($data, 0, $data.Length)
            $pad = (512 - $data.Length % 512) % 512
            if ($pad -gt 0) { $gz.Write((New-Object byte[] $pad), 0, $pad) }
        }
    }
    $gz.Write((New-Object byte[] 1024), 0, 1024)
    $gz.Dispose(); $file.Dispose()
}

# .zip whose entries carry Unix permissions (what macOS's Archive Utility and the app's updater apply).
function Write-UnixZip([string]$path, $entries) {
    if (Test-Path $path) { Remove-Item $path -Force }
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($e in $entries) {
        $isDir = $e.Name.EndsWith('/')
        $entry = $zip.CreateEntry($e.Name, $(if ($isDir) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }))
        $type = if ($isDir) { 0x4000 } else { 0x8000 }
        # File type and permissions in the high 16 bits (as a signed int).
        $entry.ExternalAttributes = [BitConverter]::ToInt32([BitConverter]::GetBytes([UInt32](($type -bor $e.Mode) * 65536)), 0)
        if (-not $isDir) {
            $s = $entry.Open()
            $bytes = [IO.File]::ReadAllBytes($e.Source)
            $s.Write($bytes, 0, $bytes.Length)
            $s.Dispose()
        }
    }
    $zip.Dispose()
    # "Made by" Unix, otherwise the permissions are ignored.
    $b = [IO.File]::ReadAllBytes($path)
    $eocd = -1
    for ($i = $b.Length - 22; $i -ge 0; $i--) {
        if ($b[$i] -eq 0x50 -and $b[$i + 1] -eq 0x4B -and $b[$i + 2] -eq 0x05 -and $b[$i + 3] -eq 0x06) { $eocd = $i; break }
    }
    if ($eocd -lt 0) { throw "Broken zip: $path" }
    $count = [BitConverter]::ToUInt16($b, $eocd + 10)
    $p = [BitConverter]::ToUInt32($b, $eocd + 16)
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($b, $p) -ne 0x02014B50) { throw "Broken zip directory: $path" }
        $b[$p + 4] = 20
        $b[$p + 5] = 3
        $p += 46 + [BitConverter]::ToUInt16($b, $p + 28) + [BitConverter]::ToUInt16($b, $p + 30) + [BitConverter]::ToUInt16($b, $p + 32)
    }
    [IO.File]::WriteAllBytes($path, $b)
}

# ------------------------------------------------------------------ ad-hoc signing (macOS)

$rcodesign = Join-Path $root 'tools\bin\rcodesign.exe'
function Get-RCodesign {
    if (Test-Path $rcodesign) { return }
    # Pinned version and checksum.
    $url = 'https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F0.29.0/apple-codesign-0.29.0-x86_64-pc-windows-msvc.zip'
    $sha = '54bb500e2da7a8de02fcae0f331d1cac6e6d7173b4281042ff9c528ba3159aaa'
    Write-Host 'Downloading rcodesign (ad-hoc signing of the macOS app)...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('rcodesign-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $zipPath = Join-Path $tmp 'rcodesign.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zipPath
        if ((Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower() -ne $sha) { throw 'rcodesign: checksum mismatch' }
        Expand-Archive $zipPath -DestinationPath $tmp -Force
        $exe = Get-ChildItem $tmp -Recurse -Filter 'rcodesign.exe' | Select-Object -First 1
        if (-not $exe) { throw 'rcodesign.exe not found in the download' }
        New-Item -ItemType Directory -Force (Split-Path $rcodesign) | Out-Null
        Copy-Item $exe.FullName $rcodesign
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

# ------------------------------------------------------------------ builds

$publishArgs = @('-c', 'Release', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '--nologo', '-v', 'q')
$staging = Join-Path $root 'installer\staging-unix'
New-Item -ItemType Directory -Force $Out | Out-Null

foreach ($target in $Targets) {
    $os, $arch = $target -split '-'
    if (($os -ne 'linux' -and $os -ne 'macos') -or ($arch -ne 'x64' -and $arch -ne 'arm64')) { throw "Unknown target: $target" }
    $rid = $(if ($os -eq 'macos') { 'osx' } else { 'linux' }) + '-' + $arch
    Write-Host "Building $target ($version)..."
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    $bin = Join-Path $staging 'bin'
    & $dotnet publish $csproj @publishArgs -r $rid -o $bin
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($target)" }
    $program = Join-Path $bin 'UltimateMP3Player'
    if (-not (Test-Path $program)) { throw "Program missing after publish ($target)" }
    # Only the program (everything is inside it); anything else the publish left goes along too.
    $files = Get-ChildItem $bin -File | Sort-Object Name

    if ($os -eq 'linux') {
        $entries = New-Object System.Collections.ArrayList
        [void]$entries.Add(@{ Name = 'UltimateMP3Player/'; Mode = 493 })
        foreach ($f in $files) {
            $mode = if ($f.Name -eq 'UltimateMP3Player' -or $f.Extension -eq '.so') { 493 } else { 420 }
            [void]$entries.Add(@{ Name = "UltimateMP3Player/$($f.Name)"; Source = $f.FullName; Mode = $mode })
        }
        $archive = Join-Path $Out "UltimateMP3Player-linux-$arch.tar.gz"
        Write-TarGz $archive $entries
    }
    else {
        $app = Join-Path $staging 'Ultimate MP3 Player.app'
        $contents = Join-Path $app 'Contents'
        New-Item -ItemType Directory -Force (Join-Path $contents 'MacOS'), (Join-Path $contents 'Resources') | Out-Null
        foreach ($f in $files) { Copy-Item $f.FullName (Join-Path $contents "MacOS\$($f.Name)") }
        Copy-Item "$assets\app.icns", "$assets\pack.icns" (Join-Path $contents 'Resources')
        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Ultimate MP3 Player</string>
    <key>CFBundleDisplayName</key>
    <string>Ultimate MP3 Player</string>
    <key>CFBundleIdentifier</key>
    <string>com.kirbohq.UltimateMP3Player</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleExecutable</key>
    <string>UltimateMP3Player</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleLocalizations</key>
    <array>
        <string>en</string>
        <string>it</string>
    </array>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.music</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Ultimate MP3 Player pack</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Owner</string>
            <key>CFBundleTypeIconFile</key>
            <string>pack.icns</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>com.kirbohq.UltimateMP3Player.pack</string>
            </array>
        </dict>
    </array>
    <key>UTExportedTypeDeclarations</key>
    <array>
        <dict>
            <key>UTTypeIdentifier</key>
            <string>com.kirbohq.UltimateMP3Player.pack</string>
            <key>UTTypeDescription</key>
            <string>Ultimate MP3 Player pack</string>
            <key>UTTypeIconFile</key>
            <string>pack.icns</string>
            <key>UTTypeConformsTo</key>
            <array>
                <string>public.data</string>
            </array>
            <key>UTTypeTagSpecification</key>
            <dict>
                <key>public.filename-extension</key>
                <array>
                    <string>ump</string>
                </array>
                <key>public.mime-type</key>
                <string>application/x-ultimate-mp3-player-pack</string>
            </dict>
        </dict>
    </array>
</dict>
</plist>
"@
        [IO.File]::WriteAllText((Join-Path $contents 'Info.plist'), $plist.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
        [IO.File]::WriteAllText((Join-Path $contents 'PkgInfo'), 'APPL????', (New-Object Text.UTF8Encoding $false))

        $signed = $false
        try {
            Get-RCodesign
            $ErrorActionPreference = 'Continue'
            $log = & $rcodesign sign $app 2>&1 | ForEach-Object { "$_" }
            $ok = $LASTEXITCODE -eq 0
            $ErrorActionPreference = 'Stop'
            if ($ok) { $signed = $true } else { $log | Select-Object -Last 5 | ForEach-Object { Write-Warning $_ } }
        }
        catch { $ErrorActionPreference = 'Stop'; Write-Warning $_ }
        if (-not $signed) {
            Write-Warning "Not signed: on Apple Silicon the app won't start until it's signed on a Mac with: codesign --force --deep -s - `"Ultimate MP3 Player.app`""
        }

        # Every file and folder of the .app, the program executable.
        $entries = New-Object System.Collections.ArrayList
        $base = $staging.TrimEnd('\') + '\'
        foreach ($d in @(Get-Item $app) + @(Get-ChildItem $app -Recurse -Directory | Sort-Object FullName)) {
            [void]$entries.Add(@{ Name = $d.FullName.Substring($base.Length).Replace('\', '/') + '/'; Mode = 493 })
        }
        foreach ($f in Get-ChildItem $app -Recurse -File | Sort-Object FullName) {
            $inMacOS = (Split-Path $f.DirectoryName -Leaf) -eq 'MacOS'
            $mode = if ($inMacOS -and ($f.Name -eq 'UltimateMP3Player' -or $f.Extension -eq '.dylib')) { 493 } else { 420 }
            [void]$entries.Add(@{ Name = $f.FullName.Substring($base.Length).Replace('\', '/'); Source = $f.FullName; Mode = $mode })
        }
        $archive = Join-Path $Out "UltimateMP3Player-macos-$arch.zip"
        Write-UnixZip $archive $entries
    }
    Remove-Item $staging -Recurse -Force
    $a = Get-Item $archive
    Write-Host ("  {0} ({1:N0} MB)  sha256 {2}" -f $a.FullName, ($a.Length / 1MB), (Get-FileHash $a.FullName -Algorithm SHA256).Hash.ToLower())
}
