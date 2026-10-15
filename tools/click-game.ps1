# Clicks inside the first Enter the Gungeon window at (X, Y) pixels from its top-left corner.
# Usage: powershell -File click-game.ps1 -X 760 -Y 1236 [-HoldMs 250]
param([Parameter(Mandatory = $true)][int]$X, [Parameter(Mandatory = $true)][int]$Y, [int]$HoldMs = 250)
Add-Type @"
using System; using System.Runtime.InteropServices;
public class EtgClick {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int L, T, R, B; }
}
"@
$p = Get-Process EtG -ErrorAction Stop | Select-Object -First 1
[EtgClick]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$r = New-Object EtgClick+RECT
[EtgClick]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
[EtgClick]::SetCursorPos($r.L + $X, $r.T + $Y) | Out-Null
Start-Sleep -Milliseconds 400
[EtgClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # left down
Start-Sleep -Milliseconds $HoldMs
[EtgClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # left up
Write-Host "Clicked ($X, $Y)"
