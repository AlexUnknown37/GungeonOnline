# Saves a screenshot of the Enter the Gungeon window of copy -Index (0 = first started) to the given file.
# Usage: powershell -File screenshot-game.ps1 -Out C:\path\shot.png
param([Parameter(Mandatory = $true)][string]$Out, [int]$Index = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class EtgWin {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public struct RECT { public int L, T, R, B; }
}
"@
. "$PSScriptRoot\find-game-window.ps1" -Index $Index
[EtgWin]::SetForegroundWindow($GameWindow) | Out-Null
Start-Sleep -Milliseconds 800
$r = New-Object EtgWin+RECT
[EtgWin]::GetWindowRect($GameWindow, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object Drawing.Bitmap $w, $h
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object Drawing.Size $w, $h))
$bmp.Save($Out)
$g.Dispose(); $bmp.Dispose()
Write-Host "Saved $Out ($w x $h)"
