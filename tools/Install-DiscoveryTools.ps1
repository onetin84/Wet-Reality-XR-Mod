# Der Standardort von Steam als Vorgabe. Liegt das Spiel woanders, den Pfad
# uebergeben: .\Install-DiscoveryTools.ps1 -GamePath 'D:\...'
param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\PowerWash Simulator 2')
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GamePath).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'PowerWash Simulator 2.exe'))) { throw 'PWS2 executable missing.' }
if (Get-Process -Name 'PowerWash Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close PWS2 before installing.' }
foreach ($marker in @('version.dll','winhttp.dll','dobby.dll','MelonLoader','BepInEx')) {
    if (Test-Path -LiteralPath (Join-Path $gameRoot $marker)) { throw "Existing loader file or directory: $marker. Inspect manually before proceeding." }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'downloads\manifest.json') -Raw | ConvertFrom-Json
$plannedFiles = @()
foreach ($package in $manifest) {
    $archivePath = Join-Path $PSScriptRoot "downloads\$($package.Asset)"
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $package.SHA256) { throw "Archive hash mismatch: $archivePath" }
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue }
            $targetPath = [IO.Path]::GetFullPath((Join-Path $gameRoot $entry.FullName))
            if (-not $targetPath.StartsWith($gameRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes game directory.' }
            if (Test-Path -LiteralPath $targetPath) { throw "Refusing to overwrite: $targetPath" }
            if ($plannedFiles.Target -contains $targetPath) { throw "Duplicate archive target: $targetPath" }
            $plannedFiles += [pscustomobject]@{Archive=$archivePath;Entry=$entry.FullName;Target=$targetPath}
        }
    } finally { $archive.Dispose() }
}
# Save the exact additive installation plan before writing to the game directory.
$plannedFiles | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'discovery-install-plan.json') -Encoding utf8
$installed = @()
foreach ($package in $manifest) {
    $archivePath = Join-Path $PSScriptRoot "downloads\$($package.Asset)"
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($file in ($plannedFiles | Where-Object Archive -eq $archivePath)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($file.Entry), $file.Target, $false)
            $installed += [pscustomobject]@{Path=$file.Target;SHA256=(Get-FileHash -LiteralPath $file.Target -Algorithm SHA256).Hash}
            $installed | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'discovery-installed-files.json') -Encoding utf8
        }
    } finally { $archive.Dispose() }
}
Write-Output "Installed and hashed $($installed.Count) files into $gameRoot"
