# Wet Reality XR Mod - configurator strings
#
# Keyed by the ENGLISH text, which is also what MainWindow.xaml contains.
#
# That keeps the XAML readable and the English build authoritative: there is no
# invented key vocabulary to keep in step, and a string that has no entry here
# simply stays English rather than turning into a missing-key placeholder. The
# localiser reports anything it could not map, so drift is visible instead of
# silent.
#
# Set-Language caches each element's ORIGINAL text the first time it walks the
# tree, so switching back to English restores exactly what the XAML said rather
# than a reverse lookup that could go wrong on a duplicate.

# Declared here, not on first use: Set-StrictMode -Version Latest makes READING
# an unset variable an error, so "if (-not $script:Originals)" would throw.
# Caught by the offscreen preview rather than by a user opening the window.
$script:Originals = $null

$script:German = @{

    # ------------------------------------------------------------- header
    'assets/logo.png missing' = 'assets/logo.png fehlt'
    'Settings - and the button below starts the game' =
        'Einstellungen - und der Knopf unten startet das Spiel'
    'Looking for your installation...' = 'Installation wird gesucht ...'
    'Choose folder' = 'Ordner wählen'

    # ------------------------------------------------------------ controls
    'CONTROLS' = 'STEUERUNG'
    'Washer hand' = 'Hand mit der Pistole'
    'Right' = 'Rechts'
    'Left' = 'Links'
    'Turn speed' = 'Drehgeschwindigkeit'
    'Snap turning instead of smooth' = 'Sprungdrehung statt gleitend'
    'Show VR hands' = 'VR-Hände anzeigen'
    'Vibration while spraying' = 'Vibration beim Sprühen'
    'Vibration strength' = 'Vibrationsstärke'
    'Strength follows the nozzle and the washer. The finer values are in MelonPreferences.cfg.' =
        'Die Stärke folgt der Düse und dem Reiniger. Die Feinwerte stehen in MelonPreferences.cfg.'

    # ------------------------------------------------------------- display
    'DISPLAY' = 'ANZEIGE'
    'Menu size' = 'Menügröße'
    'Menu distance' = 'Menüabstand'
    'Show aiming laser' = 'Ziellaser anzeigen'
    'The laser is a development measuring tool and is not needed for normal play.' =
        'Der Laser ist ein Messwerkzeug aus der Entwicklung und für das normale Spiel nicht nötig.'

    # ---------------------------------------------------------------- grip
    'GRIP FINE-TUNING' = 'GRIFF-FEINJUSTIERUNG'
    'Moves and turns the washer within its own frame so that it sits where your hand holds it. In game, hold left grip with X and Y, move your washer hand to where the washer should sit, and let go.' =
        'Verschiebt und dreht die Pistole in ihrem eigenen Bezugssystem, damit sie dort sitzt, wo die Hand sie hält. Im Spiel: linken Griff mit X und Y halten, die Pistolenhand dorthin bewegen, wo die Pistole liegen soll, und loslassen.'
    'Sideways' = 'Seitlich'
    'Height' = 'Höhe'
    'Tilt up and down' = 'Neigung hoch/runter'
    'Turn left and right' = 'Drehung links/rechts'
    'Twist sideways' = 'Kippung seitlich'
    'Forward and back' = 'Vor und zurück'
    'Reset grip' = 'Griff zurücksetzen'

    # -------------------------------------------------------------- footer
    'Open quick guide' = 'Kurzanleitung öffnen'

    # ------------------------------------------------------------- Spielstart
    #
    # Der Schluessel der Beschreibung traegt ein echtes U+2014. Im XAML steht
    # dort die Entitaet &#8212;, und Set-Language sucht mit dem AUFGELOESTEN
    # Text - ein Bindestrich hier wuerde die Zeile still englisch lassen.
    'Let Me Spray the Cat' = 'Lass mich die Katze ansprühen'
    'Disables feline protection. Cats can now be sprayed—at your own moral discretion.' =
        'Deaktiviert den Katzenschutz. Katzen können jetzt angesprüht werden – die moralische Verantwortung liegt bei dir.'
    'Starting through Steam...' = 'Wird über Steam gestartet ...'
    'There are unsaved changes. They will not apply to this session. Start anyway?' =
        'Es gibt ungespeicherte Änderungen. Sie wirken in dieser Sitzung nicht. Trotzdem starten?'
    'Save' = 'Speichern'

    # ------------------------------------------- set by the script at runtime
    'Installation found' = 'Installation gefunden'
    'No installation found' = 'Keine Installation gefunden'
    'Choose the "PowerWash Simulator 2" folder manually.' =
        'Den Ordner "PowerWash Simulator 2" bitte selbst wählen.'
    'Unsaved changes' = 'Nicht gespeicherte Änderungen'
    'deg/s' = 'Grad/s'
    'missing' = 'fehlt'
    'Select the "PowerWash Simulator 2" folder' = 'Ordner "PowerWash Simulator 2" wählen'
    'The quick guide is not available yet.' = 'Die Kurzanleitung ist noch nicht vorhanden.'
    'Expected at' = 'Erwartet unter'
    'MelonPreferences.cfg not found. Start the game once with the mod installed so that it gets created.' =
        'MelonPreferences.cfg nicht gefunden. Starte das Spiel einmal mit installierter Mod, damit sie angelegt wird.'
    'MainWindow.xaml is missing next to this script.' =
        'MainWindow.xaml fehlt neben diesem Skript.'
    'Saved at {0}. Backup written as .bak.' =
        'Gespeichert um {0}. Sicherung als .bak geschrieben.'
    'Saved. Keys not found: {0}' = 'Gespeichert. Nicht gefundene Schlüssel: {0}'
}

# Which guide the button opens. Both exist; the German one is generated with
# the English stylesheet so the two cannot look different.
$script:GuideFile = @{ en = 'QuickGuide.html'; de = 'QuickGuide-de.html' }

# ---------------------------------------------------------------- lookup

function T {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    if (-not $Text) { return $Text }
    if ($script:Language -ne 'de') { return $Text }
    if ($script:German.ContainsKey($Text)) { return $script:German[$Text] }

    # Reported rather than hidden: an unmapped string is a translation gap, and
    # the English fallback would otherwise look deliberate.
    $script:Unmapped[$Text] = $true
    return $Text
}

# ------------------------------------------------------- remembered choice
#
# Under LOCALAPPDATA, not next to the script. The package may well sit in a
# folder the user cannot write to, and a language toggle that throws on a
# read-only folder would be worse than one that forgets.

function Get-LanguageStore {
    return [System.IO.Path]::Combine($env:LOCALAPPDATA, 'WetReality', 'language.txt')
}

function Read-SavedLanguage {
    try {
        $path = Get-LanguageStore

        if (Test-Path -LiteralPath $path) {
            $value = (Get-Content -LiteralPath $path -Raw).Trim().ToLowerInvariant()
            if ($value -in @('de', 'en')) { return $value }
        }
    }
    catch {
        # Unreadable store is not worth reporting; detection takes over.
    }

    return $null
}

function Save-Language {
    param([string] $Value)

    try {
        $path = Get-LanguageStore
        $folder = Split-Path -Parent $path

        if (-not (Test-Path -LiteralPath $folder)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }

        Set-Content -LiteralPath $path -Value $Value -Encoding ASCII
    }
    catch {
        # Nothing to do about it, and nothing depends on it persisting.
    }
}

function Resolve-StartLanguage {
    $saved = Read-SavedLanguage
    if ($saved) { return $saved }

    # The UI culture, not the format culture: someone running an English Windows
    # with German number formats wants an English window.
    try {
        if ([System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'de') {
            return 'de'
        }
    }
    catch {
    }

    return 'en'
}

# ------------------------------------------------------------ applying it
#
# Walks the LOGICAL tree, not the visual one: this runs before the window is
# shown, and the visual tree does not exist until layout has happened.

function Set-Language {
    param(
        [Parameter(Mandatory)] $Window,
        [Parameter(Mandatory)] [string] $Value
    )

    $script:Language = $Value

    # Element -> its original XAML text, filled on the first walk. Switching
    # back to English restores from here rather than translating in reverse.
    if (-not $script:Originals) {
        $script:Originals = New-Object 'System.Collections.Generic.Dictionary[object,string]'
    }

    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue($Window)

    while ($queue.Count -gt 0) {
        $node = $queue.Dequeue()

        foreach ($child in [System.Windows.LogicalTreeHelper]::GetChildren($node)) {
            if ($child -is [System.Windows.DependencyObject]) { $queue.Enqueue($child) }
        }

        # TextBlock carries Text; Button and ComboBoxItem carry Content, and only
        # when that content is a plain string - a Button whose content is a panel
        # is localised through the TextBlocks inside it.
        if ($node -is [System.Windows.Controls.TextBlock]) {
            if (-not $script:Originals.ContainsKey($node)) {
                $script:Originals[$node] = $node.Text
            }

            # Empty labels are filled by the script later; nothing to translate.
            if ($script:Originals[$node]) {
                $node.Text = T $script:Originals[$node]
            }
        }
        elseif ($node -is [System.Windows.Controls.ContentControl] -and $node.Content -is [string]) {
            if (-not $script:Originals.ContainsKey($node)) {
                $script:Originals[$node] = [string] $node.Content
            }

            $node.Content = T $script:Originals[$node]
        }
    }
}
