# Projekt-Anweisungen



\## 🎯 Mod-Architektur \& Design-Prinzipien (MVP)

\- \*\*Minimal Viable Product (MVP):\*\* Keine physischen VR-Interaktionen (kein Gürtel, kein manuelles Greifen von Flaschen). Bestehende UI/Radialmenüs werden in 2D/Flat oder als World-Space-Canvas beibehalten.

\- \*\*6DOF Tracking (Wichtig):\*\* Keine Mausemulation für Motion Aim! Die Pose (Position + Rotation) des rechten Hand-Controllers muss direkt die Transformation der In-Game-Waschpistole überschreiben. Blickrichtung (HMD) und Schussrichtung (Controller) sind vollständig entkoppelt.

\- \*\*Eingabe-Routing:\*\* Quest-Controller-Inputs (Sticks, Trigger, Buttons) emulieren das native Xbox-Controller-Schema ("Modern") des Spiels, anstatt neue Input-Systeme zu registrieren.



\## 💻 Technischer Stack \& Targets

\- \*\*Target:\*\* Meta Quest 3 via OpenXR + Virtual Desktop (VDXR).

\- \*\*Framework:\*\* MelonLoader (IL2CPP, x64). UnityVRMod/BepInEx wird nur als temporärer Rendering-Proof-of-Concept genutzt, nicht als Codebase.



