# Audit helper: two copies of the game on this PC, isolated BepInEx folders, the mod's own test flags
# (auto host / auto join on 127.0.0.1, pick a character, start a run, both played by the mod's bot).
# Runs for -Minutes, closes both copies, removes the temporary steam_appid.txt, and leaves the logs in
# <audit>\smoke-host and <audit>\smoke-guest.
param(
    [int]$Minutes = 6,
    [string]$Dll = "",   # default: <work folder>\build\GungeonOnline.dll
    [string]$GuestDll = "",
    [int]$Lag = 35,
    [string]$Tag = "run",
    [string]$HostChar = "PlayerRogue",
    [string]$GuestChar = "PlayerConvict",
    [switch]$NoBot,
    [switch]$Bare   # only the mod and the test helpers, without the rest of the modpack
)
$ErrorActionPreference = "Stop"
# The work folder (holds build\, and gets smoke-host\, smoke-guest\, lan-logs\, save-backup\) and the game folder.
# Set GUNGEON_AUDIT_DIR / GUNGEON_DIR for another machine. See README.md next to this file.
$A = if ($env:GUNGEON_AUDIT_DIR) { $env:GUNGEON_AUDIT_DIR } else { "C:\Projects\GungeonOnline-audit" }
$G = if ($env:GUNGEON_DIR) { $env:GUNGEON_DIR } else { "C:\Steam\steamapps\common\Enter the Gungeon" }
if ($Dll -eq "") { $Dll = "$A\build\GungeonOnline.dll" }
$Pack = "$env:APPDATA\com.kesomannen.gale\cache\BepInEx-BepInExPack_EtG\5.4.2101\BepInEx"
# The modpack the mod is played in (the Gale Default profile). Each test copy gets its own copy of it, with
# the build under test in place of the installed Gungeon Online. The profile itself is only read.
$Modpack = "$env:APPDATA\com.kesomannen.gale\enter-the-gungeon\profiles\Default\BepInEx"
if ($GuestDll -eq "") { $GuestDll = $Dll }
if (Get-Process EtG -ErrorAction SilentlyContinue) { throw "Enter the Gungeon is already running." }

# Saves are shared by both copies: keep a copy of the save folder first (once per day is enough).
$save = "$env:USERPROFILE\AppData\LocalLow\Dodge Roll\Enter the Gungeon"
$bak = "$A\save-backup\" + (Get-Date -Format "yyyy-MM-dd")
if ((Test-Path $save) -and -not (Test-Path $bak)) {
    New-Item -ItemType Directory -Force $bak | Out-Null
    Copy-Item "$save\*" $bak -Recurse -Force
    "Saves backed up to $bak"
}

function Prepare($name, $dll) {
    $dir = "$A\smoke-$name"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item $Pack "$dir\BepInEx" -Recurse
    if (-not $Bare) {
        foreach ($sub in "plugins", "monomod", "patchers") {
            if (-not (Test-Path "$Modpack\$sub")) { continue }
            New-Item -ItemType Directory -Force "$dir\BepInEx\$sub" | Out-Null
            Get-ChildItem "$Modpack\$sub" | Where-Object { $_.Name -notlike "Gungeon_Online*" } |
                ForEach-Object { Copy-Item $_.FullName "$dir\BepInEx\$sub" -Recurse -Force }
        }
        New-Item -ItemType Directory -Force "$dir\BepInEx\config" | Out-Null
        Get-ChildItem "$Modpack\config" -File | Where-Object { $_.Name -notlike "gungeononline*" } |
            ForEach-Object { Copy-Item $_.FullName "$dir\BepInEx\config" -Force }
    }
    New-Item -ItemType Directory -Force "$dir\BepInEx\plugins\GungeonOnline-audit" | Out-Null
    Copy-Item $dll "$dir\BepInEx\plugins\GungeonOnline-audit\GungeonOnline.dll"
    Copy-Item "$A\build\AuditSmallWindow.dll" "$dir\BepInEx\plugins\GungeonOnline-audit\" -ErrorAction SilentlyContinue   # test-only: small window
    Copy-Item "$A\build\AuditCommands.dll" "$dir\BepInEx\plugins\GungeonOnline-audit\" -ErrorAction SilentlyContinue      # test-only: console commands from a file (tools\game-cmd.ps1)
    return $dir
}
function Launch($dir, $name, $mode, $char, $lag) {
    $a = @("--doorstop-enable", "true", "--doorstop-target", "`"$dir\BepInEx\core\BepInEx.Preloader.dll`"",
        "--gungeon-online-name", $name, "--gungeon-online-test", $mode, "--gungeon-online-test-run", $char,
        "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540")
    if (-not $NoBot) { $a += "--gungeon-online-bot" }
    if ($lag -gt 0) { $a += @("--gungeon-online-lag", "$lag") }
    return Start-Process -FilePath "$G\EtG.exe" -WorkingDirectory $G -PassThru -ArgumentList $a
}

# Keep the test windows small, in the bottom-right corner, behind other windows, without taking focus.
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class AuditWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    public static IntPtr Find(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            StringBuilder s = new StringBuilder(256); GetWindowText(h, s, 256);
            if (s.ToString() == "Enter the Gungeon") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void Tuck(uint pid, int x, int y, int w, int ht) {
        IntPtr h = Find(pid);
        if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(1), x, y, w, ht, 0x0011); // HWND_BOTTOM, SWP_NOACTIVATE | SWP_NOSIZE (the size is the game's own business)
    }
}
"@
function TuckAll($list) {
    $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $w = 976; $h = 579; $i = 0   # a 960x540 window with its frame
    foreach ($p in $list) {
        if ($p -and -not $p.HasExited) { [AuditWin]::Tuck([uint32]$p.Id, $area.Right - $w * ($i + 1), $area.Bottom - $h, $w, $h) }
        $i++
    }
}

$hostDir = Prepare "host" $Dll
$guestDir = Prepare "guest" $GuestDll
$made = $false
if (-not (Test-Path "$G\steam_appid.txt")) { [IO.File]::WriteAllText("$G\steam_appid.txt", "311690"); $made = $true }
$procs = @()
try {
    $procs += Launch $hostDir "AuditHost" "host" $HostChar $Lag
    for ($t = 0; $t -lt 6; $t++) { Start-Sleep -Seconds 2; TuckAll $procs }
    $procs += Launch $guestDir "AuditGuest" "join" $GuestChar $Lag
    $end = (Get-Date).AddMinutes($Minutes)
    $ticks = 0
    while ((Get-Date) -lt $end) {
        Start-Sleep -Seconds 2
        if ($ticks -lt 30) { TuckAll $procs }   # the game sets its own size while starting: keep at it for a minute
        $ticks++
        if (@($procs | Where-Object { $_.HasExited }).Count -gt 0) { "A copy exited early."; break }
    }
} finally {
    foreach ($p in $procs) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    Start-Sleep -Seconds 2
    if ($made) { Remove-Item "$G\steam_appid.txt" -Force }
}
# Keep this run's logs under a tag so several runs can be compared.
foreach ($n in "host", "guest") {
    $out = "$A\lan-logs\$Tag-$n"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory -Force $out | Out-Null
    Copy-Item "$A\smoke-$n\BepInEx\LogOutput.log" $out -ErrorAction SilentlyContinue
    Copy-Item "$A\smoke-$n\BepInEx\GungeonOnline-logs\*" $out -ErrorAction SilentlyContinue
}
"steam_appid present after: $(Test-Path "$G\steam_appid.txt")"
"logs: $A\lan-logs\$Tag-host and $Tag-guest"
