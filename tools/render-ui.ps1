# Renders MainWindow.xaml to a PNG without showing it, so a visual change can be
# LOOKED AT rather than asserted. Development tool, not shipped.

param([ValidateSet('en', 'de')] [string] $Lang = 'en')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$root = Join-Path $PSScriptRoot 'frontend'
$out = Join-Path $PSScriptRoot "ui-preview-$Lang.png"

$reader = New-Object System.Xml.XmlNodeReader ([xml](Get-Content -LiteralPath (Join-Path $root 'MainWindow.xaml') -Raw))
$window = [Windows.Markup.XamlReader]::Load($reader)

# The same localiser the configurator uses, so the preview shows what ships.
#
# $script:Language is NOT pre-set here. A script parameter lives in the script
# scope, so "param($Language)" and "$script:Language" were one and the same
# variable, and initialising it to 'en' discarded the value that was passed in -
# which is why the German render came out entirely English. Set-Language assigns
# it before anything reads it, so the initialisation was never needed either.
$script:Unmapped = @{}
. (Join-Path $root 'Strings.ps1')
Set-Language -Window $window -Value $Lang

if ($Lang -eq 'de') { $window.FindName('LangButton').Content = 'English' }

function Load-Image {
    param([string] $Name, [string] $Target, [string] $Placeholder)

    $path = Join-Path (Join-Path $root 'assets') $Name
    if (-not (Test-Path -LiteralPath $path)) { return }

    $bitmap = New-Object System.Windows.Media.Imaging.BitmapImage
    $bitmap.BeginInit()
    $bitmap.UriSource = New-Object System.Uri((Resolve-Path -LiteralPath $path).Path)
    $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $bitmap.EndInit()

    $control = $window.FindName($Target)
    if (-not $control) { return }

    $control.Source = $bitmap
    $control.Visibility = 'Visible'

    if ($Placeholder) {
        $ph = $window.FindName($Placeholder)
        if ($ph) { $ph.Visibility = 'Collapsed' }
    }
}

Load-Image -Name 'background.png' -Target 'BackgroundImage'
Load-Image -Name 'logo.png' -Target 'LogoImage' -Placeholder 'LogoPlaceholder'
Load-Image -Name 'avatar.png' -Target 'AvatarImage' -Placeholder 'AvatarPlaceholder'

# Some text is filled in by the script at runtime; without it the preview would
# show empty gaps that do not exist in use.
foreach ($pair in @(@('StatusText', (T 'Installation found')),
                    # Ein BEISPIELPFAD, nur fuer das Vorschaubild - er zeigt,
                    # wie die Zeile im Fenster aussieht. Absichtlich der
                    # Standardort von Steam und nicht der dieses Rechners.
                    @('StatusPath', 'C:\Program Files (x86)\Steam\steamapps\common\PowerWash Simulator 2'),
                    @('VersionText', 'Mod 0.96.0   Boot 0.19.0'),
                    @('TurnSpeedValue', "90 $(T 'deg/s')"),
                    # Neu in Abschnitt 102: ohne diese Zeile zeigt die Vorschau ein
                    # leeres Feld und behauptet damit einen Layoutfehler, den es nicht
                    # gibt - das Messgeraet luegt sonst genau wie in Abschnitt 100.
                    @('HapticIntensityValue', '100 %'),
                    @('UiScaleValue', '45 %'),
                    @('UiDistanceValue', '2 m'),
                    @('GripXValue', '0 cm'),
                    @('GripYValue', '0 cm'),
                    @('GripZValue', '7 cm'),
                    @('RotPitchValue', '2 °'),
                    @('RotYawValue', '-8 °'),
                    @('RotRollValue', '0 °'))) {
    $control = $window.FindName($pair[0])
    if ($control) { $control.Text = $pair[1] }
}

# A Window that is never shown still has to be measured and arranged before it
# has any visual to render.
#
# TAKEN FROM THE XAML, not written here. Both numbers used to be duplicated in
# this harness, so raising the window height in MainWindow.xaml left the
# preview rendering the old, clipped size and calling it the new state. A
# preview whose dimensions disagree with the shipped window measures nothing -
# and this file's comment block already records two other ways it has lied.
$width = [int][math]::Ceiling($window.Width)
$height = [int][math]::Ceiling($window.Height)

# The window is SHOWN, far off-screen, and rendered from there.
#
# Two earlier attempts were both wrong, and the second one lied convincingly.
# Measuring an unshown Window leaves its content unarranged - a blank 3 KB PNG.
# Detaching the content and arranging that directly does produce a picture, but
# DETACHING BREAKS THE RESOURCE CHAIN: implicit styles (Style with only a
# TargetType) are looked up through the parent chain, so every ComboBox and
# CheckBox silently fell back to the default Windows theme. Keyed StaticResource
# references survive, because those resolve while parsing - which is exactly why
# the panels looked right and the input controls looked untouched, and why the
# preview nearly sent me fixing a problem that was only in the harness.
#
# Showing it off-screen keeps the real tree, the real resources and the real
# layout.
$window.Left = -20000
$window.Top = -20000
$window.ShowInTaskbar = $false
$window.Width = $width
$window.Height = $height
$window.Show()
$window.UpdateLayout()

Write-Host "  rendering at $width x $height px, straight from the XAML"

$visual = $window.Content

$target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
    $width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$target.Render($visual)

$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))

$stream = [System.IO.File]::Create($out)
try { $encoder.Save($stream) } finally { $stream.Dispose() }

$window.Close()

Write-Host "  $out  ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"

if ($script:Unmapped.Count -gt 0) {
    Write-Host "  NICHT UEBERSETZT: $($script:Unmapped.Count) Zeichenkette(n)" -ForegroundColor Yellow
    foreach ($key in $script:Unmapped.Keys) { Write-Host "    $key" -ForegroundColor Yellow }
}
