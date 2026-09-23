================================================================================
  WET REALITY XR MOD  @@VERSION@@  -  BETA
  Room-scale VR for PowerWash Simulator 2
  by Tino
================================================================================

WHAT THIS IS

  A VR mod that puts you inside PowerWash Simulator 2 with a headset and motion
  controllers. The washer follows your hand in six degrees of freedom: where you
  look and where you spray are completely independent.

  Point the washer at something and press X to pick it up. That includes objects
  on the floor, which the game's own gaze targeting cannot reach from standing
  height.

  Tested on a Meta Quest 3 over Virtual Desktop. Other OpenXR headsets should
  work but have not been tried.

  This is a beta. It is playable, not finished. See KNOWN LIMITATIONS below.


INSTALL

  1. Make sure PowerWash Simulator 2 is CLOSED.

  2. Double-click  Install.cmd

  3. Start the game through Steam and put your headset on.

  That is the whole thing. Nothing to install beforehand, nothing to choose, and
  no administrator rights.

  The installer needs an internet connection: it fetches the mod loader, the
  runtime that loader needs, and three OpenXR files the game does not ship -
  about 75 MB in total. Each one is checked against a known fingerprint before
  it is put in place, and anything already correct is skipped. So if a download
  fails, just run Install.cmd again.

  Everything lands inside the game's own folder. Nothing is installed on your
  system, nothing goes into the registry, and Uninstall.cmd takes it all back
  out again.

  If Windows says the script cannot run: right-click Install.cmd, Properties,
  and tick "Unblock" at the bottom.

  If the installer cannot write to the game folder - which happens when Steam
  installed the game under Program Files - right-click Install.cmd and choose
  "Run as administrator".


PLAY

  Start the game through Steam as usual. Put the headset on. VR comes up on its
  own a few seconds after the game has settled, and the washer is already in
  your hand.

  Have your headset software running BEFORE you start the game.

  THE FIRST LAUNCH AFTER INSTALLING takes about half a minute with no sign of
  progress, and needs an internet connection. The mod loader prepares its
  support files once. It has not crashed. Let it finish.

  To quit, use the game's own menu. F6 used to put the game back on the monitor,
  but it is a development key and this build has those switched off - see
  FOR DEVELOPERS at the end.


CONTROLS AND SETTINGS

  COMFORT OPTIONS, all OFF by default:

    - Teleport instead of walking. Pushing the free hand's stick forward then
      teleports rather than walking, and the jump on A is switched off in
      that mode.
    - Snap turn instead of smooth, with a selectable angle: 15, 30, 45 or 60
      degrees.
    - Vignette while moving, strength continuously adjustable.

  A preset sets all three at once: Off, Gentle or Maximum.

  INDEPENDENTLY of those there is a target teleport for everyone: washer-hand
  stick up, aimed with the free hand. It never reaches higher than a jump, has
  no limit downwards, and gets you onto stairs and ledges whenever a walkable
  path leads up there. The range is the sprinting jump distance as measured in
  the game.

  IMMERSION MODE: hold the Menu button and release it to switch the game UI
  off and on again. An open menu stays visible, so you cannot lock yourself
  out. Hold rather than double-click, because Virtual Desktop already uses the
  double-click.

  Y stands in for Esc inside popups - journal, level summary, newspaper
  articles. X opens a popup, Y right above it closes it. With nothing to
  close, Y opens and closes the task list as before.

  The configuration tool also sets the colour of the pointer beams and the
  size of the teleport target.

  Double-click  Configurator.cmd

  It has the full control layout under "Open quick guide", and settings for
  which hand holds the washer, turn speed, menu size and distance, the
  alignment of the washer in your hand, the vibration while spraying and its
  strength, whether the two VR hands are shown, and whether the aiming laser
  is drawn.

  The button at the bottom starts the game through Steam, so the window can
  stay open beside it. It is greyed out until an installation has been found.

  Everything finer than that - per-nozzle vibration strength, the turbo
  rhythm, the gesture zones - sits in
  <game>\UserData\MelonPreferences.cfg as plain text. Close the game before
  editing it: the loader rewrites that file on exit and would overwrite your
  change.

  UPDATING FROM AN OLDER VERSION keeps your settings. Values you never
  touched but that still sit on an old default - menu size, snap angle, the
  washer's alignment in your hand, a few diagnostics that cost frame rate -
  are moved to today's defaults on the first start, once. Anything you set
  yourself stays as it is; the log lists both.

  The guide is also just a file you can open directly:
  configurator\QuickGuide.html


REMOVE

  Double-click  Uninstall.cmd

  It removes exactly what it installed, and only while those files are still the
  ones it wrote - anything you replaced yourself is left alone and reported.
  Your settings are kept unless you ask for them to go as well.

  If you already had the mod loader installed before this package, it is left
  where it is: it may be carrying other mods.


WHAT GETS INSTALLED, AND FROM WHERE

  All of it inside the game folder, nowhere else.

  MelonLoader v0.7.3      the mod loader that lets any of this run. Open
                          source, Apache-2.0, from its own GitHub releases.

  .NET 6.0.36 runtime     what MelonLoader runs on, unpacked into
                          <game>\dotnet. This is why you do not have to install
                          anything yourself: it sits in the game folder instead
                          of on your system. From Microsoft.

  Unity OpenXR 1.18.0     three files that give the game VR at all. Not shipped
                          inside this package, because they are Unity's files
                          under Unity's own terms; they are fetched from Unity's
                          public package registry, the same place the Unity
                          editor gets them from.

  WetReality.Pose.dll     the mod itself.
  WetReality.XRBoot.dll

  On the game's first launch afterwards MelonLoader fetches its own support
  files too. That is the half minute mentioned above.


KNOWN LIMITATIONS

  - FIXED: the bright stripes and patches on grass in the right eye only.
    The game's ground is built from up to nine surface layers, and in VR the
    extra ones beyond four were drawn wrongly in the second eye. The mod now
    keeps the four layers covering the most ground and blends each rarer one
    into the one closest in colour. Both eyes show the same picture; the
    price is that a few rare ground layers look like a similar one - for
    example the bright mowing stripes in the Home Base lawn are now ordinary
    cut grass. To switch it off, set TerrainLayerLimit = -1 in
    MelonPreferences.cfg.

  - The game's own arms and hands stay hidden, and now unconditionally. Its
    first-person mesh is one object holding BOTH hands, so showing only the
    hand that grips the washer was never possible from that mesh.

    That is no longer the limitation it was. The mod attaches the game's own
    two VR hand models to the controllers instead - one at the washer grip,
    one free for reaching - and they are what you see. The checkbox
    "Show VR hands" turns them off for anyone who would rather play without
    them; the game's own arm rig cannot come back either way, because it is
    one mesh across both arms and unusable in VR.

    The hiding covers DLC outfits and DLC washers as well. The geometry is recognised
    by where it hangs in the scene, not by its name, so a DLC that ships its
    models under new names is caught without an update. If arm or hand geometry
    still shows up somewhere, send Latest.log - it names every renderer under
    the washer with its full path and why it was kept or hidden.

  - Riding the scissor lift looks stepped, and the free hand shakes with it.
    Reported and reproduced. It is NOT the logging: during a measured ride the
    log wrote one to nine lines a second, and the only large burst in the whole
    run was the level load. The platform moves the player in steps and the view
    follows them; which side of the seam produces the step is measured in the
    next build rather than guessed at. Riding works and is safe.

  - VR cannot be stopped and restarted inside one session. F6, which put the
    game back on the monitor, is off in this build for that reason among
    others - restart the game instead.

  - No physical VR interactions: no belt, no picking bottles up by hand. The
    game's existing menus are used as they are.

  - Left-handed play is in the configurator and is NOT well tested. Two things
    are known and deliberate: the menu button stays on the left controller,
    because that button only exists there, and the shoulder and hip zones
    mirror to the left with the washer. Anything else that feels mirrored the
    wrong way round is a defect rather than a decision - please report it. A
    left-handed tester would help more here than any amount of code reading.

  - The monitor picture can show the title screen nested inside itself. The
    cause is measured and not the mirror switch: Unity sizes a screen-space
    canvas to the eye buffer and composites it into the window as well, so a
    smaller copy lands inside the bigger one. The headset view is unaffected.

  - In one session the pointer lost the menu tabs: they could only be
    switched with the grip buttons, while the rest of the menu handling kept
    working. A restart cleared it. Cause unknown - that session's log had
    already been rotated away. This build logs the names of the menu rows it
    skips, so if it happens again Latest.log is the whole diagnosis. Please
    report it.

  - A menu tile under the pointer lights up once and goes dark again, instead
    of staying lit. It stays clickable, and the game's own selection still
    works. Three ways to hold that highlight were measured and none of them
    holds it; the remaining one needs the game's own cursor input rebuilt.


IF SOMETHING GOES WRONG

  The log is the first place to look:
    <game folder>\MelonLoader\Latest.log

  VR did not start by itself:  start your headset software, then press F8.
  The controllers stopped:     press F2 twice.
  Menus are too big to read:   lower "Menu size" in the configurator.

  The game will not start at all - no window, nothing in the log:
    run Install.cmd again. It reports whether it can find the runtime, and
    puts it back if it cannot.

  When reporting a problem, Latest.log plus what you were doing is usually
  enough to find it.


FOR DEVELOPERS

  This build has the development machinery switched off: the per-frame logs, the
  F-key and keypad hotkeys, and the measuring reports. It is one preference, in
  <game folder>\UserData\MelonPreferences.cfg:

    DevMode = false

  Set it to true and the individual switches apply again as they always did -
  DevHotkeys, VerboseDiagnostics, MenuMissReport, AimMissReport and
  AimChainReport. DevMode only caps them; their own stored values are left
  untouched, so switching it back on restores whatever was set before.

  Both mods have their own entry, one under [WetReality.Pose] and one under
  [WetReality.XRBoot]. Set both.

  What stays live either way: F8 to start VR by hand, F2 to hand the washer to
  the controller, keypad 3 to recentre, and keypad + and . for the nozzle. Those
  are player controls, not tuning tools.

  The MelonLoader console window is separate. The installer hides it by setting

    [console]
    hide_console = true

  in UserData\Loader.cfg. Set it back to false to get the console while
  developing; the uninstaller reverts it either way.


================================================================================
  Unofficial. Not affiliated with FuturLab or Square Enix.

  MelonLoader is a separate open-source project (Apache-2.0). The .NET runtime
  is Microsoft's (MIT). Unity's OpenXR plugin is downloaded from Unity's own
  package registry at install time and is not part of this package. None of
  these are affiliated with this mod.
================================================================================
