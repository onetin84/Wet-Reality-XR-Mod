# Wet Reality XR Mod - installer
#
# ONE STEP FOR THE USER: run this, then start the game. Nothing to install
# beforehand, no version to choose, no administrator rights.
#
# To get there it puts four things in place, each from its own source and each
# verified against a known hash before it is allowed near the game folder:
#
#   MelonLoader v0.7.3     the mod loader           Apache-2.0, from GitHub
#   .NET 6.0.36            what MelonLoader runs on, PORTABLE, into <GAME>\dotnet
#   Unity OpenXR 1.18.0    three files the game lacks, from Unity's registry
#   two mod assemblies     from this package
#
# The runtime is what makes one step possible at all. MelonLoader needs .NET 6
# for Il2Cpp games, and its own GUI installer puts that on the machine - which
# costs the user a download, an unsigned-publisher warning, a version to pick and
# an installer to sit through. Unpacked into the game folder instead it is
# invisible: nothing system-wide, no admin rights, and it leaves with the mod.
#
# Whatever is already present and correct is left alone, so re-running this is
# cheap, and it is also how a failed download gets retried.
#
# It does NOT write MelonPreferences.cfg. The mod creates that on its first run
# with its own defaults, which are the values that were play-tested; a copy of
# them here would only be a second place to go stale.

[CmdletBinding()]
param(
    [string] $GamePath,
    # Re-fetch and re-verify even what is already in place.
    [switch] $Force,
    # Use a .NET 6 already on the machine rather than unpacking the portable one.
    [switch] $SystemDotnet
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$ModSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'mod'

Write-Host ''
Say '  Wet Reality XR Mod - Installer' 'Cyan'
Say '  PowerWash Simulator 2 in room-scale VR'
Write-Host ''

# ------------------------------------------------------------------- 1. checks
#
# Every one of them before anything at all is written.

if (-not (Test-Path -LiteralPath $ModSource)) {
    Fail "The 'mod' folder is missing next to the scripts. Please unpack the whole archive, not just the scripts."
}

foreach ($name in $script:ModFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ModSource $name))) {
        Fail "$name is missing from the 'mod' folder. Please unpack the archive again."
    }
}

$game = Resolve-GameFolder -Given $GamePath
Say "Game folder:  $game"

# A running game holds its DLLs open, so a copy would fail halfway and leave a
# half-installed state behind.
$running = @(Get-Process -Name 'PowerWash Simulator 2' -ErrorAction SilentlyContinue)

if ($running.Count -gt 0) {
    Fail 'PowerWash Simulator 2 is running. Please close the game and start the installer again.'
}

# Writable? A library under Program Files needs elevation, and learning that from
# a copy that already failed halfway is far worse than learning it now. Tested by
# actually writing, because permissions on Windows cannot be predicted from a
# path.
try {
    $modsFolder = Combine @($game, 'Mods')

    if (-not (Test-Path -LiteralPath $modsFolder)) {
        New-Item -ItemType Directory -Path $modsFolder -Force | Out-Null
    }

    $probe = Combine @($modsFolder, '.wetreality-write-test')
    Set-Content -LiteralPath $probe -Value 'x' -Encoding ASCII
    Remove-Item -LiteralPath $probe -Force
}
catch {
    Fail ("Cannot write into the game folder:" + [Environment]::NewLine + "  $game" +
          [Environment]::NewLine + [Environment]::NewLine +
          "Close anything that may be using it, or right-click Install.cmd and choose " +
          "'Run as administrator'.")
}

$entries = New-Object System.Collections.Generic.List[object]
$temp = Join-Path $env:TEMP ('wetreality-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {

    # ---------------------------------------------------------- 2. MelonLoader

    Write-Host ''
    Say 'Mod loader' 'Cyan'

    if (Test-Path -LiteralPath (Combine @($game, 'MelonLoader'))) {
        # Left completely alone: it may be carrying other mods, and it may be a
        # version the user picked deliberately.
        $installed = '(unknown version)'
        $dll = Combine @($game, 'MelonLoader', 'net6', 'MelonLoader.dll')

        if (Test-Path -LiteralPath $dll) {
            $installed = 'v' + [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
        }

        Say "  MelonLoader already installed, $installed - left untouched"

        if ($installed -notlike 'v0.7.*') {
            Say "  Tested against $($script:MelonLoader.Version). Yours differs; if the game" 'Yellow'
            Say '  misbehaves, suspect that first.' 'Yellow'
        }
    }
    else {
        Say "  installing MelonLoader $($script:MelonLoader.Version)  (about 20 MB)"

        $archive = Get-Verified -Url $script:MelonLoader.Url `
                                -Expected $script:MelonLoader.Sha256 `
                                -Algorithm SHA256 -Into $temp -What 'MelonLoader'

        # The archive's root is MelonLoader\ plus version.dll, which is exactly
        # the layout the game folder wants.
        Expand-Zip -Archive $archive -Into $game

        foreach ($proof in $script:MelonLoader.Proof) {
            if (-not (Test-Path -LiteralPath (Combine @($game, $proof)))) {
                Fail "MelonLoader did not unpack correctly - $proof is missing."
            }
        }

        # Recorded as a TREE, not as hundreds of separate files. The uninstaller
        # removes a tree only when this installer is what created it.
        $entries.Add([ordered] @{ path = 'MelonLoader'; kind = 'tree'; source = 'melonloader' })
        $entries.Add([ordered] @{ path = 'version.dll'; kind = 'file'
                                  sha256 = (Get-Sha256 -Path (Combine @($game, 'version.dll')))
                                  source = 'melonloader' })

        Say '  installed  version.dll + MelonLoader\'
    }

    # -------------------------------------------------------------- 3. runtime

    Write-Host ''
    Say '.NET runtime' 'Cyan'

    $dotnet = Test-DotnetSix -GamePath $game

    if ($dotnet.Ok -and -not $Force) {
        Say "  already satisfied  ($($dotnet.Detail))"
    }
    elseif ($SystemDotnet) {
        Say '  -SystemDotnet given, so nothing is unpacked.' 'Yellow'
        Say "  $($dotnet.Detail)" 'DarkGray'
        Say '  Install the .NET 6 Desktop Runtime (x64) from' 'Yellow'
        Say '      https://dotnet.microsoft.com/download/dotnet/6.0' 'Cyan'
    }
    else {
        Say "  unpacking .NET $($script:DotnetRuntime.Version) into the game folder  (about 32 MB)"
        Say '  Nothing is installed on your system and no admin rights are needed.' 'DarkGray'

        $archive = Get-Verified -Url $script:DotnetRuntime.Url `
                                -Expected $script:DotnetRuntime.Sha512 `
                                -Algorithm SHA512 -Into $temp -What 'the .NET 6 runtime'

        $into = Combine @($game, $script:DotnetRuntime.Folder)

        if (Test-Path -LiteralPath $into) { Remove-Item -LiteralPath $into -Recurse -Force }

        Expand-Zip -Archive $archive -Into $into

        foreach ($proof in $script:DotnetRuntime.Proof) {
            if (-not (Test-Path -LiteralPath (Combine @($into, $proof)))) {
                Fail "The .NET runtime did not unpack correctly - $proof is missing."
            }
        }

        $entries.Add([ordered] @{ path = $script:DotnetRuntime.Folder
                                  kind = 'tree'; source = 'dotnet' })

        # Point MelonLoader at it explicitly. Its own discovery of <GAME>\dotnet
        # ought to find this too, but that could not be verified here, and a beta
        # should not rest on "ought to".
        $result = Set-HostFxrOverride -GamePath $game `
                                      -Value (Combine @($game, $script:DotnetRuntime.HostFxr))

        if ($result -eq 'no-loader-section') {
            Say '  No [loader] section in UserData\Loader.cfg, so no override was set.' 'Yellow'
            Say '  MelonLoader should still find the runtime by itself; if the game does' 'Yellow'
            Say '  not start, delete Loader.cfg and run this installer again.' 'Yellow'
        }
        else {
            $entries.Add([ordered] @{ path = 'UserData\Loader.cfg'; kind = 'cfg-key'
                                      key = 'hostfxr_path_override'; source = 'dotnet' })
            Say "  unpacked  dotnet\  and pointed MelonLoader at it  ($result)"
        }

        if (-not (Test-DotnetSix -GamePath $game).Ok) {
            Fail 'The runtime unpacked but cannot be found afterwards. Please report this.'
        }
    }

    # -------------------------------------------------- 3b. the console window

    # OUT OF THE WAY FOR A BETA. MelonLoader opens a console window at the top
    # left of the screen, which is the first thing a tester sees and has nothing
    # in it they can act on. The mod's own logs still go to Latest.log.
    #
    # Written into the user's Loader.cfg rather than shipped as a file, because
    # shipping one would overwrite every other setting in it. Recorded as a
    # cfg-key entry so the uninstaller puts it back - with 'false', not the empty
    # string the hostfxr path is reset to; this key is a boolean and MelonLoader
    # would not parse hide_console = "".
    $consoleResult = Set-LoaderCfgKey -GamePath $game -Section 'console' `
                                      -Key 'hide_console' -Value 'true'

    if ($consoleResult -eq 'no-section') {
        Say '  No [console] section in UserData\Loader.cfg, so the console stays visible.' 'Yellow'
    }
    else {
        $entries.Add([ordered] @{ path = 'UserData\Loader.cfg'; kind = 'cfg-key'
                                 key = 'hide_console'; restore = 'false'; source = 'console' })
        Say "  hid the MelonLoader console window  ($consoleResult)"
    }

    # ------------------------------------------------------------ 4. mod files

    Write-Host ''
    Say 'The mod' 'Cyan'

    foreach ($name in $script:ModFiles) {
        $from = Join-Path $ModSource $name
        $to = Combine @($game, 'Mods', $name)
        $existed = Test-Path -LiteralPath $to

        Copy-Item -LiteralPath $from -Destination $to -Force

        $hash = Get-Sha256 -Path $to

        if ($hash -ne (Get-Sha256 -Path $from)) {
            Fail "$name did not copy correctly - the file in Mods does not match the package."
        }

        $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($to).FileVersion

        $entries.Add([ordered] @{ path = "Mods\$name"; kind = 'file'
                                  sha256 = $hash; replaced = $existed })

        $verb = 'installed'
        if ($existed) { $verb = 'updated  ' }
        Say ("  $verb  $name  v$version")
    }

    # --------------------------------------------------------------- 5. OpenXR
    #
    # PowerWash Simulator 2 is not a VR game, so Unity's OpenXR plugin is simply
    # not in the build. These three files add it.
    #
    # Downloaded rather than shipped: they are Unity's files under Unity's
    # package terms, which is not something this project can redistribute. The
    # download comes from Unity's own public registry, the same place the editor
    # fetches it from.

    Write-Host ''
    Say 'OpenXR support' 'Cyan'

    $needed = New-Object System.Collections.Generic.List[object]

    foreach ($file in $script:OpenXrFiles) {
        $have = Get-Sha256 -Path (Combine @($game, $file.Target))
        if ($Force -or $have -ne $file.Sha256) { $needed.Add($file) }
    }

    if ($needed.Count -eq 0) {
        Say "  already present and verified  (Unity OpenXR $($script:OpenXrVersion))"

        foreach ($file in $script:OpenXrFiles) {
            $entries.Add([ordered] @{ path = $file.Target; kind = 'file'
                                      sha256 = $file.Sha256; replaced = $false
                                      source = 'unity-openxr' })
        }
    }
    else {
        Say "  fetching Unity OpenXR $($script:OpenXrVersion)  (about 22 MB)"

        $archive = Combine @($temp, 'openxr.tgz')

        try {
            [System.Net.ServicePointManager]::SecurityProtocol =
                [System.Net.SecurityProtocolType]::Tls12

            $progress = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $script:OpenXrUrl -OutFile $archive -UseBasicParsing
            $ProgressPreference = $progress
        }
        catch {
            Fail ("Could not download the Unity OpenXR package." + [Environment]::NewLine +
                  "  $($_.Exception.Message)" + [Environment]::NewLine + [Environment]::NewLine +
                  "Everything else is installed; running this again only redoes this step.")
        }

        # The archive itself is deliberately NOT hash-pinned - a package registry
        # may repack it - so the three files that come OUT of it are, which is
        # what actually matters.
        #
        # Windows' own tar, BY ABSOLUTE PATH, not whatever "tar" happens to be on
        # PATH. Windows ships bsdtar, which understands C:\... perfectly well;
        # GNU tar does not - it reads the drive letter as a REMOTE HOST and dies
        # with "Cannot connect to C: resolve failed". Anyone with MSYS2, Cygwin or
        # a Git install on PATH can have GNU tar in front, and this failed
        # exactly that way the first time it was run from such a shell.
        $tar = Combine @($env:SystemRoot, 'System32', 'tar.exe')

        if (-not (Test-Path -LiteralPath $tar)) {
            Fail ("Windows' tar.exe is missing from System32, so the OpenXR package " +
                  "cannot be unpacked. It ships with Windows 10 1803 and later.")
        }

        # Members named EXACTLY: this package holds a second, wrong
        # openxr_loader.dll - see Common.ps1.
        $members = @($script:OpenXrFiles | ForEach-Object { $_.Source })
        $null = & $tar -xzf $archive -C $temp @members 2>&1

        if ($LASTEXITCODE -ne 0) {
            Fail ("Could not unpack the OpenXR download; the package layout may have changed." +
                  [Environment]::NewLine + "  tar exit code $LASTEXITCODE")
        }

        foreach ($file in $script:OpenXrFiles) {
            $extracted = Combine @($temp, ($file.Source -replace '/', '\'))

            if (-not (Test-Path -LiteralPath $extracted)) {
                Fail "Expected file missing from the download: $($file.Source)"
            }

            # Verified BEFORE it goes into the game. One of the two files called
            # openxr_loader.dll in that package is a test stub, and installing it
            # produces symptoms that look like broken headset drivers rather than
            # a wrong file.
            $hash = Get-Sha256 -Path $extracted

            if ($hash -ne $file.Sha256) {
                Fail ("$($file.Source) is not the file this mod was tested with." +
                      [Environment]::NewLine + "  expected  $($file.Sha256)" +
                      [Environment]::NewLine + "  found     $hash" +
                      [Environment]::NewLine + [Environment]::NewLine +
                      "Nothing was installed from the download. Please report this.")
            }

            $target = Combine @($game, $file.Target)
            $folder = Split-Path -Parent $target
            $existed = Test-Path -LiteralPath $target

            if (-not (Test-Path -LiteralPath $folder)) {
                New-Item -ItemType Directory -Path $folder -Force | Out-Null
            }

            Copy-Item -LiteralPath $extracted -Destination $target -Force

            $entries.Add([ordered] @{ path = $file.Target; kind = 'file'
                                      sha256 = $file.Sha256; replaced = $existed
                                      source = 'unity-openxr' })

            Say ('  installed  ' + (Split-Path -Leaf $file.Target))
        }
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- 6. manifest

$poseVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Combine @($game, 'Mods', 'WetReality.Pose.dll'))).FileVersion

$manifest = Write-Manifest -GamePath $game -Entries $entries.ToArray() -Version $poseVersion

# ------------------------------------------------------------------ 7. report

$final = Test-DotnetSix -GamePath $game

Write-Host ''

if ($final.Ok) {
    Say '  Done. Nothing else to install.' 'Green'
}
else {
    Say '  Done, but the .NET 6 runtime is still missing - the game will not start.' 'Yellow'
    Say "  $($final.Detail)" 'DarkGray'
}

Write-Host ''
Say '  Start the game through Steam. Put the headset on: VR comes up on its own a'
Say '  few seconds later, with the washer already in your hand.'
Write-Host ''
Say '  Have your headset software running BEFORE you start the game.' 'DarkGray'
Write-Host ''
Say '  The FIRST launch takes about half a minute with no sign of progress and' 'Yellow'
Say '  needs an internet connection: MelonLoader prepares its support files once.' 'Yellow'
Say '  It has not crashed.' 'Yellow'
Write-Host ''
Say '  Controls and settings:  Configurator.cmd  in this folder'
Say '  To remove everything:   Uninstall.cmd'
Write-Host ''
Say "  Record of what was installed: $manifest" 'DarkGray'
Write-Host ''
