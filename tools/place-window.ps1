# Moves/resizes the game window of a test copy (by its --gungeon-online-name) into a corner of the main screen.
# Usage: place-window.ps1 -Name Host -Corner TopLeft      (TopLeft, TopRight, BottomLeft, BottomRight)
param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][string]$Corner)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class PlaceWin {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@
$index = & "$PSScriptRoot\copy-index.ps1" -Name $Name
if ($LASTEXITCODE -ne 0) { throw "No test copy named $Name." }
. "$PSScriptRoot\find-game-window.ps1" -Index $index
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$w = [int]($area.Width / 2); $h = [int]($area.Height / 2)
$x = $area.X; $y = $area.Y
if ($Corner -match "Right") { $x += $w }
if ($Corner -match "Bottom") { $y += $h }
[PlaceWin]::ShowWindow($GameWindow, 9) | Out-Null   # restore if maximized/minimized
[PlaceWin]::MoveWindow($GameWindow, $x, $y, $w, $h, $true) | Out-Null
"$Name -> $Corner ($x,$y ${w}x$h)"
