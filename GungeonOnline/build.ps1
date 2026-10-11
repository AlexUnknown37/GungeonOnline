# Builds GungeonOnline.dll and packs it into a Thunderstore-style zip that Gale
# (or r2modman / Thunderstore Mod Manager) can install next to other mods.
# Uses the C# compiler that ships with Windows, so no extra installs are needed.
# Every build raises the version by one step (0.4.0 -> 0.5.0 -> ... -> 0.10.0). A failed build puts it back.
# Run from PowerShell:  .\build.ps1            (build + zip)
#                       .\build.ps1 -Deploy    (also install into the Gale "Bare" test profile)
param([switch]$Deploy)
$ErrorActionPreference = "Stop"

$GameManaged = "S:\_Games\Steamapps\steamapps\common\Enter the Gungeon\EtG_Data\Managed"
$BepInExCore = "$PSScriptRoot\..\BepInEx\core"
$Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$BinDir = "$PSScriptRoot\bin"
$DistDir = "$PSScriptRoot\dist"
$PackageDir = "$PSScriptRoot\package"

# The version lives in package\manifest.json and Plugin.ModVersion in src\Plugin.cs; both are bumped together.
$ManifestPath = "$PackageDir\manifest.json"
$PluginPath = "$PSScriptRoot\src\Plugin.cs"
$oldManifest = [IO.File]::ReadAllText($ManifestPath)
$oldPlugin = [IO.File]::ReadAllText($PluginPath)
$previous = ($oldManifest | ConvertFrom-Json).version_number
if (-not $oldPlugin.Contains("ModVersion = `"$previous`"")) {
    throw "Version mismatch: manifest.json says $previous but Plugin.ModVersion in src\Plugin.cs differs. Make them equal."
}
$parts = $previous.Split(".")
$version = "$($parts[0]).$([int]$parts[1] + 1).0"
[IO.File]::WriteAllText($ManifestPath, $oldManifest.Replace("`"$previous`"", "`"$version`""))
[IO.File]::WriteAllText($PluginPath, $oldPlugin.Replace("ModVersion = `"$previous`"", "ModVersion = `"$version`""))

New-Item -ItemType Directory -Force $BinDir, $DistDir | Out-Null

# The game runs an old Mono (.NET 3.5), so compile against the game's own mscorlib/System DLLs.
$refs = @("mscorlib.dll", "System.dll", "System.Core.dll", "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll") |
    ForEach-Object { "/reference:$GameManaged\$_" }
$refs += Get-ChildItem "$GameManaged\UnityEngine*.dll" | Where-Object { $_.Name -notlike "*.mm.dll" } | ForEach-Object { "/reference:$($_.FullName)" }
$refs += "/reference:$BepInExCore\BepInEx.dll", "/reference:$BepInExCore\0Harmony.dll"

$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }

& $Csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /debug:pdbonly /utf8output `
    "/out:$BinDir\GungeonOnline.dll" $refs $sources
if ($LASTEXITCODE -ne 0) {
    [IO.File]::WriteAllText($ManifestPath, $oldManifest)
    [IO.File]::WriteAllText($PluginPath, $oldPlugin)
    throw "Build failed (version stays $previous)."
}
Write-Host "Built GungeonOnline.dll $version" -ForegroundColor Green

# Thunderstore layout: manifest.json, icon.png, README.md at the root, the DLL under plugins\.
$stage = "$BinDir\package"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\plugins" | Out-Null
Copy-Item "$PackageDir\manifest.json", "$PackageDir\icon.png", "$PackageDir\README.md" $stage
Copy-Item "$BinDir\GungeonOnline.dll" "$stage\plugins"

$zip = "$DistDir\GungeonOnline-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Write the zip by hand: PowerShell 5.1's Compress-Archive stores "plugins\x.dll" with a backslash,
# which Thunderstore and mod managers don't treat as a folder.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, "Create")
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace("\", "/")
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "Packaged $zip" -ForegroundColor Green

if ($Deploy) {
    & "$PSScriptRoot\deploy.ps1"
}
