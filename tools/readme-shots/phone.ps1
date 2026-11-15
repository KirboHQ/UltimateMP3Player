# README screenshots, step 3: the phone app on the MuMu emulator with the demo folder (demo.ps1), as an iPhone 16 (1179 x 2556,
# density 480). A separate test package (com.kirbohq.ultimatemp3player.test): the real app and its data stay as they are.
#   phone.ps1 build             the test APK (its own id and file provider) into src\UltimateMP3Player.Android\bin\readme
#   phone.ps1 install           installs it, copies the demo data (paths made Android's by phone-data.py) and the songs
#   phone.ps1 setup             the iPhone's screen, a clean status bar (09:41, full battery), the app started
#   phone.ps1 tap <x> <y> | swipe <x1> <y1> <x2> <y2> | text <text> | key <code>      (screen pixels)
#   phone.ps1 shot <name>       into <Folder>\pshots\<name>.png
#   phone.ps1 restore           the emulator's own screen and status bar back
# The README's: player, lyrics, playlist (Road Trip), search, home_light (Settings > Appearance > Light, then Home).
param([string]$Do, [string]$A = "", [string]$B = "", [string]$C = "", [string]$D = "", [string]$Folder = (Join-Path $env:TEMP "ump-readme"))
$ErrorActionPreference = "Stop"
$adb = "C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe"
$dev = "127.0.0.1:16384"
$pkg = "com.kirbohq.ultimatemp3player.test"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$android = Resolve-Path (Join-Path $here "..\..\src\UltimateMP3Player.Android")
function Adb { & $adb -s $dev @args }
function Demo([string]$command) { Adb shell "am broadcast -a com.android.systemui.demo -e command $command >/dev/null" }

switch ($Do) {
    'build' {
        # The same file provider as the real app would make the install fail: the test copy gets its own.
        $manifest = Join-Path $android "obj\readme-manifest\AndroidManifest.xml"
        New-Item -ItemType Directory -Force (Split-Path $manifest) | Out-Null
        $text = [IO.File]::ReadAllText((Join-Path $android "Properties\AndroidManifest.xml"))
        [IO.File]::WriteAllText($manifest, $text.Replace('"com.kirbohq.ultimatemp3player.files"', '"com.kirbohq.ultimatemp3player.test.files"'))
        $env:DOTNET_ROOT = "$env:LOCALAPPDATA\dotnet10"
        & "$env:LOCALAPPDATA\dotnet10\dotnet.exe" build (Join-Path $android "UltimateMP3Player.Android.csproj") -c Debug -v q -nologo -t:SignAndroidPackage `
            -p:AndroidRid=android-x64 "-p:ApplicationId=$pkg" "-p:AndroidManifest=obj\readme-manifest\AndroidManifest.xml" -p:EmbedAssembliesIntoApk=true `
            "-p:IntermediateOutputPath=obj\readme\" "-p:OutputPath=bin\readme\" "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Android\jdk"
    }
    'install' {
        & $adb connect $dev | Out-Null
        $apk = Get-ChildItem (Join-Path $android "bin\readme") -Filter "*-Signed.apk" -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $apk) { throw "Run 'phone.ps1 build' first." }
        Adb install -r $apk.FullName
        python -I (Join-Path $here "phone-data.py") $Folder $pkg
        Adb shell am force-stop $pkg
        # run-as reaches the app's private folder only from a place everyone can read.
        Adb shell "rm -rf /data/local/tmp/umpdata"
        Adb push (Join-Path $Folder "phone\data") /data/local/tmp/umpdata | Select-Object -Last 1
        Adb shell "chmod -R a+rX /data/local/tmp/umpdata; run-as $pkg sh -c 'rm -rf files/data; mkdir -p files; cp -r /data/local/tmp/umpdata files/data'; rm -rf /data/local/tmp/umpdata"
        Adb shell "mkdir -p /sdcard/Android/data/$pkg/files/Music"
        Adb push (Join-Path $Folder "music\.") "/sdcard/Android/data/$pkg/files/Music/" | Select-Object -Last 1
    }
    'setup' {
        # What the emulator had, for restore.
        Adb shell "wm density" | Set-Content (Join-Path $Folder "phone-density.txt")
        Adb shell "wm size 1179x2556; wm density 480; pm grant $pkg android.permission.POST_NOTIFICATIONS 2>/dev/null; settings put global sysui_demo_allowed 1"
        Demo "enter"; Demo "clock -e hhmm 0941"; Demo "battery -e level 100 -e plugged false"
        Demo "network -e wifi show -e level 4"; Demo "network -e mobile show -e level 4 -e datatype none"; Demo "notifications -e visible false"
        Adb shell "monkey -p $pkg -c android.intent.category.LAUNCHER 1" | Out-Null
        "started: give it ~25 s the first time"
    }
    'tap' { Adb shell input tap $A $B; "tap $A,$B" }
    'swipe' { Adb shell input swipe $A $B $C $D 400; "swipe" }
    'text' { Adb shell input text ($A -replace ' ', '%s'); "text" }
    'key' { Adb shell input keyevent $A; "key $A" }
    'shot' {
        New-Item -ItemType Directory -Force (Join-Path $Folder "pshots") | Out-Null
        $path = Join-Path $Folder "pshots\$A.png"
        # Through cmd: PowerShell 5.1 would turn the PNG's bytes into text.
        cmd /c "`"$adb`" -s $dev exec-out screencap -p > `"$path`""
        $path
    }
    'restore' {
        $saved = Join-Path $Folder "phone-density.txt"
        $override = if (Test-Path $saved) { Select-String -Path $saved -Pattern "Override density: (\d+)" | ForEach-Object { $_.Matches[0].Groups[1].Value } }
        $density = if ($override) { "wm density $override" } else { "wm density reset" }
        Adb shell "am force-stop $pkg; am broadcast -a com.android.systemui.demo -e command exit >/dev/null; settings put global sysui_demo_allowed 0; wm size reset; $density; wm size; wm density"
    }
    default { throw "build | install | setup | tap x y | swipe x1 y1 x2 y2 | text t | key code | shot name | restore" }
}
