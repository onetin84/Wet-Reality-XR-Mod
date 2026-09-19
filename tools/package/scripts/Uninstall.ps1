# Wet Reality XR Mod - uninstaller
#
# Removes ONLY what this mod's installer created, identified through the manifest
# it wrote.
#
# Three kinds of entry, each undone its own way:
#
#   file      removed only while it still has the hash it was installed with.
#             One the user has since replaced is left alone and reported.
#   tree      a whole directory the installer unpacked - MelonLoader\ and
#             dotnet\. Recorded ONLY when the installer created it, so a
#             MelonLoader that was already there is never in the manifest and
#             therefore never removed.
#   cfg-key   a single key written into MelonLoader's Loader.cfg, reset to empty
#             rather than deleted - the file is MelonLoader's.

[CmdletBinding()]
param(
    [string] $GamePath,
    # Also deletes MelonPreferences.cfg. Off by default - those are the user's
    # own settings, and a reinstall should find them again.
    [switch] $RemoveSettings
)

. (Join-Path $PSScriptRoot 'Common.ps1')

Write-Host ''
Say '  Wet Reality XR Mod - Uninstaller' 'Cyan'
Write-Host ''

$game = Resolve-GameFolder -Given $GamePath
Say "Game folder:  $game"

$running = @(Get-Process -Name 'PowerWash Simulator 2' -ErrorAction SilentlyContinue)

if ($running.Count -gt 0) {
    Fail 'PowerWash Simulator 2 is running. Please close the game and try again.'
}

$manifest = Read-Manifest -GamePath $game

if (-not $manifest) {
    Write-Host ''
    Say 'No installation record found in this game folder.' 'Yellow'
    Say 'Either the mod was never installed here, or it was installed by hand.'
    Write-Host ''
    Say 'Nothing was removed. If you installed by hand, delete these yourself:'
    Say '  Mods\WetReality.Pose.dll'
    Say '  Mods\WetReality.XRBoot.dll'
    Say '  PowerWash Simulator 2_Data\Plugins\x86_64\openxr_loader.dll'
    Say '  PowerWash Simulator 2_Data\Plugins\x86_64\UnityOpenXR.dll'
    Say '  PowerWash Simulator 2_Data\UnitySubsystems\UnityOpenXR\'
    Say '  dotnet\   (only if you let the installer unpack a runtime there)'
    Write-Host ''
    exit 1
}

Write-Host ''
Say "Installed version $($manifest.version) on $($manifest.installed)"
Write-Host ''

$removed = 0
$kept = 0
$gone = 0
$folders = New-Object System.Collections.Generic.List[string]

foreach ($entry in $manifest.files) {
    $target = Combine @($game, $entry.path)

    # Older manifests have no "kind" - everything in them was a file.
    $kind = 'file'

    if ($entry.PSObject.Properties.Name -contains 'kind' -and $entry.kind) {
        $kind = $entry.kind
    }

    # A key inside a file that belongs to MelonLoader. Emptied, never deleted,
    # and the rest of the file is left byte for byte.
    if ($kind -eq 'cfg-key') {
        if (-not (Test-Path -LiteralPath $target)) {
            Say "  already gone  $($entry.path)" 'DarkGray'
            $gone++
            continue
        }

        $lines = @((Read-Utf8 -Path $target) -split "`r?`n")
        $touched = $false

        # WHAT TO PUT BACK, per entry. The hostfxr path is a TOML string and an
        # empty one is the right neutral value; hide_console is a boolean, and
        # hide_console = "" would stop MelonLoader parsing its own config. So an
        # entry may name its own restore value, and the empty string stays the
        # default for the entries written before this existed.
        $restore = if ($entry.PSObject.Properties.Name -contains 'restore') {
            [string] $entry.restore
        }
        else {
            '""'
        }

        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^\s*$([regex]::Escape($entry.key))\s*=") {
                $lines[$i] = "$($entry.key) = $restore"
                $touched = $true
            }
        }

        if ($touched) {
            Write-Utf8 -Path $target -Content $lines
            Say "  reset  $($entry.path)  ->  $($entry.key) = $restore"
            $removed++
        }
        else {
            Say "  nothing to reset  $($entry.path)" 'DarkGray'
            $gone++
        }

        continue
    }

    if (-not (Test-Path -LiteralPath $target)) {
        Say "  already gone  $($entry.path)" 'DarkGray'
        $gone++
        continue
    }

    # A directory the installer unpacked. It is in the manifest only because the
    # installer created it, so removing it wholesale takes back exactly what was
    # added - there is no user file inside to lose.
    if ($kind -eq 'tree') {
        $count = @(Get-ChildItem -LiteralPath $target -Recurse -File -Force -ErrorAction SilentlyContinue).Count
        Remove-Item -LiteralPath $target -Recurse -Force
        Say "  removed  $($entry.path)\  ($count files)"
        $removed++
        continue
    }

    $hash = Get-Sha256 -Path $target

    if ($hash -ne $entry.sha256) {
        # Changed since the install. It might be a newer mod build the user
        # copied in, or a file another tool now owns. Deleting it would destroy
        # something this uninstaller did not put there.
        Say "  changed, kept  $($entry.path)" 'Yellow'
        $kept++
        continue
    }

    Remove-Item -LiteralPath $target -Force
    Say "  removed  $($entry.path)"
    $removed++

    $folders.Add((Split-Path -Parent $target))
}

# Directories the installer created, and only if they are now empty. The
# Plugins\x86_64 folder belongs to the game and always has files left in it, so
# it simply fails the emptiness test rather than needing a special case.
foreach ($folder in ($folders | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $folder)) { continue }

    $contents = @(Get-ChildItem -LiteralPath $folder -Force)

    if ($contents.Count -eq 0) {
        Remove-Item -LiteralPath $folder -Force
        Say "  removed empty folder  $folder" 'DarkGray'
    }
}

# ---------------------------------------------------------------- the settings

$cfg = Combine @($game, 'UserData', 'MelonPreferences.cfg')

if ($RemoveSettings) {
    foreach ($path in @($cfg, "$cfg.bak")) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
            Say "  removed  $(Split-Path -Leaf $path)"
        }
    }
}
elseif (Test-Path -LiteralPath $cfg) {
    Say ''
    Say "  kept  UserData\MelonPreferences.cfg  (your settings; -RemoveSettings deletes it)" 'DarkGray'
}

# The manifest goes last, so an interrupted run can be repeated.
$manifestPath = Combine @($game, $script:ManifestRelative)

if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }

Write-Host ''
Say "  Done. $removed removed, $kept kept because they had changed, $gone were already gone." 'Green'

if ($kept -gt 0) {
    Say '  The kept files are not the ones this package installed - remove them by hand if you want them gone.' 'Yellow'
}

Write-Host ''

# Only meaningful when MelonLoader was NOT ours to remove - which is exactly
# when it is absent from the manifest.
$ours = @($manifest.files | Where-Object { $_.path -eq 'MelonLoader' }).Count -gt 0

if (-not $ours) {
    Say '  MelonLoader was already there before this mod, so it was left installed.' 'DarkGray'
    Say '  Use its own installer to remove it.' 'DarkGray'
    Write-Host ''
}
