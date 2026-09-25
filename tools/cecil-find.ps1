param([string]$Dll, [string]$Pattern)
# Read-only type/member listing of an interop assembly via MelonLoader's Mono.Cecil.
Add-Type -Path 'F:\SteamLibrary\steamapps\common\PowerWash Simulator 2\MelonLoader\net472\Mono.Cecil.dll'
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($Dll)
foreach ($type in $module.GetTypes()) {
    $typeHit = $type.FullName -match $Pattern
    $hits = @()
    foreach ($m in $type.Methods) {
        if ($m.Name -like 'NativeMethodInfoPtr_*' -or $m.Name -like '.c*') { continue }
        if ($typeHit -or $m.Name -match $Pattern) {
            $vis = if ($m.IsPublic) { 'public' } else { 'nonpub' }
            $st = if ($m.IsStatic) { ' static' } else { '' }
            $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ', '
            $hits += "    $vis$st $($m.ReturnType.Name) $($m.Name)($ps)"
        }
    }
    if ($hits.Count -gt 0) {
        $base = if ($type.BaseType) { $type.BaseType.FullName } else { '-' }
        "== $($type.FullName) : $base"
        $hits
    }
}
