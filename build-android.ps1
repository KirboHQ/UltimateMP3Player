# Android build: one signed APK per processor, for the GitHub release (the app updates itself from there).
#   .\build-android.ps1                 the APKs in .\dist\android
#   .\build-android.ps1 -Out .\dist     into another folder (build.ps1 -Release does this)
# Files: UltimateMP3Player-android-arm64-v8a.apk (almost every phone), -armeabi-v7a.apk (old 32-bit phones),
#        -x86_64.apk (emulators, Chromebooks). The app's updater looks for exactly these names.
# Needs: the .NET 10 SDK with the android workload (dotnet workload install android), the Android SDK and a JDK
# (%LOCALAPPDATA%\Android\Sdk and \Android\jdk, or ANDROID_HOME / JAVA_HOME); the engines come from tools\android-deps.ps1.
#
# Signing key: Android installs an update only if it's signed with the same key as the installed app. The first run
# creates it OUTSIDE the repository, in %USERPROFILE%\.ultimatemp3player-android (or -KeyDir / UMP_ANDROID_KEYS), with its
# password next to it. Keep a copy of that folder somewhere safe and never put it in git: without it, phones can't update
# and everyone would have to uninstall (losing the app's music) to install the next version.
param([string]$Out = '', [string]$KeyDir = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = "$root\src\UltimateMP3Player.Android\UltimateMP3Player.Android.csproj"
if (-not $Out) { $Out = Join-Path $root 'dist\android' }
if (-not $KeyDir) { $KeyDir = if ($env:UMP_ANDROID_KEYS) { $env:UMP_ANDROID_KEYS } else { Join-Path $env:USERPROFILE '.ultimatemp3player-android' } }

# .NET 10 SDK: from PATH, otherwise the local copy in %LOCALAPPDATA%\dotnet10.
$dotnet = 'dotnet'
$hasSdk = $false
try { $hasSdk = [bool](& dotnet --list-sdks 2>$null | Select-String '^10\.') } catch { }
if (-not $hasSdk) {
    $dotnet = Join-Path $env:LOCALAPPDATA 'dotnet10\dotnet.exe'
    if (-not (Test-Path $dotnet)) { throw '.NET 10 SDK not found: https://dotnet.microsoft.com/download/dotnet/10.0 (then: dotnet workload install android)' }
    $env:DOTNET_ROOT = Split-Path $dotnet
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$jdk = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\jdk' }
if (-not (Test-Path "$sdk\platform-tools")) { throw "Android SDK not found in $sdk (set ANDROID_HOME)" }
if (-not (Test-Path "$jdk\bin\keytool.exe")) { throw "JDK not found in $jdk (set JAVA_HOME)" }

& "$root\tools\android-deps.ps1" | Out-Null

# The signing key (created once).
$keystore = Join-Path $KeyDir 'ultimatemp3player.keystore'
$passFile = Join-Path $KeyDir 'password.txt'
$alias = 'ultimatemp3player'
if (-not (Test-Path $keystore)) {
    if ($KeyDir.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'The signing key must stay outside the repository' }
    New-Item -ItemType Directory -Force $KeyDir | Out-Null
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $pass = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'
    [IO.File]::WriteAllText($passFile, "$pass`n$pass`n")
    Write-Host "Creating the signing key in $KeyDir..."
    # keytool talks on stderr: don't let PowerShell 5.1 turn it into errors.
    $ErrorActionPreference = 'Continue'
    $out = & "$jdk\bin\keytool.exe" -genkeypair -keystore $keystore -alias $alias -keyalg RSA -keysize 4096 -validity 36500 `
        -storepass $pass -keypass $pass -dname 'CN=Ultimate MP3 Player, O=KirboHQ' 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $keystore)) { $out | Write-Host; throw 'keytool failed' }
    Write-Host "  KEEP A COPY of $KeyDir somewhere safe: every future update must be signed with this key." -ForegroundColor Yellow
}
if (-not (Test-Path $passFile)) { throw "Password file missing: $passFile" }
# apksigner reads the keystore's and the key's password from the same file one line each: the same password, twice.
$lines = @([IO.File]::ReadAllLines($passFile) | Where-Object { $_ })
if ($lines.Count -eq 0) { throw "Password file empty: $passFile" }
if ($lines.Count -lt 2) { [IO.File]::WriteAllText($passFile, "$($lines[0])`n$($lines[0])`n") }

$props = [xml](Get-Content $proj)
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$bin = "$root\src\UltimateMP3Player.Android\bin\Release"
$obj = "$root\src\UltimateMP3Player.Android\obj\Release"
foreach ($d in $bin, $obj) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force $Out | Out-Null
# One build per processor: each APK carries only that processor's engines (about 60 MB instead of 195).
foreach ($t in @(@{ Rid = 'android-arm64'; Abi = 'arm64-v8a' }, @{ Rid = 'android-arm'; Abi = 'armeabi-v7a' }, @{ Rid = 'android-x64'; Abi = 'x86_64' })) {
    $abi = $t.Abi
    Write-Host "Building Android $version for $abi..."
    $ErrorActionPreference = 'Continue'
    $log = & $dotnet publish $proj -c Release -f net10.0-android --nologo -v q `
        "-p:AndroidRid=$($t.Rid)" `
        "-p:AndroidSdkDirectory=$sdk" "-p:JavaSdkDirectory=$jdk" `
        -p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=$keystore" "-p:AndroidSigningKeyAlias=$alias" `
        "-p:AndroidSigningStorePass=file:$passFile" "-p:AndroidSigningKeyPass=file:$passFile" 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) {
        $log | Select-String -Pattern 'error' | Select-Object -First 30 | ForEach-Object { Write-Host $_ }
        throw "Android build failed ($abi)"
    }
    # (the newest: each build's APK is copied away before the next one)
    $apk = Get-ChildItem $bin -Recurse -Filter 'com.kirbohq.ultimatemp3player-Signed.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apk) { throw "APK for $abi not found" }
    $dst = Join-Path $Out "UltimateMP3Player-android-$abi.apk"
    Copy-Item $apk.FullName $dst -Force
    $f = Get-Item $dst
    Write-Host ("{0} ({1:N0} MB)  sha256 {2}" -f $f.FullName, ($f.Length / 1MB), (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower())
}
