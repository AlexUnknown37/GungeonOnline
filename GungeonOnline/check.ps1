# Compiles only, to check for errors (no version bump, no zip). Output goes to a scratch DLL.
. "$PSScriptRoot\paths.ps1"
$refs = Get-References
$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }
& $Csc /nologo /noconfig /nostdlib+ /target:library /utf8output "/out:$env:TEMP\GungeonOnline-check.dll" $refs $sources
exit $LASTEXITCODE
