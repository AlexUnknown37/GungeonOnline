# Holds one key down in the game window of copy -Index for -Ms milliseconds (e.g. to walk: -Key D -Ms 1000).
# -Key is a single letter or digit (its virtual-key code is the same as the uppercase character).
param([Parameter(Mandatory = $true)][string]$Key, [int]$Ms = 1000, [int]$Index = 0)
. "$PSScriptRoot\find-game-window.ps1" -Index $Index
Add-Type @"
using System; using System.Runtime.InteropServices;
public class HoldKey {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
}
"@
[HoldKey]::SetForegroundWindow($GameWindow) | Out-Null
Start-Sleep -Milliseconds 400
$vk = [byte][char]$Key.ToUpper()
$scan = [byte][HoldKey]::MapVirtualKey($vk, 0)
[HoldKey]::keybd_event($vk, $scan, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds $Ms
[HoldKey]::keybd_event($vk, $scan, 2, [UIntPtr]::Zero)
"Held $Key for $Ms ms in copy $Index"
