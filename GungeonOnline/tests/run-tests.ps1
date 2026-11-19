# Runs the LAN reliable-delivery tests without the game: compiles src\LanLink.cs + src\Transport.cs together with
# small stand-ins for the game's classes (Stubs.cs) and runs LanLinkTests.cs. Exit code = number of failed checks.
#     .\run-tests.ps1
$Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Out = "$env:TEMP\GungeonOnline-LanLinkTests.exe"
& $Csc /nologo /target:exe "/out:$Out" "$PSScriptRoot\Stubs.cs" "$PSScriptRoot\LanLinkTests.cs" `
    "$PSScriptRoot\..\src\LanLink.cs" "$PSScriptRoot\..\src\Transport.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Out
exit $LASTEXITCODE
