# Installs the most recent build (bin\package, made by build.ps1) into the Gale "Bare" test profile.
# The game locks the mod file while running, so every copy of the game must be closed.
$ErrorActionPreference = "Stop"

$Stage = "$PSScriptRoot\bin\package"
$TestProfile = "$env:APPDATA\com.kesomannen.gale\enter-the-gungeon\profiles\Bare\BepInEx\plugins\Gungeon_Online-dev"

if (-not (Test-Path "$Stage\plugins\GungeonOnline.dll")) {
    throw "No build found. Run .\build.ps1 first."
}
if (Get-Process EtG -ErrorAction SilentlyContinue) {
    throw "Enter the Gungeon is still running, so the mod file is locked. Close every copy of the game first."
}

if (Test-Path $TestProfile) { Remove-Item $TestProfile -Recurse -Force }
New-Item -ItemType Directory -Force $TestProfile | Out-Null
Copy-Item "$Stage\*" $TestProfile -Recurse
$version = (Get-Content "$Stage\manifest.json" -Raw | ConvertFrom-Json).version_number
Write-Host "Installed Gungeon Online $version into the Bare profile." -ForegroundColor Green
