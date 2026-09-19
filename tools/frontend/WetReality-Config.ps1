# Wet Reality XR Mod - configurator
#
# Writes MelonPreferences.cfg, opens the quick guide, and starts the game
# THROUGH STEAM - steam://rungameid/<appid>, never the executable.
#
# The note here used to say a launch was left out deliberately, because "a launch
# path of its own could collide with Steam and its overlay". That objection was
# about starting the .exe directly and it still stands. The URL protocol is the
# opposite: it hands the request to Steam, which starts the game exactly as a
# click in the library does, overlay and all. Nothing is compiled either way, so
# there is still neither a signing problem nor a SmartScreen warning.
#
# Nothing here is compiled. PowerShell 5.1 ships WPF through
# PresentationFramework, the window comes from MainWindow.xaml, and every graphic
# is a file under assets/. A missing file leaves a placeholder in its place and
# changes nothing about the layout.
#
# NOTHING IS WRITTEN without a click on Save, and a backup of the cfg is made
# every time.

[CmdletBinding()]
param(
    [string] $GamePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Localisation. Loaded before the window so the first paint is already in the
# right language - switching after showing would flash English.
$script:Language = 'en'
$script:Unmapped = @{}

. (Join-Path $script:Root 'Strings.ps1')
# NACHGEMESSEN, nicht uebernommen: appmanifest_2968420.acf in der Bibliothek
# neben dem Spielordner nennt "appid" "2968420" und "name" "PowerWash
# Simulator 2". Eine AppID aendert sich fuer ein Spiel nicht, also eine
# Konstante und keine Suche - der Knopf haengt ohnehin an Find-GameFolder, das
# die Installation wirklich nachweist.
$script:AppId = '2968420'

# Ungespeichertes, als echter Merker und nicht als Hinweistext.
#
# Mark-Dirty schrieb bisher nur in SaveHint. Ein Start mit ungespeicherten
# Aenderungen wuerde die alte cfg laden und still das Gegenteil dessen tun, was
# der Spieler gerade eingestellt hat - deshalb ein Flag, das der Startknopf
# lesen kann. Deklariert statt bei Bedarf gesetzt: unter
# Set-StrictMode -Version Latest ist das LESEN einer nicht gesetzten Variablen
# ein Fehler.
$script:Dirty = $false
$script:CfgPath = $null
$script:GameFolder = $null
$script:Loading = $true

# ------------------------------------------------------------- text file IO
#
# Explicit UTF-8, because Get-Content without -Encoding reads a file that has no
# BOM in the system ANSI codepage. Writing that back as UTF-8 double-encodes
# every non-ASCII character. Encoding.UTF8 on the way in copes with a BOM or
# none; on the way out the BOM is written, because PowerShell 5.1 and Notepad
# both read a BOM-less file as ANSI.

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

# ---------------------------------------------------------------- Installation

function Get-SteamLibraries {
    # Registry first, then the library folders file. Both are read-only lookups.
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
            # Absent key is the normal case on one of the two hives.
        }
    }

    $extra = New-Object System.Collections.Generic.List[string]

    foreach ($base in $libraries) {
        # [System.IO.Path]::Combine, NOT Join-Path - and this is not a style
        # preference.
        #
        # Join-Path resolves against the PowerShell provider and THROWS on a
        # drive that does not exist. libraryfolders.vdf keeps stale entries for
        # libraries that have been removed: this machine still lists drives S:
        # and T:. With $ErrorActionPreference = 'Stop' at the top of the script,
        # the first stale entry killed the whole configurator before the window
        # opened - found by running the discovery on its own rather than by
        # opening the window and seeing nothing.
        #
        # Combine is pure string work. Test-Path on a missing drive then simply
        # answers false, which is the behaviour wanted here.
        $vdf = [System.IO.Path]::Combine($base, 'steamapps', 'libraryfolders.vdf')

        if (-not (Test-Path -LiteralPath $vdf)) { continue }

        # Only the "path" entries are of interest; a full VDF parser would be
        # out of proportion for four lines of regex.
        foreach ($line in (Get-Content -LiteralPath $vdf)) {
            if ($line -match '"path"\s+"(.+?)"') {
                $extra.Add(($matches[1] -replace '\\\\', '\'))
            }
        }
    }

    foreach ($path in $extra) { $libraries.Add($path) }

    return ($libraries | Select-Object -Unique)
}

function Find-GameFolder {
    foreach ($library in Get-SteamLibraries) {
        if (-not $library) { continue }

        $candidate = [System.IO.Path]::Combine(
            $library, 'steamapps', 'common', 'PowerWash Simulator 2')

        if (Test-Path -LiteralPath ([System.IO.Path]::Combine($candidate, 'MelonLoader'))) {
            return $candidate
        }
    }

    return $null
}

function Test-GameFolder {
    param([string] $Path)

    if (-not $Path) { return $false }
    return (Test-Path -LiteralPath ([System.IO.Path]::Combine($Path, 'MelonLoader')))
}

# ------------------------------------------------------------------------- cfg
#
# Targeted per-key replacement inside the file, NOT a parse-and-rewrite.
#
# MelonPreferences.cfg carries a description comment above every entry and is
# owned by MelonLoader, which rewrites it on its own terms. Reformatting it would
# throw those comments away and risk the mod's own defaults on the next launch.
# So each key is replaced in place and everything else is left byte for byte.

function Read-CfgValue {
    param([string] $Key, [string] $Fallback)

    if (-not $script:CfgPath -or -not (Test-Path -LiteralPath $script:CfgPath)) { return $Fallback }

    foreach ($line in ((Read-Utf8 -Path $script:CfgPath) -split "`r?`n")) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.+?)\s*$") {
            return $matches[1].Trim().Trim('"')
        }
    }

    return $Fallback
}

function Write-CfgValues {
    param([hashtable] $Values)

    if (-not (Test-Path -LiteralPath $script:CfgPath)) {
        throw (T 'MelonPreferences.cfg not found. Start the game once with the mod installed so that it gets created.')
    }

    # One backup per save, timestamped. Cheap, and it is the only way back if a
    # replacement ever goes wrong.
    $backup = "$($script:CfgPath).bak"
    Copy-Item -LiteralPath $script:CfgPath -Destination $backup -Force

    $lines = (Read-Utf8 -Path $script:CfgPath) -split "`r?`n"
    $written = @{}

    for ($index = 0; $index -lt $lines.Count; $index++) {
        foreach ($key in $Values.Keys) {
            if ($lines[$index] -match "^\s*$([regex]::Escape($key))\s*=") {
                $lines[$index] = "$key = $($Values[$key])"
                $written[$key] = $true
            }
        }
    }

    $missing = @($Values.Keys | Where-Object { -not $written.ContainsKey($_) })

    # Explicit UTF-8 in and out. A user whose Windows account carries an umlaut
    # has it in the UnityExplorer paths inside this file, and an ANSI round trip
    # would corrupt their config on every save.
    Write-Utf8 -Path $script:CfgPath -Content $lines

    return $missing
}

function Format-Bool {
    param([bool] $Value)
    if ($Value) { return 'true' } else { return 'false' }
}

function Format-Float {
    param([double] $Value)
    # Invariant decimal point: the cfg is read by a .NET mod, and a German comma
    # would parse as something else entirely.
    return $Value.ToString('0.####', [System.Globalization.CultureInfo]::InvariantCulture)
}

# ---------------------------------------------------------------------- window

$xamlPath = Join-Path $script:Root 'MainWindow.xaml'

if (-not (Test-Path -LiteralPath $xamlPath)) {
    [System.Windows.MessageBox]::Show((T 'MainWindow.xaml is missing next to this script.'), 'Wet Reality') | Out-Null
    return
}

$reader = New-Object System.Xml.XmlNodeReader ([xml](Get-Content -LiteralPath $xamlPath -Raw))
$window = [Windows.Markup.XamlReader]::Load($reader)

function Ctl { param([string] $Name) return $window.FindName($Name) }

# Applied to the tree BEFORE anything else reads a label, and before the window
# is shown.
Set-Language -Window $window -Value (Resolve-StartLanguage)

function Update-LanguageButton {
    # The button offers the OTHER language, so its caption is the destination.
    $button = Ctl 'LangButton'
    if (-not $button) { return }

    if ($script:Language -eq 'de') { $button.Content = 'English' }
    else { $button.Content = 'Deutsch' }
}

Update-LanguageButton

# --------------------------------------------------------------------- images
#
# Every graphic is optional. Present, it is used and the placeholder goes away;
# absent, the placeholder stays. That lets the layout be finished before the
# assets are, and each image be dropped in on its own.

# MUSS VORHANDEN SEIN, BEVOR ES GELESEN WIRD. Set-StrictMode -Version Latest
# steht oben in dieser Datei, und damit wirft der Zugriff auf eine nie
# zugewiesene Variable - im Dev-Baum gibt es keine Assets.ps1, die sie setzt,
# also waere genau dort jeder Bildaufruf gescheitert.
#
# Tried getrennt von der Hashtable, weil "nicht vorhanden" und "vorhanden und
# leer" zwei verschiedene Zustaende sind und nur der erste einen zweiten
# Ladeversuch verdient.
$script:EmbeddedAssets = $null
$script:EmbeddedTried = $false

function Set-OptionalImage {
    param(
        [string[]] $FileNames,
        [Parameter(Mandatory)] $Image,
        $Placeholder,
        # The name this image has inside Assets.ps1. Defaults to nothing, so a
        # caller that does not pass one simply uses the folder.
        [string] $Key = ''
    )

    # SEVERAL candidate names, first match wins - because the right format
    # differs per asset and the script should not dictate it.
    #
    # The logo and the avatar need a real alpha channel and must be PNG. The
    # background plate has no transparency and is photographic, so PNG was the
    # wrong choice: the finished plate came to 3.7 MB as PNG and 204 KB as JPEG
    # at quality 92, with no visible difference on a blurred gradient. Quality 85
    # would have been 130 KB but soft gradients are where JPEG starts to band.
    # EMBEDDED FIRST, FOLDER SECOND.
    #
    # The packaged build has no assets folder: Make-Package.ps1 writes the images
    # into Assets.ps1 as base64 so they are not sitting in the download as files
    # to be swapped. Obfuscation, not protection - base64 is recognisable, and
    # that is understood.
    #
    # The dev tree has no Assets.ps1, so it drops straight through to the folder
    # below and nothing about working on the images changes.
    if (-not $script:EmbeddedTried) {
        $script:EmbeddedTried = $true
        $embedded = Join-Path $script:Root 'Assets.ps1'
        if (Test-Path -LiteralPath $embedded) { . $embedded }
    }

    if ($script:EmbeddedAssets -and $script:EmbeddedAssets.ContainsKey($Key)) {
        try {
            $bytes = [System.Convert]::FromBase64String($script:EmbeddedAssets[$Key])
            $stream = New-Object System.IO.MemoryStream(, $bytes)

            $bitmap = New-Object System.Windows.Media.Imaging.BitmapImage
            $bitmap.BeginInit()
            $bitmap.StreamSource = $stream
            # OnLoad so the decode happens here and the stream can go.
            $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
            $bitmap.EndInit()

            $Image.Source = $bitmap
            $Image.Visibility = 'Visible'

            if ($Placeholder) { $Placeholder.Visibility = 'Collapsed' }

            return $true
        }
        catch {
            # Falls through to the folder rather than failing: a window with a
            # gradient instead of a plate is usable, a crashed configurator is not.
        }
    }

    $folder = Join-Path $script:Root 'assets'
    $path = $null

    foreach ($name in $FileNames) {
        $candidate = Join-Path $folder $name

        if (Test-Path -LiteralPath $candidate) {
            $path = $candidate
            break
        }
    }

    if (-not $path) { return $false }

    try {
        $bitmap = New-Object System.Windows.Media.Imaging.BitmapImage
        $bitmap.BeginInit()
        $bitmap.UriSource = New-Object System.Uri((Resolve-Path -LiteralPath $path).Path)
        # Loaded into memory so the file is not locked while the window is open -
        # otherwise a newly exported asset could not overwrite the old one.
        $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bitmap.EndInit()

        $Image.Source = $bitmap
        $Image.Visibility = 'Visible'

        if ($Placeholder) { $Placeholder.Visibility = 'Collapsed' }

        return $true
    }
    catch {
        return $false
    }
}

Set-OptionalImage -FileNames @('background.jpg', 'background.png') -Key 'background' `
    -Image (Ctl 'BackgroundImage') | Out-Null

Set-OptionalImage -FileNames @('logo.png') -Key 'logo' `
    -Image (Ctl 'LogoImage') -Placeholder (Ctl 'LogoPlaceholder') | Out-Null

Set-OptionalImage -FileNames @('avatar.png') -Key 'avatar' `
    -Image (Ctl 'AvatarImage') -Placeholder (Ctl 'AvatarPlaceholder') | Out-Null

# ---------------------------------------------------------------------- state

function Set-Status {
    param([string] $Path)

    $ok = Test-GameFolder -Path $Path

    # Kept, because the language toggle needs to re-issue the status line and the
    # label it would otherwise read back holds an INSTRUCTION when no
    # installation was found, not a path.
    $script:GameFolder = $Path

    if ($ok) {
        $script:CfgPath = [System.IO.Path]::Combine($Path, 'UserData', 'MelonPreferences.cfg')
        (Ctl 'StatusDot').Fill = (Ctl 'StatusDot').FindResource('Cyan')
        (Ctl 'StatusText').Text = T 'Installation found'
        (Ctl 'StatusPath').Text = $Path
    }
    else {
        $script:CfgPath = $null
        (Ctl 'StatusDot').Fill = (Ctl 'StatusDot').FindResource('Coral')
        (Ctl 'StatusText').Text = T 'No installation found'
        (Ctl 'StatusPath').Text = T 'Choose the "PowerWash Simulator 2" folder manually.'
    }

    (Ctl 'SaveButton').IsEnabled = $ok

    # Derselbe Nachweis fuer den Start: $ok bedeutet, dass unter dem Pfad ein
    # MelonLoader-Ordner liegt. Ohne Installation bleibt der Knopf grau statt
    # eine Fehlermeldung zu produzieren.
    (Ctl 'LaunchButton').IsEnabled = $ok
    ShowVersions -GamePath $Path
    return $ok
}

# Reads the version out of the INSTALLED DLLs rather than out of this script.
#
# FileVersionInfo reads the PE metadata without loading the assembly, which
# matters here: loading WetReality.Pose.dll outside the game would pull in the
# Il2Cpp interop types and fail.
#
# And reading the installed file rather than a constant means the number shown is
# always the number that will actually run. A hardcoded string would drift the
# moment somebody copies an older DLL in - which is exactly the situation where a
# version display has to be trustworthy.
function ShowVersions {
    param([string] $GamePath)

    $label = Ctl 'VersionText'

    if (-not $label) { return }

    try {
        if (-not (Test-GameFolder -Path $GamePath)) {
            $label.Text = ''
            return
        }

        $parts = @()

        foreach ($mod in @(@('Mod', 'WetReality.Pose.dll'), @('Boot', 'WetReality.XRBoot.dll'))) {
            $dll = [System.IO.Path]::Combine($GamePath, 'Mods', $mod[1])

            if (Test-Path -LiteralPath $dll) {
                $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
                $v = $info.FileVersion

                # Only a FOURTH component gets dropped. A bare '\.0$' also ate the
                # patch digit and turned 0.95.0 into 0.95 - caught by printing the
                # result rather than by reading the pattern.
                if ($v) { $parts += ("{0} {1}" -f $mod[0], ($v -replace '^(\d+\.\d+\.\d+)\.0$', '$1')) }
            }
            else {
                $parts += ("{0} {1}" -f $mod[0], (T 'missing'))
            }
        }

        if ($parts.Count -gt 0) { $label.Text = ($parts -join '   ') }
        else { $label.Text = '' }
    }
    catch {
        $label.Text = ''
    }
}

function Load-Settings {
    $script:Loading = $true

    $hand = Read-CfgValue -Key 'Hand' -Fallback 'RightHand'
    (Ctl 'HandBox').SelectedIndex = if ($hand -eq 'LeftHand') { 1 } else { 0 }

    (Ctl 'TurnSpeedSlider').Value = [double](Read-CfgValue -Key 'TurnSpeed' -Fallback '90')
    (Ctl 'HapticIntensitySlider').Value = [double](Read-CfgValue -Key 'HapticIntensity' -Fallback '1')
    (Ctl 'UiScaleSlider').Value = [double](Read-CfgValue -Key 'UiScale' -Fallback '0.5')
    (Ctl 'UiDistanceSlider').Value = [double](Read-CfgValue -Key 'UiDistance' -Fallback '2')
    (Ctl 'GripXSlider').Value = [double](Read-CfgValue -Key 'GripOffsetX' -Fallback '0')
    (Ctl 'GripYSlider').Value = [double](Read-CfgValue -Key 'GripOffsetY' -Fallback '0')
    (Ctl 'GripZSlider').Value = [double](Read-CfgValue -Key 'GripOffsetZ' -Fallback '0')
    (Ctl 'RotPitchSlider').Value = [double](Read-CfgValue -Key 'RotationOffsetPitch' -Fallback '0')
    (Ctl 'RotYawSlider').Value = [double](Read-CfgValue -Key 'RotationOffsetYaw' -Fallback '0')
    (Ctl 'RotRollSlider').Value = [double](Read-CfgValue -Key 'RotationOffsetRoll' -Fallback '0')

    (Ctl 'SnapTurnCheck').IsChecked = (Read-CfgValue -Key 'SnapTurn' -Fallback 'false') -eq 'true'
    (Ctl 'SprayHapticsCheck').IsChecked = (Read-CfgValue -Key 'SprayHaptics' -Fallback 'true') -eq 'true'
    (Ctl 'VrHandsCheck').IsChecked = (Read-CfgValue -Key 'ShowVrHands' -Fallback 'true') -eq 'true'
    (Ctl 'LaserCheck').IsChecked = (Read-CfgValue -Key 'ShowWashLaser' -Fallback 'false') -eq 'true'

    $script:Loading = $false
    Update-Labels
    (Ctl 'SaveHint').Text = ''
}

function Update-Labels {
    (Ctl 'TurnSpeedValue').Text = "$([int](Ctl 'TurnSpeedSlider').Value) $(T 'deg/s')"
    # Als Prozent, weil 1.25 als Zahl nichts sagt und "125 %" alles.
    (Ctl 'HapticIntensityValue').Text = "$([int]((Ctl 'HapticIntensitySlider').Value * 100)) %"
    (Ctl 'UiScaleValue').Text = "$([int]((Ctl 'UiScaleSlider').Value * 100)) %"
    (Ctl 'UiDistanceValue').Text = "$((Format-Float (Ctl 'UiDistanceSlider').Value)) m"
    (Ctl 'GripXValue').Text = "$([int]((Ctl 'GripXSlider').Value * 100)) cm"
    (Ctl 'GripYValue').Text = "$([int]((Ctl 'GripYSlider').Value * 100)) cm"
    (Ctl 'GripZValue').Text = "$([int]((Ctl 'GripZSlider').Value * 100)) cm"
    $deg = [char]0x00B0
    (Ctl 'RotPitchValue').Text = "$([int](Ctl 'RotPitchSlider').Value)$deg"
    (Ctl 'RotYawValue').Text = "$([int](Ctl 'RotYawSlider').Value)$deg"
    (Ctl 'RotRollValue').Text = "$([int](Ctl 'RotRollSlider').Value)$deg"
}

function Mark-Dirty {
    if ($script:Loading) { return }
    $script:Dirty = $true
    (Ctl 'SaveHint').Text = T 'Unsaved changes'
}

# --------------------------------------------------------------------- events

foreach ($name in @('TurnSpeedSlider', 'HapticIntensitySlider',
                    'UiScaleSlider', 'UiDistanceSlider',
                    'GripXSlider', 'GripYSlider', 'GripZSlider',
                    'RotPitchSlider', 'RotYawSlider', 'RotRollSlider')) {
    (Ctl $name).Add_ValueChanged({ Update-Labels; Mark-Dirty })
}

foreach ($name in @('SnapTurnCheck', 'VrHandsCheck', 'LaserCheck',
                    'SprayHapticsCheck')) {
    (Ctl $name).Add_Click({ Mark-Dirty })
}

(Ctl 'HandBox').Add_SelectionChanged({ Mark-Dirty })

(Ctl 'GripResetButton').Add_Click({
    (Ctl 'GripXSlider').Value = 0
    (Ctl 'GripYSlider').Value = 0
    (Ctl 'GripZSlider').Value = 0
    (Ctl 'RotPitchSlider').Value = 0
    (Ctl 'RotYawSlider').Value = 0
    (Ctl 'RotRollSlider').Value = 0
})

(Ctl 'BrowseButton').Add_Click({
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = T 'Select the "PowerWash Simulator 2" folder'

    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        if (Set-Status -Path $dialog.SelectedPath) { Load-Settings }
    }
})

(Ctl 'LangButton').Add_Click({
    $next = 'de'
    if ($script:Language -eq 'de') { $next = 'en' }

    Set-Language -Window $window -Value $next
    Save-Language -Value $next
    Update-LanguageButton

    # The status line and the value labels are written by the script rather than
    # by the XAML, so the walk cannot reach them - they are re-issued here.
    Set-Status -Path $script:GameFolder | Out-Null
    if (-not $script:Loading) { Update-Labels }
})

(Ctl 'GuideButton').Add_Click({
    # Follows the window's language, falling back to the English file if the
    # localised one was left out of the package.
    $guide = Join-Path $script:Root $script:GuideFile[$script:Language]

    if (-not (Test-Path -LiteralPath $guide)) {
        $guide = Join-Path $script:Root 'QuickGuide.html'
    }

    if (Test-Path -LiteralPath $guide) {
        Start-Process -FilePath $guide
    }
    else {
        [System.Windows.MessageBox]::Show(
            "$(T 'The quick guide is not available yet.')`n`n$(T 'Expected at'): $guide",
            'Wet Reality') | Out-Null
    }
})

(Ctl 'LaunchButton').Add_Click({
    # ERST WARNEN, DANN STARTEN. Wer etwas verstellt und direkt startet, spielt
    # sonst mit der alten cfg und sucht den Fehler im Spiel. Der Merker kann nur
    # zu VORSICHTIG sein: er wird beim Speichern geraeumt und beim Laden nie
    # gesetzt, also gibt es allenfalls eine Rueckfrage zu viel, nie eine zu
    # wenig.
    if ($script:Dirty) {
        $answer = [System.Windows.MessageBox]::Show(
            (T 'There are unsaved changes. They will not apply to this session. Start anyway?'),
            'Wet Reality',
            [System.Windows.MessageBoxButton]::OKCancel,
            [System.Windows.MessageBoxImage]::Warning)

        if ($answer -ne [System.Windows.MessageBoxResult]::OK) { return }
    }

    try {
        # Das URL-Protokoll, nicht die .exe: Steam startet das Spiel dann wie
        # ein Klick in der Bibliothek, mit Overlay, und startet sich notfalls
        # selbst. Ein Direktstart der exe umgeht beides.
        Start-Process -FilePath "steam://rungameid/$($script:AppId)"
        (Ctl 'SaveHint').Text = T 'Starting through Steam...'
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Wet Reality') | Out-Null
    }
})

(Ctl 'SaveButton').Add_Click({
    try {
        $hand = 'RightHand'
        if ((Ctl 'HandBox').SelectedIndex -eq 1) { $hand = 'LeftHand' }

        $values = @{
            'Hand'          = "`"$hand`""
            'TurnSpeed'     = Format-Float (Ctl 'TurnSpeedSlider').Value
            'HapticIntensity' = Format-Float (Ctl 'HapticIntensitySlider').Value
            'SprayHaptics'  = Format-Bool ([bool](Ctl 'SprayHapticsCheck').IsChecked)
            'UiScale'       = Format-Float (Ctl 'UiScaleSlider').Value
            'UiDistance'    = Format-Float (Ctl 'UiDistanceSlider').Value
            'GripOffsetX'   = Format-Float (Ctl 'GripXSlider').Value
            'GripOffsetY'   = Format-Float (Ctl 'GripYSlider').Value
            'GripOffsetZ'   = Format-Float (Ctl 'GripZSlider').Value
            'RotationOffsetPitch' = Format-Float (Ctl 'RotPitchSlider').Value
            'RotationOffsetYaw'   = Format-Float (Ctl 'RotYawSlider').Value
            'RotationOffsetRoll'  = Format-Float (Ctl 'RotRollSlider').Value
            'SnapTurn'      = Format-Bool ([bool](Ctl 'SnapTurnCheck').IsChecked)
            'ShowVrHands'   = Format-Bool ([bool](Ctl 'VrHandsCheck').IsChecked)
            'ShowWashLaser' = Format-Bool ([bool](Ctl 'LaserCheck').IsChecked)
        }

        # @() around the call, and that is not cosmetic. A PowerShell function
        # returning an EMPTY array sends nothing down the pipeline, so $missing
        # arrived here as $null; with exactly ONE missing key it arrived as a
        # bare string. Under Set-StrictMode both make $missing.Count throw
        # PropertyNotFoundException, and the catch below put that in front of
        # the user as a MessageBox - on every save where the cfg already held
        # all ten keys, which is the normal case for a correct installation.
        # Only two or more missing keys ever returned a real array, which is
        # why this survived testing here: the dev cfg was missing several keys.
        $missing = @(Write-CfgValues -Values $values)
        $script:Dirty = $false

        if ($missing.Count -gt 0) {
            # Reported rather than appended. A key that is absent means the mod
            # has not created it yet, and inventing it here could write it into
            # the wrong section.
            (Ctl 'SaveHint').Text = (T 'Saved. Keys not found: {0}') -f ($missing -join ', ')
        }
        else {
            (Ctl 'SaveHint').Text = (T 'Saved at {0}. Backup written as .bak.') -f (Get-Date -Format 'HH:mm:ss')
        }
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Wet Reality') | Out-Null
    }
})

# ----------------------------------------------------------------------- start

$found = $GamePath
if (-not $found) { $found = Find-GameFolder }

if (Set-Status -Path $found) { Load-Settings } else { $script:Loading = $false }

# THE WINDOW MUST NOT BE TALLER THAN THE SCREEN IT OPENS ON.
#
# MainWindow.xaml asks for 965 px seit Abschnitt 109 - das Spiegelbild-Haekchen
# ist wieder heraus, weil es nicht behebt, wonach es aussieht. Die Preference
# DesktopMirror bleibt in der cfg: sie stoppt den Augenpuffer-Blit, sie
# beseitigt nur das verschachtelte Titelbild nicht.
#
# Vorher, Abschnitt 108: die Vibrations- und
# Spiegelbild-Steuerelemente sind dazugekommen, und der Knopf zum
# Zuruecksetzen des Griffs lag darunter - gemeldet als "man muss scrollen".
# Der Deckel aus WorkArea unten bleibt die Rueckfallebene fuer kleine Schirme;
# auf 1080p ist bei etwa 1040 px Arbeitsflaeche noch Luft.
#
# MainWindow.xaml asks for 900 px so that all six grip sliders fit without
# scrolling - the three rotation rows used to sit below the edge. On a 1080p
# laptop with a taskbar that is close to the whole screen, so the work area is
# the limit and the XAML's ScrollViewer takes over from there. Shrinking the
# window is the harmless direction; the settings stay reachable either way.
#
# Guarded rather than trusted: WorkArea comes back empty on some remote
# sessions, and a MaxHeight of nearly zero would leave a title bar with
# nothing under it.
$work = [System.Windows.SystemParameters]::WorkArea.Height

if ($work -gt 400 -and $window.Height -gt $work - 40) {
    $window.Height = $work - 40
}

# DIE KONSOLE WEG - Abschnitt 130, und die Stelle ist der Entwurf.
#
# Configurator.cmd startet die Sitzung schon mit -WindowStyle Hidden; ein
# Doppelklick auf diese .ps1 tut das nicht. ShowWindow deckt beide Wege ab.
#
# HIER und nicht oben: alles darueber kann fehlschlagen und schreibt dann in
# die Konsole - fehlende WPF-Assemblies, ein kaputtes XAML, eine unlesbare
# cfg. Eine in Zeile 1 versteckte Konsole macht daraus ein Werkzeug, das beim
# Start still nichts tut, und das ist schlimmer als ein sichtbares Fenster.
#
# GetConsoleWindow gibt 0 zurueck, wenn es gar keine Konsole gibt - dann ist
# nichts zu verstecken. Ein Fehlschlag bleibt folgenlos: das Fenster oeffnet
# sich trotzdem, die Konsole bleibt eben stehen. Das ist die harmlose Richtung.
try {
    if (-not ('WetReality.Native' -as [type])) {
        Add-Type -Namespace WetReality -Name Native -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@
    }

    $console = [WetReality.Native]::GetConsoleWindow()

    if ($console -ne [System.IntPtr]::Zero) {
        # 0 ist SW_HIDE.
        [WetReality.Native]::ShowWindow($console, 0) | Out-Null
    }
}
catch {
    # Absichtlich stumm: eine Meldung ueber ein nicht verstecktes Fenster in
    # ein Fenster zu schreiben, das gleich aufgeht, hilft niemandem.
}

$window.ShowDialog() | Out-Null
