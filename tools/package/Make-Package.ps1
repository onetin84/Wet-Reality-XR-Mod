# Wet Reality XR Mod - package builder (development tool, not shipped)
#
# Assembles the release folder from the build output and zips it. Run from the
# repository root or from anywhere; paths are derived from this script's own
# location.
#
#   .\tools\package\Make-Package.ps1
#   .\tools\package\Make-Package.ps1 -OutputRoot D:\releases
#
# The version is taken from the built WetReality.Pose.dll, so the folder name,
# the README and the guide footer cannot disagree with the DLL a tester actually
# receives.

[CmdletBinding()]
param(
    [string] $OutputRoot,
    [switch] $SkipZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $here)

if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'dist' }

# ------------------------------------------------------------- text file IO
#
# Explicit UTF-8, because Get-Content without -Encoding reads a BOM-less file in
# the system ANSI codepage. Writing that back as UTF-8 double-encodes every
# non-ASCII character - which is exactly what mangled the guides' em dash and
# every German umlaut in the first packaged build.
#
# Encoding.UTF8 on the way IN copes with a BOM or no BOM. On the way OUT the BOM
# is written, because PowerShell 5.1 and Notepad both read a BOM-less file as
# ANSI and a shipped .txt has to survive being opened by hand.

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

function Step { param([string] $Text) Write-Host "  $Text" -ForegroundColor Cyan }
function Note { param([string] $Text) Write-Host "    $Text" -ForegroundColor DarkGray }

Write-Host ''
Write-Host '  Wet Reality XR Mod - building package' -ForegroundColor Cyan
Write-Host ''

# ------------------------------------------------------------- 1. build output

$builds = @(
    @{ Name = 'WetReality.Pose.dll'
       Path = Join-Path $repo 'src\WetReality.Pose\bin\Release\net6.0\WetReality.Pose.dll' },
    @{ Name = 'WetReality.XRBoot.dll'
       Path = Join-Path $repo 'src\WetReality.XRBoot\bin\Release\net6.0\WetReality.XRBoot.dll' }
)

foreach ($build in $builds) {
    if (-not (Test-Path -LiteralPath $build.Path)) {
        throw "Not built: $($build.Path)`nBuild both projects in Release first."
    }
}

$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($builds[0].Path).FileVersion
$version = $version -replace '^(\d+\.\d+\.\d+)\.0$', '$1'

# A mod DLL is useless to a tester if it does not match the MelonInfo attribute
# the loader reports, and the two live in different files. Checked here rather
# than trusted, because the csproj version is easy to forget when bumping
# MelonInfo.
$source = Get-Content -LiteralPath (Join-Path $repo 'src\WetReality.Pose\Pose.cs') -Raw

if ($source -notmatch 'MelonInfo\(typeof\([^)]+\),\s*"[^"]+",\s*"([0-9.]+)"') {
    throw 'Could not read the MelonInfo version out of Pose.cs.'
}

if ($matches[1] -ne $version) {
    throw ("Version mismatch: Pose.cs MelonInfo says $($matches[1]), the built DLL says " +
           "$version. Update <Version> in WetReality.Pose.csproj and rebuild.")
}

Step "version $version"

# --------------------------------------------------------------- 2. lay it out

$name = "WetReality-XRMod-$version-beta"
$staging = Join-Path $OutputRoot $name

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

New-Item -ItemType Directory -Path (Join-Path $staging 'mod') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging 'scripts') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging 'configurator') -Force | Out-Null

Step 'mod'
foreach ($build in $builds) {
    Copy-Item -LiteralPath $build.Path -Destination (Join-Path $staging "mod\$($build.Name)") -Force
    $v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($build.Path).FileVersion
    Note "$($build.Name)  v$v"
}

Step 'scripts'
foreach ($file in @('Common.ps1', 'Install.ps1', 'Uninstall.ps1')) {
    Copy-Item -LiteralPath (Join-Path $here "scripts\$file") `
              -Destination (Join-Path $staging "scripts\$file") -Force
    Note $file
}

Step 'launchers'
foreach ($file in @('Install.cmd', 'Uninstall.cmd', 'Configurator.cmd')) {
    Copy-Item -LiteralPath (Join-Path $here $file) -Destination (Join-Path $staging $file) -Force
    Note $file
}

function Write-Utf8Lines {
    param([Parameter(Mandatory)] [string] $Path,
          # AllowEmptyString, nicht nur AllowEmptyCollection: die Leerzeilen
          # zwischen den Bloecken sind leere Elemente, und [string[]] weist die
          # sonst ab - der Fehler lautet dann "leere Zeichenfolge" und nicht
          # "leeres Array", was beim Suchen in die falsche Richtung zeigt.
          [Parameter(Mandatory)] [AllowEmptyString()] [AllowEmptyCollection()]
          [string[]] $Lines)

    # UTF-8 WITH a BOM, deliberately: Assets.ps1 is dot-sourced by a Windows
    # PowerShell 5.1 script, and 5.1 reads a BOM-less file as ANSI. Everything in
    # here is ASCII today, so it would survive - but the next line of German in a
    # comment would not, and that failure is silent.
    $utf8 = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($Path, ($Lines -join "`r`n") + "`r`n", $utf8)
}

# --------------------------------------------------------- 3. the configurator

$frontend = Join-Path $repo 'tools\frontend'

Step 'configurator'
foreach ($file in @('WetReality-Config.ps1', 'MainWindow.xaml', 'Strings.ps1',
                    'QuickGuide.html', 'QuickGuide-de.html')) {
    Copy-Item -LiteralPath (Join-Path $frontend $file) `
              -Destination (Join-Path $staging "configurator\$file") -Force
    Note $file
}

# EMBEDDED, NOT COPIED. The images go into a generated Assets.ps1 as base64 and
# the package carries no assets folder at all.
#
# Why: the folder sat in the download as plain PNGs, so anything in the window
# could be swapped for anything else with no effort whatsoever.
#
# WHAT THIS IS AND IS NOT. It is obfuscation. Base64 is recognisable and anyone
# who wants the images back will have them in a minute; the point is that it is
# no longer the default outcome of opening a folder. It is not protection, and
# nothing here should be read as if it were.
#
# The dev tree is untouched - tools\frontend\assets stays the source of truth,
# and WetReality-Config.ps1 falls back to it when Assets.ps1 is absent. Working
# files that never belonged in a download are simply not listed: logo_high.png
# at 2.2 MB, the spare backgrounds, the README about the image prompts.
$embed = [ordered] @{
    'background' = @('background.jpg', 'background.png')
    'logo'       = @('logo.png')
    'avatar'     = @('avatar.png')
}

$required = @('logo', 'avatar')
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add('# GENERATED BY Make-Package.ps1 - do not edit.')
$lines.Add('#')
$lines.Add('# The configurator images, base64 encoded, so the package carries no')
$lines.Add('# assets folder. Obfuscation, not protection - see the packager for why.')
$lines.Add('#')
$lines.Add('# Consumed by Set-OptionalImage in WetReality-Config.ps1, which falls back')
$lines.Add('# to an assets folder when this file is not there.')
$lines.Add('$script:EmbeddedAssets = @{}')
$lines.Add('')

foreach ($name in $embed.Keys) {
    $found = $null

    foreach ($candidate in $embed[$name]) {
        $path = Join-Path $frontend "assets\$candidate"
        if (Test-Path -LiteralPath $path) { $found = $path; break }
    }

    if (-not $found) {
        if ($required -contains $name) { throw "Required asset missing: $name" }

        Write-Host "    no $name image found - the window falls back to its placeholder" `
                   -ForegroundColor Yellow
        continue
    }

    # Read-AllBytes, not Get-Content: Get-Content decodes text, and section
    # "PowerShell 5.1 reads ANSI without a BOM" is about exactly that mistake.
    $bytes = [System.IO.File]::ReadAllBytes($found)
    $b64 = [System.Convert]::ToBase64String($bytes)

    # Chunked, because one 2 MB line makes the file unopenable in most editors
    # and some tools truncate it.
    $lines.Add("`$script:EmbeddedAssets['$name'] = @(")

    for ($i = 0; $i -lt $b64.Length; $i += 120) {
        $take = [Math]::Min(120, $b64.Length - $i)
        $lines.Add("    '" + $b64.Substring($i, $take) + "'")
    }

    # Doppelte Anfuehrungszeichen, weil der Text selbst zwei einfache
    # enthaelt. Mit einfachen war die erzeugte Zeile ") -join '" - ein
    # offener String, der die naechste Zuweisung verschluckt hat.
    $lines.Add(") -join ''")
    $lines.Add('')

    Note ("embedded $name  (" + [math]::Round($bytes.Length / 1KB) + " KB)")
}

Write-Utf8Lines -Path (Join-Path $staging 'configurator\Assets.ps1') -Lines $lines.ToArray()
Note 'Assets.ps1'

# ------------------------------------------------------------ 4. stamp version
#
# The README and the guide are static text, so unlike the configurator window
# they cannot read the version out of the DLL. They get it stamped in here, at
# the one moment when the built DLL is in hand.

Step 'stamping version'

foreach ($name in @('README.txt', 'LIESMICH.txt')) {
    $source = Join-Path $here $name

    if (-not (Test-Path -LiteralPath $source)) { throw "Missing: $source" }

    $text = Read-Utf8 -Path $source

    if ($text -notmatch '@@VERSION@@') { throw "$name has no @@VERSION@@ placeholder." }

    Write-Utf8 -Path (Join-Path $staging $name) `
               -Content ($text -replace '@@VERSION@@', "v$version")
    Note $name
}

$pattern = '(<span id="version"[^>]*>)v[0-9.]+(</span>)'

foreach ($name in @('QuickGuide.html', 'QuickGuide-de.html')) {
    $guide = Join-Path $staging "configurator\$name"
    $html = Read-Utf8 -Path $guide

    # THE IMAGES GO INTO THE HTML ITSELF.
    #
    # The guide is opened in a browser, so it cannot use the base64 in
    # Assets.ps1 - that one is for the configurator window. Without this the
    # package would either need the assets folder back, which is what this build
    # set out to remove, or ship three broken images. Inlining keeps the guide
    # self-contained and the folder gone.
    #
    # Same honesty as the other half: a data: URI is not protection, it just
    # stops the pictures being loose files in a download.
    foreach ($img in @('logo.png', 'controller.png', 'avatar.png')) {
        $from = Join-Path $frontend "assets\$img"

        if (-not (Test-Path -LiteralPath $from)) {
            Write-Host "    $name would keep a broken link: assets\$img is missing" `
                       -ForegroundColor Yellow
            continue
        }

        # Base64 has no '$' in its alphabet, so it is safe as a -replace
        # substitution, where '$' would otherwise be a group reference.
        $data = 'data:image/png;base64,' `
                + [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($from))

        $html = $html -replace [regex]::Escape("assets/$img"), $data
    }

    if ($html -match 'src="assets/') {
        throw "$name still points at the assets folder, which the package does not ship."
    }

    if ($html -notmatch $pattern) { throw "$name has no version span to stamp." }

    Write-Utf8 -Path $guide -Content ($html -replace $pattern, "`${1}v$version`${2}")
    Note $name
}

# ---------------------------------------------------------------- 5. self-check
#
# Catches the two mistakes that are invisible until a tester hits them: a dev-only
# mod left in the payload, and a hardcoded path from this machine.

Step 'checking'

$strays = @(Get-ChildItem -LiteralPath (Join-Path $staging 'mod') -File |
            Where-Object { $_.Name -notin @('WetReality.Pose.dll', 'WetReality.XRBoot.dll') })

if ($strays.Count -gt 0) {
    throw "Unexpected files in mod\: $($strays.Name -join ', ')"
}

$leaks = @()

$textFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -File |
               Where-Object { $_.Extension -in @('.ps1', '.cmd', '.html', '.txt', '.xaml') })

foreach ($file in $textFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw

    # Drive letters of this machine's library and repo. A tester's paths differ,
    # and an absolute path that happens to resolve on their machine is worse than
    # one that fails.
    if ($content -match '[A-Z]:\\(SteamLibrary|Privat)') {
        $leaks += $file.FullName.Substring($staging.Length + 1)
    }
}

# The assemblies get the same test. A release build embeds the path of its own
# .pdb in the debug directory, which spelled out the whole build tree until
# DebugType/PathMap were set in the csproj - the kind of thing that only shows up
# if something looks.
foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $staging 'mod') -File)) {
    $content = Get-Content -LiteralPath $file.FullName -Raw

    if ($content -match '[A-Za-z]:\\[^\x00]{0,60}(Privat|SteamLibrary|Users)') {
        $leaks += "$($file.Name)  ($($matches[0]))"
    }
}

if ($leaks.Count -gt 0) {
    throw "Hardcoded local paths found in: $($leaks -join ', ')"
}

# Double encoding leaves a telltale: a UTF-8 "Ã" or "â€" sequence where one
# character should be. Checked because the first packaged build shipped exactly
# that and it was only noticed by a human looking at a browser tab.
foreach ($file in $textFiles) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    if ($text -match '[\u00c3\u00e2]\u20ac' -or $text -match '\u00c3\u0083') {
        throw ("Double-encoded text in $($file.Name) - a UTF-8 file was read as ANSI " +
               "somewhere in this build.")
    }
}

Note "no dev files, no local paths, no double encoding ($($textFiles.Count) text files scanned)"

# ---------------------------------------------------------------------- 6. zip

$zip = "$staging.zip"

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
    Step 'zipped'
}

# ------------------------------------------------------------------- 7. report

$files = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host "  $name" -ForegroundColor Green
Write-Host "  $($files.Count) files, $([math]::Round($bytes / 1KB)) KB" -ForegroundColor Green
Write-Host "  $staging" -ForegroundColor DarkGray

if (-not $SkipZip) {
    $zipSize = (Get-Item -LiteralPath $zip).Length
    Write-Host "  $zip  ($([math]::Round($zipSize / 1KB)) KB)" -ForegroundColor DarkGray
}

Write-Host ''
