# Measures the scroll overflow of MainWindow.xaml as a NUMBER, so a layout
# change can be compared instead of eyeballed. Section 108 fixed the scrolling;
# every panel added since has to be measured against that, and the margin is
# thin. Development tool, not shipped.
#
# Reports ExtentHeight - ViewportHeight of the main ScrollViewer. Zero or less
# means no scrolling. Run before and after a change and compare.

param([ValidateSet('en', 'de')] [string] $Lang = 'en')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $root 'tools/frontend/MainWindow.xaml'

$reader = New-Object System.Xml.XmlTextReader (New-Object System.IO.StringReader (
    [System.IO.File]::ReadAllText($xamlPath)))
$window = [Windows.Markup.XamlReader]::Load($reader)

# Same sizing the tool itself does: the window is laid out for its designed
# width and whatever height the work area allows.
$work = [System.Windows.SystemParameters]::WorkArea
$window.Width = [math]::Min($window.Width, $work.Width)
$window.Height = [math]::Min($window.Height, $work.Height)

# Off-screen so nothing flashes on the desktop, then forced through a full
# measure and arrange - an unarranged tree reports zero for everything.
$window.Left = -10000
$window.Top = -10000
$window.ShowActivated = $false
$window.Show()
$window.UpdateLayout()

function Find-ScrollViewer($element) {
    if ($element -is [System.Windows.Controls.ScrollViewer]) { return $element }

    $count = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($element)

    for ($index = 0; $index -lt $count; $index++) {
        $child = [System.Windows.Media.VisualTreeHelper]::GetChild($element, $index)
        $found = Find-ScrollViewer $child
        if ($null -ne $found) { return $found }
    }

    return $null
}

$scroll = Find-ScrollViewer $window

if ($null -eq $scroll) {
    $window.Close()
    throw 'No ScrollViewer found - the layout changed shape, not just size.'
}

$overflow = [math]::Round($scroll.ExtentHeight - $scroll.ViewportHeight, 1)

'window      {0} x {1}' -f [int]$window.Width, [int]$window.Height
'work area   {0} x {1}' -f [int]$work.Width, [int]$work.Height
'extent      {0}' -f [math]::Round($scroll.ExtentHeight, 1)
'viewport    {0}' -f [math]::Round($scroll.ViewportHeight, 1)
'OVERFLOW    {0} px  {1}' -f $overflow, $(if ($overflow -gt 0) { 'SCROLLS' } else { 'no scrolling' })

# The height of each panel in the top row, because that row's height is set by
# its TALLEST column - adding to a short column is free, adding to the tall one
# is not.
foreach ($name in @('VrHandsCheck', 'UiScaleSlider', 'TeleportCheck')) {
    $control = $window.FindName($name)
    if ($null -ne $control) {
        $panel = $control
        while ($null -ne $panel.Parent -and -not ($panel -is [System.Windows.Controls.Border])) {
            $panel = $panel.Parent
        }
        'panel of {0,-16} height {1}' -f $name, [math]::Round($panel.ActualHeight, 1)
    }
}

$window.Close()
