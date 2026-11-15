# README screenshots, step 2: the Windows app on the screen with the demo folder (demo.ps1), driven by hand, one action a call.
# Puts a window on the screen and moves the mouse. Clicks go by the pixels of the pictures it takes (UI Automation gives
# empty rectangles for the app's elements).
#   desktop.ps1 start                 the Debug build with UMP_DATA = <Folder>\data (runs beside your own copy)
#   desktop.ps1 place <x> <y>         the window there, 1600 x 1000
#   desktop.ps1 img <x> <y> [n]       n clicks at a point of the window's picture, the window brought forward first
#   desktop.ps1 dlg <x> <y> [n]       the same without bringing it forward (a dialog over it)
#   desktop.ps1 type <text>           keys to whatever has the focus; keys <text>: to the window
#   desktop.ps1 shot <name>           the window alone (PrintWindow) into <Folder>\shots\<name>.png; screen <name>: what's on the screen there
#   desktop.ps1 stop
# The README's: home, lyrics (song page, Lyrics on), playlist (Road Trip), search (Elektronomia), dj (Sky High + Limitless).
param([string]$Do, [string]$A = "", [string]$B = "", [string]$C = "1", [string]$Folder = (Join-Path $env:TEMP "ump-readme"))
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
}
'@
[W]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
$repo = Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "..\..")
$exe = (Get-ChildItem (Join-Path $repo "src\UltimateMP3Player\bin\Debug") -Recurse -Filter "UltimateMP3Player.exe" | Select-Object -First 1).FullName

function Proc { Get-Process UltimateMP3Player -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
function Hwnd { $p = Proc; if (-not $p) { throw "The demo app isn't running." }; $p.MainWindowHandle }
# The visible frame (the window's rectangle has the invisible resize borders around it).
function Frame { $r = New-Object W+RECT; [W]::DwmGetWindowAttribute((Hwnd), 9, [ref]$r, 16) | Out-Null; $r }
function Click([int]$x, [int]$y, [int]$times, [bool]$forward) {
    if ($forward) { [W]::SetForegroundWindow((Hwnd)) | Out-Null }
    $r = Frame
    [W]::SetCursorPos($r.L + $x, $r.T + $y) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt $times; $i++) { [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60 }
}
function Picture([string]$name) { New-Item -ItemType Directory -Force (Join-Path $Folder "shots") | Out-Null; Join-Path $Folder "shots\$name.png" }

switch ($Do) {
    'start' {
        $env:UMP_DATA = Join-Path $Folder "data"
        Start-Process $exe
        for ($i = 0; $i -lt 60 -and -not (Proc); $i++) { Start-Sleep -Milliseconds 500 }
        Start-Sleep 3
        "started"
    }
    'place' { [W]::SetWindowPos((Hwnd), [IntPtr]::Zero, [int]$A, [int]$B, 1600, 1000, 0x0040) | Out-Null; [W]::SetForegroundWindow((Hwnd)) | Out-Null; "placed" }
    'img' { Click ([int]$A) ([int]$B) ([int]$C) $true; "clicked $A,$B" }
    'dlg' { Click ([int]$A) ([int]$B) ([int]$C) $false; "clicked $A,$B" }
    'type' { [System.Windows.Forms.SendKeys]::SendWait($A); "typed" }
    'keys' { [W]::SetForegroundWindow((Hwnd)) | Out-Null; Start-Sleep -Milliseconds 150; [System.Windows.Forms.SendKeys]::SendWait($A); "typed" }
    'shot' {
        # Out of the way first: a tooltip or a hover would be in the picture.
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 0, 0
        Start-Sleep -Milliseconds 800
        $h = Hwnd; $r = Frame
        $wr = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$wr) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($wr.R - $wr.L), ($wr.B - $wr.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
        [W]::PrintWindow($h, $hdc, 2) | Out-Null
        $g.ReleaseHdc($hdc); $g.Dispose()
        $crop = New-Object System.Drawing.Rectangle ($r.L - $wr.L), ($r.T - $wr.T), ($r.R - $r.L), ($r.B - $r.T)
        $path = Picture $A
        $bmp.Clone($crop, $bmp.PixelFormat).Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $path
    }
    'screen' {
        $r = Frame
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
        $path = Picture $A
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $path
    }
    'stop' { $p = Proc; if ($p) { $p.CloseMainWindow() | Out-Null; Start-Sleep 2; if (-not $p.HasExited) { $p.Kill() } }; "stopped" }
    default { throw "start | place x y | img x y [n] | dlg x y [n] | type text | keys text | shot name | screen name | stop" }
}
