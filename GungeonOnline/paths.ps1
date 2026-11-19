# Shared by build.ps1 and check.ps1: finds the game's Managed folder and BepInEx's core folder.
# Order: environment variables (GUNGEON_MANAGED, GUNGEON_BEPINEX_CORE), the usual places, Steam's own library list.
# Dot-source it:  . "$PSScriptRoot\paths.ps1"
$Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

function Find-GameManaged {
    if ($env:GUNGEON_MANAGED -and (Test-Path "$env:GUNGEON_MANAGED\Assembly-CSharp.dll")) { return $env:GUNGEON_MANAGED }
    $candidates = @("S:\_Games\Steamapps\steamapps\common\Enter the Gungeon")
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam") {
        $steam = $null
        try { $p = Get-ItemProperty $key -ErrorAction Stop; $steam = if ($p.SteamPath) { $p.SteamPath } else { $p.InstallPath } } catch { }
        if (-not $steam) { continue }
        $candidates += "$steam\steamapps\common\Enter the Gungeon"
        $vdf = "$steam\steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $candidates += ($m.Groups[1].Value -replace '\\\\', '\') + "\steamapps\common\Enter the Gungeon"
            }
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path "$c\EtG_Data\Managed\Assembly-CSharp.dll") { return "$c\EtG_Data\Managed" }
    }
    throw "Can't find Enter the Gungeon. Set the GUNGEON_MANAGED environment variable to its EtG_Data\Managed folder."
}

function Find-BepInExCore {
    if ($env:GUNGEON_BEPINEX_CORE -and (Test-Path "$env:GUNGEON_BEPINEX_CORE\BepInEx.dll")) { return $env:GUNGEON_BEPINEX_CORE }
    $candidates = @("$PSScriptRoot\..\BepInEx\core")
    $gale = "$env:APPDATA\com.kesomannen.gale"
    $candidates += "$gale\enter-the-gungeon\profiles\Bare\BepInEx\core", "$gale\enter-the-gungeon\profiles\Default\BepInEx\core"
    if (Test-Path "$gale\cache\BepInEx-BepInExPack_EtG") {
        $candidates += Get-ChildItem "$gale\cache\BepInEx-BepInExPack_EtG" -Directory | ForEach-Object { "$($_.FullName)\BepInEx\core" }
    }
    foreach ($c in $candidates) {
        if (Test-Path "$c\BepInEx.dll") { return $c }
    }
    throw "Can't find BepInEx. Set the GUNGEON_BEPINEX_CORE environment variable to a BepInEx\core folder (BepInExPack for EtG)."
}

# The compiler arguments naming every DLL the mod is built against.
function Get-References {
    $managed = Find-GameManaged
    $core = Find-BepInExCore
    # The game runs an old Mono (.NET 3.5), so compile against the game's own mscorlib/System DLLs.
    $refs = @("mscorlib.dll", "System.dll", "System.Core.dll", "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PlayMaker.dll") |
        ForEach-Object { "/reference:$managed\$_" }
    $refs += Get-ChildItem "$managed\UnityEngine*.dll" | Where-Object { $_.Name -notlike "*.mm.dll" } | ForEach-Object { "/reference:$($_.FullName)" }
    $refs += "/reference:$core\BepInEx.dll", "/reference:$core\0Harmony.dll"
    return $refs
}
