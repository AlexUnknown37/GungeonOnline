# Audit helper: sends Mod the Gungeon API console commands to a running test copy (started by lan-test.ps1).
# Usage: game-cmd.ps1 -To host "give shotgun" "wait 1" "spawn bullet_kin"
#        -To host | guest | both. Commands run in order, about four a second; "wait N" pauses N seconds.
param(
    [Parameter(Mandatory = $true)][ValidateSet("host", "guest", "both")][string]$To,
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Commands
)
$A = if ($env:GUNGEON_AUDIT_DIR) { $env:GUNGEON_AUDIT_DIR } else { "C:\Projects\GungeonOnline-audit" }
$targets = if ($To -eq "both") { "host", "guest" } else { $To }
foreach ($t in $targets) {
    $f = "$A\smoke-$t\BepInEx\audit-commands.txt"
    $tmp = "$f.tmp"
    $old = if (Test-Path $f) { @(Get-Content $f) } else { @() }   # not picked up yet: keep the order
    [IO.File]::WriteAllLines($tmp, [string[]]($old + $Commands))
    Move-Item $tmp $f -Force
}
