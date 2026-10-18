# Holds W, taps Space (dodge roll) mid-walk, in copy -Index.
param([int]$Index = 0)
. "$PSScriptRoot\find-game-window.ps1" -Index $Index
Add-Type @"
using System; using System.Runtime.InteropServices;
public class RollKey {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
}
"@
[RollKey]::SetForegroundWindow($GameWindow) | Out-Null
Start-Sleep -Milliseconds 400
function Down($vk) { [RollKey]::keybd_event($vk, [byte][RollKey]::MapVirtualKey($vk, 0), 0, [UIntPtr]::Zero) }
function Up($vk) { [RollKey]::keybd_event($vk, [byte][RollKey]::MapVirtualKey($vk, 0), 2, [UIntPtr]::Zero) }
Down 0x53; Start-Sleep -Milliseconds 500; Up 0x53   # back off (S)
Down 0x57; Start-Sleep -Milliseconds 150
Down 0x20; Start-Sleep -Milliseconds 80; Up 0x20     # roll (Space)
Start-Sleep -Milliseconds 900; Up 0x57
"rolled"
