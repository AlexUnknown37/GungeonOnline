# Compiles only, to check for errors (no version bump, no zip). Output goes to a scratch DLL.
$GameManaged = "S:\_Games\Steamapps\steamapps\common\Enter the Gungeon\EtG_Data\Managed"
$BepInExCore = "$PSScriptRoot\..\BepInEx\core"
$Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = @("mscorlib.dll", "System.dll", "System.Core.dll", "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PlayMaker.dll") |
    ForEach-Object { "/reference:$GameManaged\$_" }
$refs += Get-ChildItem "$GameManaged\UnityEngine*.dll" | Where-Object { $_.Name -notlike "*.mm.dll" } | ForEach-Object { "/reference:$($_.FullName)" }
$refs += "/reference:$BepInExCore\BepInEx.dll", "/reference:$BepInExCore\0Harmony.dll"
$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }
& $Csc /nologo /noconfig /nostdlib+ /target:library /utf8output "/out:$env:TEMP\GungeonOnline-check.dll" $refs $sources
exit $LASTEXITCODE
