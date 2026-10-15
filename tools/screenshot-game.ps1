# Saves a screenshot of the first Enter the Gungeon window to the given file.
# Usage: powershell -File screenshot-game.ps1 -Out C:\path\shot.png
param([Parameter(Mandatory = $true)][string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class EtgWin {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public struct RECT { public int L, T, R, B; }
}
"@
$p = Get-Process EtG -ErrorAction Stop | Select-Object -First 1
[EtgWin]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 800
$r = New-Object EtgWin+RECT
[EtgWin]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object Drawing.Bitmap $w, $h
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object Drawing.Size $w, $h))
$bmp.Save($Out)
$g.Dispose(); $bmp.Dispose()
Write-Host "Saved $Out ($w x $h)"
