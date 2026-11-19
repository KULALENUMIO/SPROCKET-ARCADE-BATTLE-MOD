param([string]$Replay = "replay damaged", [int]$Frames = 8, [double]$Interval = 0.45, [double]$Delay = 0.1,
      [string]$Out = "$env:TEMP\hitcam_grid.png", [int]$Columns = 4, [double]$Left = 0.68, [double]$Height = 0.36, [double]$Top = 0, [double]$Right = 1)
# Dev: replay a hit camera capture (hitcam <Replay>) and tile captures of the top-right panel into one image.
# Captures the game window with PrintWindow, so it works while the window is covered and never takes focus.
# Keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less files as Shift-JIS).
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static IntPtr UnityWindow(uint pid) {
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "UnityWndClass", null)) != IntPtr.Zero) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid) return h;
        }
        return IntPtr.Zero;
    }
}
"@
[Win]::SetProcessDPIAware() | Out-Null
$hwnd = [Win]::UnityWindow([uint32](Get-Process Sprocket).Id)
$r = New-Object Win+RECT
[Win]::GetClientRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right; $h = $r.Bottom
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\..\BepInEx")
[IO.File]::WriteAllLines("$root\sprocketmods_commands.txt", @("hitcam $Replay"), (New-Object Text.UTF8Encoding $false))
Start-Sleep -Milliseconds ([int]($Delay * 1000) + 150)
$cx = [int]($w * $Left); $cw = [int]($w * $Right) - $cx; $ch = [int]($h * $Height); $cy = [int]($h * $Top)
$rows = [Math]::Ceiling($Frames / $Columns)
$grid = New-Object System.Drawing.Bitmap ($cw * $Columns), ($ch * $rows)
$gg = [System.Drawing.Graphics]::FromImage($grid)
$full = New-Object System.Drawing.Bitmap $w, $h
for ($i = 0; $i -lt $Frames; $i++) {
    $g = [System.Drawing.Graphics]::FromImage($full)
    $dc = $g.GetHdc()
    [Win]::PrintWindow($hwnd, $dc, 3) | Out-Null   # PW_CLIENTONLY | PW_RENDERFULLCONTENT
    $g.ReleaseHdc($dc); $g.Dispose()
    $src = New-Object System.Drawing.Rectangle $cx, $cy, $cw, $ch
    $dst = New-Object System.Drawing.Rectangle (($i % $Columns) * $cw), ([Math]::Floor($i / $Columns) * $ch), $cw, $ch
    $gg.DrawImage($full, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)
    Start-Sleep -Milliseconds ([int]($Interval * 1000))
}
$grid.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$gg.Dispose(); $grid.Dispose(); $full.Dispose()
$Out
