# Wet Reality XR Mod - shared helpers for Install.ps1 and Uninstall.ps1
#
# Dot-sourced, so everything here is a function or a $script: variable and
# nothing runs on load.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The three files that make up the OpenXR payload, with the byte-exact hashes of
# the copies that were verified to work in this game.
#
# The hash is not belt-and-braces here, it is load-bearing. The Unity package
# contains TWO files called openxr_loader.dll:
#
#   RuntimeLoaders/windows/x64/openxr_loader.dll        2373632 bytes  the real one
#   Runtime/MockRuntime/windows/x64/openxr_loader.dll    137216 bytes  a test stub
#
# An installer that searched by filename could pick either, and the mock runtime
# fails in a way that looks like a broken headset rather than a wrong file. So
# the source path is exact AND the result is hashed.
$script:OpenXrVersion = '1.18.0'
$script:OpenXrUrl = 'https://download.packages.unity.com/com.unity.xr.openxr/-/com.unity.xr.openxr-1.18.0.tgz'

$script:OpenXrFiles = @(
    @{
        Source = 'package/RuntimeLoaders/windows/x64/openxr_loader.dll'
        Target = 'PowerWash Simulator 2_Data\Plugins\x86_64\openxr_loader.dll'
        Sha256 = 'F7384429C16288D9C2C123ACFCA1E040C9CEC6C5484A3F5A5107D6AA32BA57E8'
        Size   = 2373632
    },
    @{
        Source = 'package/Runtime/windows/x64/UnityOpenXR.dll'
        Target = 'PowerWash Simulator 2_Data\Plugins\x86_64\UnityOpenXR.dll'
        Sha256 = 'A344BF5428D70D41E91BD36AA85677206A66224949F9C830D8DED4F1D612BE66'
        Size   = 919040
    },
    @{
        Source = 'package/Runtime/UnitySubsystemsManifest.json'
        Target = 'PowerWash Simulator 2_Data\UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json'
        Sha256 = '2528D0CAA5FB4C4EA91ED74FB88315FCA934158314BCEC29D12FBEAAC8E7CFD6'
        Size   = 250
    }
)

# MelonLoader itself. Apache-2.0, so fetching and unpacking it here is fine.
#
# Pinned to the exact version this mod was built and tested against, which also
# removes a whole class of beta report: "it broke after MelonLoader updated".
# Verified against the SHA256 recorded when the archive was first downloaded.
$script:MelonLoader = @{
    Version = 'v0.7.3'
    Url     = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip'
    Sha256  = '5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416'
    # What the archive must produce, checked after extraction.
    Proof   = @('version.dll', 'MelonLoader\net6\MelonLoader.dll')
}

# A PORTABLE .NET 6 runtime, unpacked into the game folder.
#
# This is what makes the install prerequisite-free. MelonLoader needs .NET 6 for
# Il2Cpp games and its own GUI installer puts it on the machine; here it goes
# into <GAME>\dotnet instead, so nothing is installed system-wide, no admin
# rights are needed, and nothing else on the machine is touched.
#
# It has to be major 6. MelonLoader\net6\MelonLoader.runtimeconfig.json asks for
# Microsoft.NETCore.App 6.0.0 with rollForward "LatestMinor", which stays inside
# the requested major - a machine with only .NET 8 or 10 does NOT satisfy it.
#
# 6.0.36 is the last release, and .NET 6 reached end of life in November 2024.
# That is MelonLoader's requirement rather than a choice made here, and keeping
# it inside the game folder is better than installing an EOL runtime
# machine-wide.
#
# URL and SHA512 both come from Microsoft's own release metadata at
# builds.dotnet.microsoft.com/dotnet/release-metadata/6.0/releases.json, and the
# hash was checked against a real download before being written down here.
$script:DotnetRuntime = @{
    Version = '6.0.36'
    Url     = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/6.0.36/dotnet-runtime-6.0.36-win-x64.zip'
    Sha512  = '935DB5C6CEE19F2C016E67168BFAE7B491044735DE76C673ABB3B125DD325FD5E779D7EFE12BA80178D46689AE70A25E558A3FA846417D44C5F4CA256E7F4BF2'
    # Where it goes, relative to the game folder. <GAME>\dotnet is one of the two
    # places MelonLoader 0.7.3 looks for a portable runtime by itself.
    Folder  = 'dotnet'
    # And the exact hostfxr inside it, which Loader.cfg is pointed at so the
    # result does not depend on that auto-discovery working.
    HostFxr = 'dotnet\host\fxr\6.0.36\hostfxr.dll'
    Proof   = @('dotnet.exe', 'host\fxr\6.0.36\hostfxr.dll',
                'shared\Microsoft.NETCore.App\6.0.36\System.Private.CoreLib.dll')
}

$script:ModFiles = @('WetReality.Pose.dll', 'WetReality.XRBoot.dll')

$script:ManifestRelative = 'UserData\WetReality\install-manifest.json'

# Steam app id, needed only by New-CleanTestCopy.ps1: a game launched directly
# rather than through Steam has no way to tell Steamworks which app it is.
$script:AppId = '2968420'

# ------------------------------------------------------------------- utilities

function Say {
    param([string] $Text, [string] $Colour = 'Gray')
    Write-Host $Text -ForegroundColor $Colour
}

function Fail {
    param([string] $Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Get-Sha256 {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

# [System.IO.Path]::Combine, not Join-Path. Join-Path resolves through the
# PowerShell provider and THROWS on a drive that does not exist, and
# libraryfolders.vdf keeps entries for libraries that have been removed. With
# $ErrorActionPreference = 'Stop' one stale entry would kill the installer before
# it printed anything. Combine is pure string work, and Test-Path then simply
# answers false.
function Combine {
    param([string[]] $Parts)
    $path = $Parts[0]
    for ($i = 1; $i -lt $Parts.Count; $i++) { $path = [System.IO.Path]::Combine($path, $Parts[$i]) }
    return $path
}

# ------------------------------------------------------------- text file IO
#
# Explicit UTF-8, because Get-Content without -Encoding reads a file that has no
# BOM in the system ANSI codepage. Writing that back as UTF-8 double-encodes
# every non-ASCII character. Encoding.UTF8 on the way in copes with a BOM or
# none; on the way out the BOM is written, because PowerShell 5.1 and Notepad
# both read a BOM-less file as ANSI.
#
# It matters here because the value written into Loader.cfg is an absolute game
# path, and a Windows account name can carry an umlaut.

function Read-Utf8 {
    param([Parameter(Mandatory)] [string] $Path)
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Write-Utf8 {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyString()] $Content
    )

    if ($Content -is [array]) { $Content = ($Content -join [Environment]::NewLine) }

    [System.IO.File]::WriteAllText($Path, [string] $Content,
        (New-Object System.Text.UTF8Encoding($true)))
}

# ------------------------------------------------------------------ downloads

# Downloads to a temp file and verifies it BEFORE the caller may use it.
function Get-Verified {
    param(
        [string] $Url,
        [string] $Expected,
        [string] $Algorithm,
        [string] $Into,
        [string] $What
    )

    $file = Combine @($Into, ([System.IO.Path]::GetFileName(($Url -split '\?')[0])))

    try {
        # TLS 1.2 explicitly: PowerShell 5.1 still defaults to older protocols on
        # some machines, and both hosts refuse those.
        [System.Net.ServicePointManager]::SecurityProtocol =
            [System.Net.SecurityProtocolType]::Tls12

        $progress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $file -UseBasicParsing
        $ProgressPreference = $progress
    }
    catch {
        Fail ("Could not download $What." + [Environment]::NewLine +
              "  $($_.Exception.Message)" + [Environment]::NewLine + [Environment]::NewLine +
              "Check your internet connection and run the installer again - it picks up " +
              "where it left off.")
    }

    $actual = (Get-FileHash -LiteralPath $file -Algorithm $Algorithm).Hash.ToUpperInvariant()

    if ($actual -ne $Expected.ToUpperInvariant()) {
        Fail ("$What does not match its expected $Algorithm hash, so it was NOT installed." +
              [Environment]::NewLine +
              "  expected  $Expected" + [Environment]::NewLine +
              "  found     $actual" + [Environment]::NewLine + [Environment]::NewLine +
              "Either the download was corrupted - try again - or the file at that address " +
              "has changed. Please report this.")
    }

    return $file
}

# ZipFile rather than Expand-Archive: these archives hold about a thousand files
# and Expand-Archive takes the better part of a minute over them.
function Expand-Zip {
    param([string] $Archive, [string] $Into)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (-not (Test-Path -LiteralPath $Into)) {
        New-Item -ItemType Directory -Path $Into -Force | Out-Null
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory(
        (Resolve-Path -LiteralPath $Archive).Path,
        (Resolve-Path -LiteralPath $Into).Path)
}

# ------------------------------------------------------------------ Loader.cfg
#
# MelonLoader's own configuration. One key is set here:
#
#   hostfxr_path_override = "<GAME>\dotnet\host\fxr\6.0.36\hostfxr.dll"
#
# described in the file itself as "Manually defines the HostFXR path to use for
# DotNet Initialization". MelonLoader 0.7.3 also finds a portable runtime under
# <GAME>\dotnet by itself, but that discovery could not be verified here, so the
# override makes the outcome deterministic either way.
#
# Targeted single-key replacement, like MelonPreferences.cfg: the file belongs to
# MelonLoader, carries its own comments, and is rewritten on its terms.
function Set-HostFxrOverride {
    param([string] $GamePath, [string] $Value)

    $cfg = Combine @($GamePath, 'UserData', 'Loader.cfg')
    # TOML string: backslashes have to be doubled.
    $line = 'hostfxr_path_override = "{0}"' -f ($Value -replace '\\', '\\')

    if (-not (Test-Path -LiteralPath $cfg)) {
        # Not there yet - MelonLoader writes it on first run. A minimal file is
        # valid TOML, and MelonLoader fills in every absent key with its default;
        # that is how new keys arrive in existing configs anyway.
        $folder = Split-Path -Parent $cfg

        if (-not (Test-Path -LiteralPath $folder)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }

        Write-Utf8 -Path $cfg -Content @('[loader]', $line)
        return 'created'
    }

    $lines = @((Read-Utf8 -Path $cfg) -split "`r?`n")
    $done = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*hostfxr_path_override\s*=') {
            $lines[$i] = $line
            $done = $true
        }
    }

    if (-not $done) {
        # Key absent: it has to land inside [loader], not at the end of the file
        # where it would fall into whichever section happens to come last.
        $out = New-Object System.Collections.Generic.List[string]

        foreach ($text in $lines) {
            $out.Add($text)
            if ($text -match '^\s*\[loader\]\s*$') { $out.Add($line) }
        }

        if ($out.Count -eq $lines.Count) { return 'no-loader-section' }

        $lines = @($out.ToArray())
    }

    Write-Utf8 -Path $cfg -Content $lines
    return 'set'
}

# Sets one key in one section of Loader.cfg.
#
# A SIBLING OF Set-HostFxrOverride rather than a rewrite of it. That one writes a
# fixed key into [loader] and is known to work; hide_console lives under
# [console], so it needs the section as an argument, and a release build is the
# wrong moment to refactor the function the installer already depends on.
#
# Returns 'set', 'created' or 'no-section'.
function Set-LoaderCfgKey {
    param(
        [Parameter(Mandatory)] [string] $GamePath,
        [Parameter(Mandatory)] [string] $Section,
        [Parameter(Mandatory)] [string] $Key,
        [Parameter(Mandatory)] [string] $Value
    )

    $cfg = Combine @($GamePath, 'UserData', 'Loader.cfg')
    $line = "$Key = $Value"

    if (-not (Test-Path -LiteralPath $cfg)) {
        $folder = Split-Path -Parent $cfg

        if (-not (Test-Path -LiteralPath $folder)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }

        Write-Utf8 -Path $cfg -Content @("[$Section]", $line)
        return 'created'
    }

    $lines = @((Read-Utf8 -Path $cfg) -split "`r?`n")
    $done = $false

    # ONLY INSIDE THE RIGHT SECTION. Loader.cfg has several, and a bare key match
    # would happily rewrite a same-named key under another heading.
    $current = ''

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[(.+?)\]\s*$') { $current = $Matches[1] }
        elseif ($current -eq $Section -and $lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") {
            $lines[$i] = $line
            $done = $true
        }
    }

    if (-not $done) {
        $out = New-Object System.Collections.Generic.List[string]

        foreach ($text in $lines) {
            $out.Add($text)
            if ($text -match "^\s*\[$([regex]::Escape($Section))\]\s*$") { $out.Add($line) }
        }

        if ($out.Count -eq $lines.Count) { return 'no-section' }

        $lines = @($out.ToArray())
    }

    Write-Utf8 -Path $cfg -Content $lines
    return 'set'
}

# --------------------------------------------------------------- .NET runtime

function Get-DotnetRoot {
    if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath $env:DOTNET_ROOT)) { return $env:DOTNET_ROOT }

    $default = Combine @($env:ProgramFiles, 'dotnet')
    if (Test-Path -LiteralPath $default) { return $default }

    return $null
}

# Answers: can MelonLoader start at all in this game folder?
#
# Returns a hashtable with Ok, Detail and Portable. Never throws - a runtime
# check that breaks the installer would be worse than no check.
function Test-DotnetSix {
    param([string] $GamePath)

    try {
        # Since 0.7.3 MelonLoader accepts a PORTABLE runtime in the game folder,
        # so a system install is not the only way to satisfy this.
        foreach ($relative in @('dotnet', 'MelonLoader\Dependencies\dotnet')) {
            $portable = Combine @($GamePath, $relative)

            if (Test-Path -LiteralPath $portable) {
                return @{ Ok = $true; Portable = $true; Detail = "portable runtime in $relative" }
            }
        }

        $root = Get-DotnetRoot

        if (-not $root) {
            return @{ Ok = $false; Portable = $false; Detail = 'no .NET installation found' }
        }

        $shared = Combine @($root, 'shared', 'Microsoft.NETCore.App')

        if (-not (Test-Path -LiteralPath $shared)) {
            return @{ Ok = $false; Portable = $false; Detail = 'no .NET runtimes installed' }
        }

        $versions = @(Get-ChildItem -LiteralPath $shared -Directory -ErrorAction SilentlyContinue |
                      Select-Object -ExpandProperty Name)
        $six = @($versions | Where-Object { $_ -like '6.*' })

        if ($six.Count -gt 0) {
            return @{ Ok = $true; Portable = $false; Detail = ".NET $($six[-1])" }
        }

        $others = 'none'
        if ($versions.Count -gt 0) { $others = ($versions | Sort-Object) -join ', ' }

        return @{ Ok = $false; Portable = $false; Detail = "found $others, but no 6.x" }
    }
    catch {
        return @{ Ok = $true; Portable = $false; Detail = 'check skipped' }
    }
}

# ---------------------------------------------------------------- installation

function Get-SteamLibraries {
    $libraries = New-Object System.Collections.Generic.List[string]

    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $entry = Get-ItemProperty -Path $key -ErrorAction Stop
            $install = $null

            if ($entry.PSObject.Properties.Name -contains 'SteamPath') { $install = $entry.SteamPath }
            elseif ($entry.PSObject.Properties.Name -contains 'InstallPath') { $install = $entry.InstallPath }

            if ($install) { $libraries.Add(($install -replace '/', '\')) }
        }
        catch {
            # One of the two hives is normally absent.
        }
    }

    $extra = New-Object System.Collections.Generic.List[string]

    foreach ($base in $libraries) {
        $vdf = Combine @($base, 'steamapps', 'libraryfolders.vdf')
        if (-not (Test-Path -LiteralPath $vdf)) { continue }

        foreach ($line in (Get-Content -LiteralPath $vdf)) {
            if ($line -match '"path"\s+"(.+?)"') { $extra.Add(($matches[1] -replace '\\\\', '\')) }
        }
    }

    foreach ($path in $extra) { $libraries.Add($path) }
    return ($libraries | Select-Object -Unique)
}

# The game folder is identified by its EXECUTABLE, not by MelonLoader.
#
# The configurator looks for MelonLoader, because it only has a job to do once
# the mod is installed. The installer has to find the folder BEFORE anything is
# installed, and MelonLoader may well be the thing that is missing.
function Test-GameFolder {
    param([string] $Path)

    if (-not $Path) { return $false }
    return (Test-Path -LiteralPath (Combine @($Path, 'PowerWash Simulator 2.exe')))
}

function Find-GameFolder {
    foreach ($library in Get-SteamLibraries) {
        if (-not $library) { continue }

        $candidate = Combine @($library, 'steamapps', 'common', 'PowerWash Simulator 2')
        if (Test-GameFolder -Path $candidate) { return $candidate }
    }

    return $null
}

function Request-GameFolder {
    Say ''
    Say 'Could not find PowerWash Simulator 2 automatically.' 'Yellow'
    Say 'Please pick the folder that contains "PowerWash Simulator 2.exe".'

    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the "PowerWash Simulator 2" folder'

    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    if (-not (Test-GameFolder -Path $dialog.SelectedPath)) { return $null }

    return $dialog.SelectedPath
}

function Resolve-GameFolder {
    param([string] $Given)

    if ($Given) {
        if (-not (Test-GameFolder -Path $Given)) {
            Fail "That folder does not contain PowerWash Simulator 2.exe:`n  $Given"
        }
        return $Given
    }

    $found = Find-GameFolder
    if ($found) { return $found }

    $picked = Request-GameFolder
    if (-not $picked) { Fail 'No game folder selected. Nothing was changed.' }
    return $picked
}

# ------------------------------------------------------------------- manifest
#
# Records exactly what was installed, with a hash per file, so the uninstaller
# can remove ITS OWN files and leave anything the user has since replaced alone.
# Without this an uninstall is guesswork over a folder it does not own.

function Read-Manifest {
    param([string] $GamePath)

    $path = Combine @($GamePath, $script:ManifestRelative)
    if (-not (Test-Path -LiteralPath $path)) { return $null }

    try {
        return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Write-Manifest {
    param([string] $GamePath, [array] $Entries, [string] $Version)

    $path = Combine @($GamePath, $script:ManifestRelative)
    $folder = Split-Path -Parent $path

    if (-not (Test-Path -LiteralPath $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }

    $payload = [ordered] @{
        version   = $Version
        installed = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        openxr    = $script:OpenXrVersion
        files     = $Entries
    }

    # -Depth matters: the default of 2 would flatten the file entries.
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}
