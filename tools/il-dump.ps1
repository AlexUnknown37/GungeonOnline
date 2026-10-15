# Prints the IL (compiled instructions) of game methods, so we can read how the game does something.
# Usage: powershell -File il-dump.ps1 -Type GameManager -Method LoadCharacterSelect
#        (add -Dll Assembly-CSharp-firstpass.dll for the other game DLL; -Method accepts a regex)
#        For another mod's DLL pass its full path: -Dll "C:\...\plugins\Some_Mod\Mod.dll" (references are
#        found in the game folder and the Bare profile's BepInEx folders).
param(
    [Parameter(Mandatory = $true)][string]$Type,
    [Parameter(Mandatory = $true)][string]$Method,
    [string]$Dll = "Assembly-CSharp.dll",
    [string]$Managed = "S:\_Games\Steamapps\steamapps\common\Enter the Gungeon\EtG_Data\Managed"
)

$profileDir = "$env:APPDATA\com.kesomannen.gale\enter-the-gungeon\profiles\Bare\BepInEx"
$global:searchDirs = @($Managed) + @(Get-ChildItem "$profileDir\core", "$profileDir\plugins", "$profileDir\monomod" -Directory -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }) + @("$profileDir\core")
$global:resolved = @{}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($s, $e)
    $n = $e.Name.Split(',')[0]
    if ($global:resolved.ContainsKey($n)) { return $global:resolved[$n] }
    $found = $null
    foreach ($d in $global:searchDirs) {
        $p = [IO.Path]::Combine($d, "$n.dll")
        if ([IO.File]::Exists($p)) { $found = [Reflection.Assembly]::ReflectionOnlyLoadFrom($p); break }
    }
    if ($found -eq $null) { $found = [Reflection.Assembly]::ReflectionOnlyLoad($e.Name) }
    $global:resolved[$n] = $found
    return $found
})
$dllPath = if ([IO.Path]::IsPathRooted($Dll)) { $Dll } else { "$Managed\$Dll" }
$asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($dllPath)

# Build opcode lookup tables (one-byte and two-byte 0xFE xx opcodes).
$one = @{}; $two = @{}
foreach ($f in [Reflection.Emit.OpCodes].GetFields()) {
    $op = $f.GetValue($null)
    $v = [int]($op.Value -band 0xFFFF)
    if ($op.Size -eq 1) { $one[$v] = $op } else { $two[$v -band 0xFF] = $op }
}

function Describe-Member($mod, $tok) {
    try {
        $mb = $mod.ResolveMember($tok)
        if ($mb -is [Reflection.MethodBase]) {
            $ps = ($mb.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ", "
            return "$($mb.DeclaringType.FullName)::$($mb.Name)($ps)"
        }
        if ($mb -is [Reflection.FieldInfo]) { return "$($mb.DeclaringType.FullName)::$($mb.Name) [$($mb.FieldType.Name)]" }
        if ($mb -is [Type]) { return $mb.FullName }
        return $mb.ToString()
    } catch { return ("token 0x{0:X8}" -f $tok) }
}

$bf = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance,DeclaredOnly'
$t = $asm.GetType($Type)
if (-not $t) { throw "Type $Type not found" }
# Include compiler-generated nested types (coroutines are state machines named <Method>c__Iterator...).
$targets = @($t.GetMethods($bf) + $t.GetConstructors($bf) | Where-Object { $_.Name -match $Method })
foreach ($nt in $t.GetNestedTypes($bf)) {
    if ($nt.Name -match $Method) { $targets += $nt.GetMethods($bf) | Where-Object { $_.Name -eq "MoveNext" } }
}

foreach ($mt in $targets) {
    $body = $mt.GetMethodBody()
    if (-not $body) { continue }
    "==== $($mt.DeclaringType.FullName)::$($mt)"
    $il = $body.GetILAsByteArray(); $mod = $mt.Module; $i = 0
    while ($i -lt $il.Length) {
        $start = $i
        $b = $il[$i++]
        if ($b -eq 0xFE) { $op = $two[[int]$il[$i++]] } else { $op = $one[[int]$b] }
        if (-not $op) { "  IL_{0:X4}: ??? {1:X2}" -f $start, $b; continue }
        $arg = ""
        switch ($op.OperandType.ToString()) {
            "InlineNone" { }
            "ShortInlineBrTarget" { $d = [sbyte]$il[$i]; $i += 1; $arg = "IL_{0:X4}" -f ($i + $d) }
            "ShortInlineI" { $arg = [sbyte]$il[$i]; $i += 1 }
            "ShortInlineVar" { $arg = $il[$i]; $i += 1 }
            "InlineVar" { $arg = [BitConverter]::ToUInt16($il, $i); $i += 2 }
            "InlineBrTarget" { $d = [BitConverter]::ToInt32($il, $i); $i += 4; $arg = "IL_{0:X4}" -f ($i + $d) }
            "InlineI" { $arg = [BitConverter]::ToInt32($il, $i); $i += 4 }
            "InlineI8" { $arg = [BitConverter]::ToInt64($il, $i); $i += 8 }
            "ShortInlineR" { $arg = [BitConverter]::ToSingle($il, $i); $i += 4 }
            "InlineR" { $arg = [BitConverter]::ToDouble($il, $i); $i += 8 }
            "InlineString" { $arg = '"' + $mod.ResolveString([BitConverter]::ToInt32($il, $i)) + '"'; $i += 4 }
            "InlineSwitch" { $n = [BitConverter]::ToInt32($il, $i); $i += 4 + 4 * $n; $arg = "($n cases)" }
            default { $arg = Describe-Member $mod ([BitConverter]::ToInt32($il, $i)); $i += 4 }
        }
        "  IL_{0:X4}: {1} {2}" -f $start, $op.Name, $arg
    }
}
