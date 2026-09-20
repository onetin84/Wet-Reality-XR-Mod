using MelonLoader;
using UnityEngine;

namespace WetReality;

// DAS WERKZEUG AUS DEM BILD NEHMEN, SOLANGE EINE UI OFFEN IST - UND ZWAR OHNE
// EINEN EINZIGEN ASSET-NAMEN.
//
// Gemeldet: in den DLC steht das Werkzeug beim Oeffnen des Menues weiter im
// Bild und liegt zusammen mit dem Zeigestrahl ueber der Oberflaeche. Vermutet
// wurden unbekannte DLC-Objektnamen.
//
// Der Befund ist unbequemer: DER MOD HAT BISHER GAR KEINE UI-AUSBLENDUNG.
// Keine Stelle reagiert auf menuMode mit einem Sichtbarkeitsschreibzugriff.
// Was im Hauptspiel wie eine Ausblendung aussieht, ist unbelegt - was dort
// weniger auffaellt, ist die kleine Basispistole gegen ein DLC-Werkzeug mit
// elf Meshes und Teleskoparm.
//
// Die Vermutung trifft trotzdem die richtige Stelle, denn beide Auswahlformen,
// die dieses Projekt schon hat, greifen im DLC zu kurz:
//
//   DER NAME       WasherMeshes sucht "PWG_PW2" und "_PW_UX". Der DLC-Washer
//                  traegt keines von beiden (Abschnitt 114).
//
//   DIE LISTE DES  Gemessen im DLC-Lauf, Log 26-9-19_2-8-22: body[1] und
//   SPIELS         body[2] liegen unter PWG_HUT_PW_SCHeadMid(Clone)/
//                  TelescopicArm_MovingPart/TelescopicArm/
//                  WSG_HUT_SCHeadTelescope_01 und _02 - TEILE DES WERKZEUGS,
//                  die der PowerWasherAssembler nicht nennt. Seine
//                  Positivliste ist im DLC unvollstaendig.
//
// Was bleibt, ist die HIERARCHIE, und ihre Anker gehoeren dem Spiel:
//
//   1. DER ANKER KOMMT AUS DEM SPIEL SELBST. BaseCharacterVisuals fuehrt
//      m_equipmentAnchor als Feld, je einmal fuer die erste und die dritte
//      Person, erreichbar ueber PlayerCharacter.FirstPersonVisuals und
//      .ThirdPersonVisuals. Kein Transform.Find, kein "PowerWasher_Assembly",
//      kein "Third Person Visuals" - genau die gepinnten Pfade, die
//      Abschnitt 113 als Namenstests mit sechs Gelenken entlarvt hat.
//
//   2. GENOMMEN WIRD JEDER RENDERER DARUNTER, in jeder Tiefe. Ein DLC darf
//      seine Meshes nennen, wie es will, und sie haengen lassen, wo es will.
//      Kein Typfilter: unter dem Ausruestungsanker soll im Menue alles weg.
//
//   3. DREI AUSNAHMEN, alle strukturell. Die Wassertechnik unter einem
//      NozzleAnchor behaelt ihre eigene Sichtbarkeit - sie zeichnet im Menue
//      nichts, und ihre Duesen gehoeren dem Spiel. Alles unterhalb der
//      Mod-eigenen VR-Hand bleibt, denn die Hand haelt den Zeigestrahl. Und
//      ein Knoten, der selbst PowerWasherAssembler oder NozzleAnchor traegt,
//      wird nie abgeschaltet: ein toter Zusammenbauer waere ein teurer Preis
//      fuer ein sauberes Bild.
//
// GESCHRIEBEN WIRD DAS GAMEOBJECT, NICHT DER RENDERER. Das ist keine Vorliebe,
// sondern der Messwert aus Abschnitt 114: drei Renderer auf enabled false
// geschrieben, keine Ausnahme - und die Arme blieben sichtbar. Etwas im Spiel
// schreibt enabled zurueck. Ein deaktivierter Knoten ist dagegen immun, und
// aus demselben Grund nimmt ApplyArmMode denselben Weg.
//
// ZURUECKGEGEBEN WIRD GENAU DAS GENOMMENE. Die Liste haelt die selbst
// abgeschalteten Knoten, und nur die werden wieder aktiviert. Ein Knoten, der
// schon vorher aus war - eine nicht gewaehlte Duese zum Beispiel -, wird nie
// angefasst; activeSelf entscheidet das beim Nehmen, wie bei den Armen.
//
// ForceHideWasher, den eigenen Schalter des Spiels, schreibt diese Fassung
// NICHT. Er wird gelesen und gemeldet. Erst fragen, wer den Zustand haelt:
// steht er im Menue von selbst auf true, fuehrt das Spiel die Sache schon, und
// dann ist ein Aufruf billiger als dieser ganze Durchlauf.
internal sealed class ToolHide
{
    // Die selbst abgeschalteten Knoten und ihre Kennungen. Zwei Listen wie bei
    // den Armen: die Kennung verhindert die Doppelnahme, ohne pro Frame
    // Referenzen zu vergleichen.
    private readonly List<GameObject> taken = new();
    private readonly List<int> takenIds = new();

    private float nextSweep;
    private int reportedTaken = -1;
    private int reportedReturns = -1;
    private string route = "-";

    internal bool Hiding { get; private set; }

    internal string Status { get; private set; } = "tool: visible";

    // want traegt die GANZE Bedingung. Diese Klasse kennt weder menuMode noch
    // den Zeigestrahl - die Aufrufstelle entscheidet, und damit gilt fuer jede
    // kuenftige Einblendung dasselbe Tor ohne eine Aenderung hier.
    internal void Apply(MelonLogger.Instance log, bool want,
        Il2CppFuturLab.PW2.CharacterVisualsFirstPerson? visuals,
        Transform? fallbackRoot, Transform? ownHand)
    {
        try
        {
            if (!want)
            {
                Restore(log, "UI closed");
                return;
            }

            // Der erste Durchlauf sofort, danach auf 0,5 s. Ein Werkzeug- oder
            // Duesenwechsel baut neue Klone, und die stuenden sonst im Bild,
            // waehrend die UI noch offen ist.
            if (!Hiding || Time.unscaledTime >= nextSweep)
            {
                nextSweep = Time.unscaledTime + 0.5f;
                Sweep(log, visuals, fallbackRoot, ownHand);
            }

            Hiding = true;

            // WER DEN ZUSTAND HAELT, und das ist der Messteil dieses Laufs.
            // Gelesen wird genau das, was geschrieben wurde: activeSelf der
            // selbst abgeschalteten Knoten. Holt das Spiel einen zurueck,
            // steht es im Log statt im Kopfhoerer eines Testers.
            Reassert(log);

            Status = $"tool: hidden {taken.Count} [{route}]";
        }
        catch (Exception exception)
        {
            log.Warning($"  tool hide threw {exception.GetType().Name}: {exception.Message}");
            Status = "tool: hide failed";
        }
    }

    // Die Ruecknahme. Billig und still, solange nichts genommen ist - sie
    // laeuft pro Frame, sobald keine UI offen ist.
    internal void Restore(MelonLogger.Instance log, string reason)
    {
        if (taken.Count == 0)
        {
            Hiding = false;
            Status = "tool: visible";
            return;
        }

        var back = 0;
        var gone = 0;

        for (var index = 0; index < taken.Count; index++)
        {
            var target = taken[index];

            // Unity-null UND Muster-null: ein Duesenwechsel im offenen Menue
            // zerstoert den Klon, und "is null" sieht das nicht.
            if (target is null || target == null)
            {
                gone++;
                continue;
            }

            target.SetActive(true);
            back++;
        }

        Forget();
        log.Msg($"  tool hide: off ({reason}), {back} node(s) back"
            + (gone > 0 ? $", {gone} destroyed meanwhile" : ""));
    }

    // Beim Levelwechsel: vergessen, nicht schreiben. Die Knoten des alten
    // Spielers sind tot, und ein SetActive darauf waere eine Ausnahme ohne
    // Gegenwert.
    internal void Reset()
    {
        Forget();
    }

    private void Forget()
    {
        taken.Clear();
        takenIds.Clear();
        nextSweep = 0f;
        reportedTaken = -1;
        reportedReturns = -1;
        route = "-";
        Hiding = false;
        Status = "tool: visible";
    }

    private void Sweep(MelonLogger.Instance log,
        Il2CppFuturLab.PW2.CharacterVisualsFirstPerson? visuals,
        Transform? fallbackRoot, Transform? ownHand)
    {
        var roots = Roots(visuals, fallbackRoot);

        for (var index = 0; index < roots.Count; index++)
            Take(roots[index], ownHand);

        // EINMAL PRO BESTAND, nicht pro Frame - und mit den Pfaden, nicht nur
        // der Zahl. Die Lehre aus Abschnitt 112: eine Zahl kann nicht sagen,
        // WELCHE. Beim naechsten DLC steht hier ohne Ratespiel, wo die
        // Geometrie diesmal hing.
        if (taken.Count == reportedTaken)
            return;

        reportedTaken = taken.Count;
        log.Msg($"  tool hide: {taken.Count} node(s) deactivated under "
            + $"{roots.Count} equipment anchor(s) [{route}]"
            + $"   game ForceHideWasher {FlagText(visuals)}");

        for (var index = 0; index < taken.Count && index < 6; index++)
        {
            var target = taken[index];

            if (target is null || target == null)
                continue;

            log.Msg($"    tool[{index}] {PathOf(target.transform)}");
        }
    }

    // DIE ANKER DES SPIELS, in dieser Reihenfolge und mit Vermerk im Log,
    // welcher Weg gegriffen hat. Der Rueckfall auf die gefahrene Assembly ist
    // Absicht: ohne aufgeloeste Visuals soll die Ausblendung nicht ausfallen,
    // aber im Log soll stehen, dass sie am schwaecheren Anker hing.
    private List<Transform> Roots(
        Il2CppFuturLab.PW2.CharacterVisualsFirstPerson? visuals,
        Transform? fallbackRoot)
    {
        var roots = new List<Transform>();
        var fromGame = 0;

        if (visuals is not null && visuals != null)
        {
            if (Add(roots, AnchorOf(visuals)))
                fromGame++;

            try
            {
                var character = visuals.PlayerCharacter;

                if (character is not null && character != null)
                {
                    if (Add(roots, AnchorOf(character.FirstPersonVisuals)))
                        fromGame++;

                    if (Add(roots, AnchorOf(character.ThirdPersonVisuals)))
                        fromGame++;
                }
            }
            catch
            {
                // Eine unvollstaendige Ankerliste ist kein Grund, nichts zu
                // tun: der Rueckfall unten haelt wenigstens die erste Hand.
            }
        }

        if (roots.Count == 0)
            Add(roots, fallbackRoot);

        route = roots.Count == 0
            ? "NO ANCHOR"
            : fromGame > 0 ? $"{fromGame} via visuals" : "ASSEMBLY FALLBACK";

        return roots;
    }

    private static Transform? AnchorOf(Il2CppFuturLab.PW2.BaseCharacterVisuals? visuals)
    {
        if (visuals is null || visuals == null)
            return null;

        try
        {
            var anchor = visuals.m_equipmentAnchor;

            return anchor is null || anchor == null ? null : anchor;
        }
        catch
        {
            return null;
        }
    }

    // Doppelte Anker sind zu erwarten: FirstPersonVisuals ist derselbe
    // Bestand, den GunRender schon gefunden hat. Verglichen wird ueber die
    // Kennung, nicht ueber den Namen.
    private static bool Add(List<Transform> roots, Transform? candidate)
    {
        if (candidate is null || candidate == null)
            return false;

        var id = candidate.GetInstanceID();

        for (var index = 0; index < roots.Count; index++)
            if (roots[index].GetInstanceID() == id)
                return false;

        roots.Add(candidate);
        return true;
    }

    private void Take(Transform root, Transform? ownHand)
    {
        var all = root.GetComponentsInChildren<Renderer>(true);

        if (all is null)
            return;

        for (var index = 0; index < all.Length; index++)
        {
            var renderer = all[index];

            if (renderer is null || renderer == null)
                continue;

            var node = renderer.transform;

            if (UnderNozzleAnchor(node, root))
                continue;

            if (ownHand is not null && ownHand != null && Underneath(node, ownHand))
                continue;

            if (Structural(node))
                continue;

            var target = renderer.gameObject;

            // activeSelf wie bei den Armen: ein Knoten, der schon aus ist,
            // wird nicht genommen - sonst gaebe die Ruecknahme ihn frei, und
            // eine abgewaehlte Duese stuende im Bild.
            if (target is null || target == null || !target.activeSelf)
                continue;

            target.SetActive(false);

            var id = target.GetInstanceID();

            if (takenIds.Contains(id))
                continue;

            takenIds.Add(id);
            taken.Add(target);
        }
    }

    private void Reassert(MelonLogger.Instance log)
    {
        var returns = 0;

        for (var index = 0; index < taken.Count; index++)
        {
            var target = taken[index];

            if (target is null || target == null)
                continue;

            if (!target.activeSelf)
                continue;

            target.SetActive(false);
            returns++;
        }

        if (returns == reportedReturns)
            return;

        reportedReturns = returns;

        if (returns > 0)
            log.Warning($"  tool hide: {returns} node(s) came back and were taken "
                + "again - something in the game re-activates them");
    }

    // Der Schalter des Spiels, NUR GELESEN. Beide Seiten in EINER Zeile, damit
    // die Momentaufnahme zusammenpasst: ein Wert je Logzeile waere wieder die
    // Paarung ueber zwei Zeilen, die dieses Projekt schon Fehldiagnosen
    // gekostet hat.
    private static string FlagText(
        Il2CppFuturLab.PW2.CharacterVisualsFirstPerson? visuals)
    {
        if (visuals is null || visuals == null)
            return "unknown (no visuals)";

        try
        {
            var first = visuals.ForceHideWasher;
            var character = visuals.PlayerCharacter;
            var third = character is null || character == null
                ? null
                : character.ThirdPersonVisuals;

            var thirdText = third is null || third == null
                ? "-"
                : third.ForceHideWasher.ToString();

            return $"fp {first}   tp {thirdText}";
        }
        catch (Exception exception)
        {
            return $"read threw {exception.GetType().Name}";
        }
    }

    // Aufwaerts bis zur Wurzel, nicht weiter. Dieselbe Form wie in GunRender:
    // die Komponente NozzleAnchor markiert die Wassertechnik JEDER Duese, nicht
    // nur der aktiven.
    private static bool UnderNozzleAnchor(Transform? node, Transform root)
    {
        var walk = node;
        var rootId = root.GetInstanceID();

        while (walk is not null && walk != null)
        {
            var anchor = walk.GetComponent<Il2CppFuturLab.PW2.NozzleAnchor>();

            if (anchor is not null && anchor != null)
                return true;

            if (walk.GetInstanceID() == rootId)
                return false;

            walk = walk.parent;
        }

        return false;
    }

    // Knoten, die das Spiel zum Zusammenbauen braucht. Ein Renderer sitzt in
    // den gemessenen Hierarchien immer auf einem Blatt, also kostet diese
    // Ausnahme nichts - und legt ein DLC den Zusammenbauer doch einmal auf
    // einen Mesh-Knoten, bleibt er am Leben.
    private static bool Structural(Transform node)
    {
        try
        {
            var assembler = node.GetComponent<Il2CppFuturLab.PW2.PowerWasherAssembler>();

            if (assembler is not null && assembler != null)
                return true;

            var anchor = node.GetComponent<Il2CppFuturLab.PW2.NozzleAnchor>();

            return anchor is not null && anchor != null;
        }
        catch
        {
            return true;
        }
    }

    private static bool Underneath(Transform? node, Transform root)
    {
        var walk = node;
        var rootId = root.GetInstanceID();
        var guard = 0;

        while (walk is not null && walk != null && guard++ < 24)
        {
            if (walk.GetInstanceID() == rootId)
                return true;

            walk = walk.parent;
        }

        return false;
    }

    private static string PathOf(Transform node)
    {
        var path = node.name;
        var walk = node.parent;
        var guard = 0;

        while (walk is not null && walk != null && guard++ < 24)
        {
            path = walk.name + "/" + path;
            walk = walk.parent;
        }

        return path;
    }
}
