using MelonLoader;
using UnityEngine;

namespace WetReality;

// DIE AUSRUESTUNGSMESSUNG, Abschnitt 204. Reine Messung, unter DevMode.
//
// Gemeldet: gekaufte Washer sind in VR nicht anwaehlbar. Die Logs sagen,
// warum das nicht an zwei festen Zustaenden liegt - InvokeSwitchGun laeuft
// durch PVLight, UXLight, UXProfessional, SCLight und PVProfessional. Aber
// ConfigurationManager fuehrt PowerWasherBrandGroups und
// LastUsedWasherConfigPerGroup, und PwsPlayerInput trennt Druck
// (HandleEquipmentPressed -> TryCycleEquipmentGroup) von Halten
// (HandleEquipmentHeld -> TryOpenHUDRadialMenu). Die Hypothese: der Druck
// wechselt nur die MARKE, die Stufen innerhalb einer Marke gibt es nur in der
// Waehlscheibe (LT/RT = RadialNavigateSubMenu).
//
// Das ist eine Hypothese aus Signaturen, keine Messung. Dieser Lauf misst
// jede Frage mit einer eigenen Taste, und jede Taste schreibt den Zustand
// davor und danach ins Log:
//
//   Einfg              Stufe vor   ConfigurationManager.SwitchToNextAvailablePowerWasherInGroup(+1)
//   Umschalt+Einfg     Stufe zurueck (-1)
//   Strg+Einfg         WasherInputHandler.OnSwitchPowerWasher(+1), zum Vergleich
//   Entf               Waehlscheibe auf/zu ueber den Weg des Spiels:
//                      PwsPlayerInput.HandleEquipmentHeld / HandleEquipmentReleased
//   Ende               Waehlscheibe auf/zu ueber BaseInput.InvokeOpenRadialMenu /
//                      InvokeCloseRadialMenu
//   Bild auf/ab        bei offener Waehlscheibe: InvokeNavigateSubMenu(+1/-1)
//                      sonst: WasherInputHandler.OnChangeNozzleWidth(+1/-1)
//   Umschalt+F7        +100000 Geld, ShopManager.AddCurrencyRemote(v, usingMuckCoin: false)
//
// Solange eine der beiden Waehlscheiben offen ist, geht der rechte Stick als
// RadialWheelNavigateRaw ans Spiel, und einmal pro Sekunde steht im Log, was
// das Spiel davon zurueckliest und ob der Menuemodus der Mod anspringt. Die
// Mod sperrt dabei NICHTS - ist menuMode false, dreht der Stick weiter. Das
// ist Absicht: gemessen wird, was ohne Eingriff geschieht.
//
// NUR SICHERE FORMEN. int, bool, float, Enum und Klassenreferenzen; keine
// Liste und kein Dictionary, weil BlittableListWrapper in diesem Build
// defekt ist (Abschnitt 36). Die Gruppenlisten werden darum nicht gelesen,
// sondern ihre Wirkung am Namen des Washers davor und danach.
internal sealed class EquipmentProbe
{
    private const int MoneyStep = 100000;

    private bool wheelOpenGame;
    private bool wheelOpenInvoke;
    private float nextWheelReport;

    internal bool WheelOpen => wheelOpenGame || wheelOpenInvoke;

    internal void Update(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput? input,
        bool menuMode, Vector2 rightStick)
    {
        if (input is null || input == null)
        {
            wheelOpenGame = false;
            wheelOpenInvoke = false;
            return;
        }

        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            return;

        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        try
        {
            if (Input.GetKeyDown(KeyCode.Insert))
            {
                if (ctrl)
                {
                    var handler = FindWasherInputHandler();
                    Act(log, "washer: OnSwitchPowerWasher(+1)", menuMode, () =>
                    {
                        if (handler is null)
                            throw new InvalidOperationException("no WasherInputHandler");
                        handler.OnSwitchPowerWasher(1);
                    });
                }
                else
                {
                    var direction = shift ? -1 : 1;
                    Act(log, $"washer: SwitchToNextAvailablePowerWasherInGroup({direction:+0;-0})",
                        menuMode, () =>
                        {
                            var manager = FindConfigurationManager();
                            if (manager is null)
                                throw new InvalidOperationException("no ConfigurationManager");
                            manager.SwitchToNextAvailablePowerWasherInGroup(direction);
                        });
                }
            }

            if (Input.GetKeyDown(KeyCode.Delete))
            {
                var pws = input.TryCast<Il2CppFuturLab.PW2.PwsPlayerInput>();

                if (pws is null)
                    log.Msg("equipment probe: input is not PwsPlayerInput - wheel (game path) skipped");
                else if (!wheelOpenGame)
                {
                    Act(log, "wheel: HandleEquipmentHeld(PowerWasher)", menuMode,
                        () => pws.HandleEquipmentHeld(Il2CppFuturLab.PW2.EquipmentType.PowerWasher));
                    wheelOpenGame = true;
                }
                else
                {
                    Act(log, "wheel: HandleEquipmentReleased(PowerWasher)", menuMode,
                        () => pws.HandleEquipmentReleased(Il2CppFuturLab.PW2.EquipmentType.PowerWasher));
                    wheelOpenGame = false;
                }
            }

            if (Input.GetKeyDown(KeyCode.End))
            {
                if (!wheelOpenInvoke)
                {
                    Act(log, "wheel: InvokeOpenRadialMenu(PowerWasher)", menuMode,
                        () => input.InvokeOpenRadialMenu(Il2CppFuturLab.PW2.EquipmentType.PowerWasher));
                    wheelOpenInvoke = true;
                }
                else
                {
                    Act(log, "wheel: InvokeCloseRadialMenu", menuMode,
                        () => input.InvokeCloseRadialMenu());
                    wheelOpenInvoke = false;
                }
            }

            var pageUp = Input.GetKeyDown(KeyCode.PageUp);
            var pageDown = Input.GetKeyDown(KeyCode.PageDown);

            if (pageUp || pageDown)
            {
                var direction = pageUp ? 1 : -1;

                if (WheelOpen)
                {
                    Act(log, $"wheel: InvokeNavigateSubMenu({direction:+0;-0})", menuMode,
                        () => input.InvokeNavigateSubMenu(direction));
                }
                else
                {
                    var handler = FindWasherInputHandler();
                    Act(log, $"nozzle width: OnChangeNozzleWidth({direction:+0;-0})", menuMode, () =>
                    {
                        if (handler is null)
                            throw new InvalidOperationException("no WasherInputHandler");
                        handler.OnChangeNozzleWidth(direction);
                    });
                }
            }

            if (shift && Input.GetKeyDown(KeyCode.F7))
                AddMoney(log);

            if (WheelOpen)
                FeedWheel(log, input, menuMode, rightStick);
        }
        catch (Exception exception)
        {
            log.Warning($"equipment probe threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Der Stick als Richtung an die Waehlscheibe, jedes Frame. Kein Gamepad
    // ist angemeldet, also ruft das Spiel OnRadialWheelNavigate nie selbst -
    // niemand ueberschreibt den Wert zwischen zwei Frames.
    private void FeedWheel(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput input,
        bool menuMode, Vector2 rightStick)
    {
        input.RadialWheelNavigateRaw = rightStick;

        if (Time.unscaledTime < nextWheelReport)
            return;

        nextWheelReport = Time.unscaledTime + 1f;
        var readBack = input.RadialWheelNavigateRaw;
        log.Msg($"wheel open ({(wheelOpenGame ? "game path" : "invoke path")}): "
            + $"stick ({rightStick.x:0.##}, {rightStick.y:0.##})  "
            + $"read back ({readBack.x:0.##}, {readBack.y:0.##})  menuMode {menuMode}  "
            + Describe());
    }

    private static void Act(MelonLogger.Instance log, string what, bool menuMode, Action action)
    {
        var before = Describe();

        try
        {
            action();
        }
        catch (Exception exception)
        {
            log.Warning($"equipment probe: {what} threw {exception.GetType().Name}: {exception.Message}");
            return;
        }

        log.Msg($"equipment probe: {what}  menuMode {menuMode}");
        log.Msg($"    before {before}");
        log.Msg($"    after  {Describe()}");
    }

    private static void AddMoney(MelonLogger.Instance log)
    {
        // 1.115.0 las den ShopManager ueber ConfigurationManager.m_shopManager
        // und meldete in der Basis zweimal "no ShopManager". Jetzt zuerst aus
        // dem Spielzustand: GameplayStateBase.ShopManager, und HubGameplayState
        // - die Basis - erbt von GameplayStateBase.
        var manager = FindConfigurationManager();
        var source = "GameplayStateBase.ShopManager";
        Il2CppFuturLab.PW2.ShopManager? shop = null;

        try
        {
            var current = Il2CppFuturLab.PW2.PwsScreenManager.Instance?.MainViewport?.CurrentState;
            var gameplay = current is null || current == null
                ? null
                : current.TryCast<Il2CppFuturLab.PW2.GameplayStateBase>();
            shop = gameplay is null || gameplay == null ? null : gameplay.ShopManager;
        }
        catch (Exception exception)
        {
            log.Msg($"equipment probe: state shop read threw {exception.GetType().Name}");
        }

        if (shop is null || shop == null)
        {
            source = "ConfigurationManager.m_shopManager";
            shop = manager?.m_shopManager;
        }

        if (shop is null || shop == null)
        {
            log.Msg($"equipment probe: money skipped - no ShopManager "
                + $"(configuration manager {(manager is null ? "absent" : "present")})");
            return;
        }

        var before = ReadMoney(manager);
        var returned = shop.AddCurrencyRemote(MoneyStep, false);
        log.Msg($"equipment probe: AddCurrencyRemote({MoneyStep}, usingMuckCoin: false) via {source} "
            + $"returned {returned}   money before {before}  after {ReadMoney(manager)}");
    }

    private static string ReadMoney(Il2CppFuturLab.PW2.ConfigurationManager? manager)
    {
        if (manager is null)
            return "?";

        try
        {
            var save = manager.m_playerSaveData;
            return save is null || save == null ? "?" : save.PrimaryCurrency.Value.ToString();
        }
        catch (Exception exception)
        {
            return $"? ({exception.GetType().Name})";
        }
    }

    private static Il2CppFuturLab.PW2.ConfigurationManager? FindConfigurationManager()
    {
        var equipment = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.EquipmentManager>();
        return equipment is null || equipment == null ? null : equipment.ConfigurationManager;
    }

    private static Il2CppFuturLab.PW2.WasherInputHandler? FindWasherInputHandler()
    {
        var character = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.PlayerCharacter>();
        return character is null || character == null ? null : character.m_washerInputHandler;
    }

    // Eine Zeile mit allem, was die naechste Umsetzung braucht: Marke, Klasse
    // und Name des Washers, Duese mit Gruppe, Vibrationskategorie und
    // Strahlanzahl, die Breite (DynamicScale) samt der Stufung des Spiels, und
    // die Verlaengerung.
    private static string Describe()
    {
        try
        {
            var equipment = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.EquipmentManager>();

            if (equipment is null || equipment == null)
                return "[no EquipmentManager]";

            var configuration = equipment.ConfigurationManager?.CurrentConfiguration;

            if (configuration is null)
                return "[no configuration]";

            var gun = configuration.PressureGun;
            var washer = gun is null || gun == null ? "none" : gun.name;
            var washerClass = gun is null || gun == null ? "?" : gun.Class.ToString();
            var brand = gun is null || gun == null || gun.BrandData is null || gun.BrandData == null
                ? "?"
                : gun.BrandData.name;

            var nozzle = configuration.Nozzle;
            var type = nozzle?.NozzleType;
            var nozzleName = type?.ShortName ?? "none";
            var group = type is null ? -1 : type.NozzleGroup;
            var multiple = type is not null && type.HasMultipleNozzles;
            var count = nozzle is null ? -1 : nozzle.NozzleCount;
            var vibration = nozzle is null ? "?" : nozzle.VibrationCategory.ToString();

            var extension = configuration.Extension?.ExtensionType?.ShortName ?? "none";

            return $"[washer {washer} class {washerClass} brand {brand}"
                + $"  nozzle {nozzleName} group {group} count {count} multi {(multiple ? "Y" : "n")}"
                + $" vib {vibration}"
                + $"  width {configuration.DynamicScale:0.###}"
                + $" (steps {equipment.m_adaptableNozzleIncrements}"
                + $" x {equipment.m_adaptableNozzleDynamicScalePerIncrement:0.###})"
                + $"  ext {extension}]";
        }
        catch (Exception exception)
        {
            return $"[describe threw {exception.GetType().Name}: {exception.Message}]";
        }
    }
}
