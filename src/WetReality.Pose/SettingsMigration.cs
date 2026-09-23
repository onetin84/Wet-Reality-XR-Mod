using System.Globalization;
using MelonLoader;

namespace WetReality;

// ALTE VORGABEN NACHZIEHEN - Abschnitt 195.
//
// MelonPreferences uebernimmt einen Wert, der schon in der cfg steht, und
// ignoriert dann eine geaenderte Vorgabe im Quellcode (die Falle aus 100 und
// 157). Der Installer schreibt die cfg bewusst nicht - sie gehoert dem
// Spieler. Wer also vor 1.86 installiert und nur aktualisiert hat, behielt
// z. B. die Menuegroesse 0,45 statt 0,3627 und alle Diagnosen der Beta.
//
// DIE REGEL: ein Wert wird NUR geaendert, wenn er noch genau auf einer
// frueheren Vorgabe steht - dann hat ihn der Spieler nie angefasst. Was
// jemand verstellt hat, bleibt. Jede Aenderung steht im Log, und
// SettingsVersion merkt sich den Stand, damit jeder Schritt genau einmal
// laeuft.
//
// DIE LISTE ist nicht geschaetzt, sondern aus den DLLs aller siebzehn
// ausgelieferten Pakete gelesen (1.2.0 bis 1.86.0, tools/defaults-probe),
// gegen den Quellcode validiert: 267 von 267 Vorgaben der aktuellen DLL
// stimmen. XRBoot hat nie eine Vorgabe geaendert.
//
// Grenze, offen benannt: wer einen alten Wert ABSICHTLICH gesetzt hat - etwa
// SnapAngle 30 -, sieht aus wie jemand, der ihn nie angefasst hat. Vom
// Nutzer so entschieden (Abschnitt 195); im Konfigurator ist er wieder
// einstellbar.
internal static class SettingsMigration
{
    internal const int Current = 1;

    // Schritt 1, Stand Pose 1.100.0. Je Schluessel jede fruehere Vorgabe.
    private static readonly (string key, float[] old)[] FloatsStep1 =
    {
        ("UiScale", new[] { 0.45f }),
        ("SnapAngle", new[] { 30f }),
        ("GripOffsetX", new[] { 0f }),
        ("GripOffsetY", new[] { 0f }),
        ("GripOffsetZ", new[] { 0f }),
        ("RotationOffsetPitch", new[] { 0f }),
        ("RotationOffsetYaw", new[] { 0f }),
        ("RotationOffsetRoll", new[] { 0f }),
        ("HipZoneRadius", new[] { 0.3f, 0.15f }),
        ("ShoulderZoneRadius", new[] { 0.3f, 0.15f }),
        ("WasherZoneRadius", new[] { 0.3f }),
    };

    private static readonly (string key, bool old)[] BoolsStep1 =
    {
        ("WasherDepthNeutral", false),
        ("MenuSetEventSelection", false),
        ("AimChainReport", true),
        ("InteractProbe", true),
        ("MenuMissReport", true),
        ("ProbeHandAssets", true),
    };

    // Die cfg speichert floats als double-Text (0.3626999855041504); ein
    // exakter Vergleich wuerde an der Rundung scheitern.
    private const float Tolerance = 1e-4f;

    internal static void Apply(MelonLogger.Instance log, MelonPreferences_Category settings,
        MelonPreferences_Entry<int> version)
    {
        var from = version.Value;

        if (from >= Current)
            return;

        var changed = 0;
        var kept = 0;

        if (from < 1)
        {
            foreach (var (key, old) in FloatsStep1)
            {
                var entry = settings.GetEntry<float>(key);

                if (entry is null)
                {
                    log.Warning($"settings: migration key {key} not found - skipped");
                    continue;
                }

                if (Math.Abs(entry.Value - entry.DefaultValue) <= Tolerance)
                    continue;

                if (old.Any(value => Math.Abs(entry.Value - value) <= Tolerance))
                {
                    log.Msg($"settings: {key} {F(entry.Value)} -> {F(entry.DefaultValue)}"
                        + "   (was an earlier default, never changed by the player)");
                    entry.Value = entry.DefaultValue;
                    changed++;
                }
                else
                {
                    log.Msg($"settings: {key} {F(entry.Value)} left alone - set by the player"
                        + $" (default now {F(entry.DefaultValue)})");
                    kept++;
                }
            }

            foreach (var (key, old) in BoolsStep1)
            {
                var entry = settings.GetEntry<bool>(key);

                if (entry is null)
                {
                    log.Warning($"settings: migration key {key} not found - skipped");
                    continue;
                }

                if (entry.Value == entry.DefaultValue)
                    continue;

                // Bei einem bool IST der alte Wert der einzige andere - er
                // wird also immer nachgezogen. So entschieden, siehe oben.
                if (entry.Value == old)
                {
                    log.Msg($"settings: {key} {entry.Value} -> {entry.DefaultValue}"
                        + "   (was an earlier default)");
                    entry.Value = entry.DefaultValue;
                    changed++;
                }
            }
        }

        version.Value = Current;
        log.Msg($"settings: SettingsVersion {from} -> {Current}   {changed} value(s) updated to "
            + $"today's defaults, {kept} kept as set by the player");
    }

    private static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
