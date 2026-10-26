# Starts one copy of Enter the Gungeon with the Gale "Bare" profile loaded, bypassing the Steam launcher.
# Run it twice to get two copies for LAN testing on one PC, e.g.:
#     .\launch-test-copy.ps1 -Name Host
#     .\launch-test-copy.ps1 -Name Guest
# When no copy is running yet, it first installs the newest build into the Bare profile.
#
# Needs steam_appid.txt (containing 311690) next to EtG.exe, otherwise the game closes itself
# when it notices it wasn't started by Steam. Steam must still be running.
# Warning: both copies share the same save files. Stay in the Breach and don't start runs in both.
# -Test host / -Test join: auto-host on LAN / auto-join 127.0.0.1 once the title screen is up.
# -Profile OnlineTest: run another Gale profile (no automatic install there).
# -Bot: this copy plays itself in runs (Take a Break, always on).
param([string]$Name = "", [string]$Test = "", [switch]$AllocProf, [string]$Profile = "Bare", [string]$TestRun = "", [switch]$Bot, [int]$Lag = 0)
$ErrorActionPreference = "Stop"

$GameDir = "S:\_Games\Steamapps\steamapps\common\Enter the Gungeon"
$Preloader = "$env:APPDATA\com.kesomannen.gale\enter-the-gungeon\profiles\$Profile\BepInEx\core\BepInEx.Preloader.dll"

if (-not (Test-Path "$GameDir\steam_appid.txt")) {
    throw "steam_appid.txt is missing from $GameDir. Create it with the text 311690."
}
if (-not (Test-Path $Preloader)) {
    throw "Can't find the Bare profile's BepInEx at $Preloader"
}

$running = @(Get-Process EtG -ErrorAction SilentlyContinue).Count
if ($running -eq 0 -and $Profile -eq "Bare") {
    & "$PSScriptRoot\deploy.ps1"
    # Keep Running (separate mod: no pausing when tabbed out) makes two-copy testing much easier.
    $keepRunning = "$PSScriptRoot\..\..\KeepRunning\deploy.ps1"
    if (Test-Path $keepRunning) { & $keepRunning }
}

$gameArgs = @("--doorstop-enable", "true", "--doorstop-target", "`"$Preloader`"")
if ($Name -ne "") {
    # Both copies share one settings file, so the name for this copy is passed on the command line.
    $gameArgs += @("--gungeon-online-name", "`"$Name`"")
}
if ($AllocProf) {
    $gameArgs += @("--gungeon-online-allocprof")
}
if ($Bot) {
    $gameArgs += @("--gungeon-online-bot")
}
if ($Lag -gt 0) {
    # Test: hold every packet this many ms before sending (35 on both copies = about 70 ms ping).
    $gameArgs += @("--gungeon-online-lag", $Lag)
}
if ($TestRun -ne "") {
    $gameArgs += @("--gungeon-online-test-run", $TestRun)
}
if ($Test -ne "") {
    $gameArgs += @("--gungeon-online-test", $Test)
}
Start-Process -FilePath "$GameDir\EtG.exe" -WorkingDirectory $GameDir -ArgumentList $gameArgs
Write-Host "Started copy #$($running + 1) of Enter the Gungeon ($Profile profile)$(if ($Name) { " as $Name" })." -ForegroundColor Green
