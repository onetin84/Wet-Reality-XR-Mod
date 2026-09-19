using UnityEngine;

namespace WetReality;

// EIN ORT FUER DEN ZTEST - Abschnitt 104.
//
// Die drei Namen standen privat in GameUi, und der Zeiger-Laser braucht
// dasselbe. Zwei Kopien derselben Liste waeren zwei Orte, an denen ein vierter
// Name fehlen kann; die Mod hat solche Doppelungen schon einmal einen Lauf
// gekostet.
//
// WARUM OHNE HasProperty, und das ist gemessen: unity_GUIZTestMode ist in
// Unitys UI-Shader NICHT deklariert - es steht dort nur als
// ZTest [unity_GUIZTestMode], und Unity selbst setzt es per SetInt. Ein erster
// Versuch hinter HasProperty setzte darum bei 402 Graphics und sieben Shadern
// NULL Materialien, mit einer Begruendung, die wie ein Befund aussah. Ohne das
// Tor: 13 Materialien, Ruecklesung 8, und das Clipping war weg.
//
// Ein SetInt auf einen Namen, den ein Shader nicht liest, ist wirkungslos und
// harmlos - es legt nur einen Eintrag in die Property-Liste des Materials.
internal static class UiDepth
{
    // unity_GUIZTestMode gehoert Unitys UI-Shadern, _ZTestMode dem von
    // TextMeshPro, _ZTest den URP-Varianten.
    internal static readonly string[] ZTestProperties =
    {
        "unity_GUIZTestMode",
        "_ZTestMode",
        "_ZTest",
    };

    // UnityEngine.Rendering.CompareFunction.Always und .LessEqual, als Zahlen:
    // hier sind es Materialwerte, und kein Aufzaehlungstyp muss ueber die
    // Interop-Grenze.
    internal const int CompareAlways = 8;
    internal const int CompareLessEqual = 4;

    // Liest den Vorwert, bevor geschrieben wird.
    //
    // Fuer eine nicht deklarierte Eigenschaft liest GetInt 0, und 0 waere als
    // ZTest "Disabled" - zurueckgeschrieben also gerade NICHT der Zustand von
    // vorher. Darum wird 0 als LEqual gemeldet, was Unity fuer einen Canvas
    // ausserhalb des Overlay-Modus setzt.
    internal static int ReadPrevious(Material material)
    {
        try
        {
            var read = material.GetInt(ZTestProperties[0]);

            return read == 0 ? CompareLessEqual : read;
        }
        catch
        {
            return CompareLessEqual;
        }
    }

    // Setzt alle Namen auf Always und gibt zurueck, wie viele Schreibvorgaenge
    // durchgingen. Null heisst: dieses Material nimmt keinen davon.
    internal static int ForceAlways(Material material) => WriteAll(material, CompareAlways);

    // Nimmt zurueck, und zwar ALLE Namen - gesetzt wurden auch alle, und ein
    // vergessener waere ein halb zurueckgenommener Zustand.
    internal static int Restore(Material material, int previous) =>
        WriteAll(material, previous);

    private static int WriteAll(Material material, int value)
    {
        var written = 0;

        for (var index = 0; index < ZTestProperties.Length; index++)
        {
            try
            {
                material.SetInt(ZTestProperties[index], value);
                written++;
            }
            catch
            {
                // Ein Name, der sich nicht setzen laesst, kostet nur sich
                // selbst.
            }
        }

        return written;
    }

    // Die Ruecklesung, fuer den Bericht. Sie beweist, dass der Schreibvorgang
    // in der Property-Liste gelandet ist - nicht, was der Shader daraus macht.
    internal static int ReadBack(Material material)
    {
        try
        {
            return material.GetInt(ZTestProperties[0]);
        }
        catch
        {
            return -1;
        }
    }
}
