# Sends keys to the game window of copy -Index (0 = first started), using SendKeys syntax:
#   -Keys "{F2}"   -Keys "character thelamb{ENTER}"
param([Parameter(Mandatory = $true)][string]$Keys, [int]$Index = 0)
. "$PSScriptRoot\find-game-window.ps1" -Index $Index
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class EtgKey { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
[EtgKey]::SetForegroundWindow($GameWindow) | Out-Null
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait($Keys)
Write-Host "Sent $Keys"
