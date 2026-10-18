# Prints the -Index (start order) of the test copy started with --gungeon-online-name <Name>, for the other tools.
param([Parameter(Mandatory = $true)][string]$Name)
$procs = @(Get-Process EtG -ErrorAction Stop | Sort-Object StartTime)
for ($i = 0; $i -lt $procs.Count; $i++) {
    $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($procs[$i].Id)").CommandLine
    if ($cmd -match "--gungeon-online-name `"?$Name`"?(\s|$)") { $i; exit 0 }
}
exit 1
