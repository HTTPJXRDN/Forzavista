# Forzavista Anywhere

Version 1.3.0

Forzavista Anywhere is an external Windows menu for using supported vehicle
presentation controls in Forza Horizon 6 Free Roam.

## Features

- Supports the verified Steam and Microsoft Store/Xbox app builds listed below.
- Open or close supported doors, hood, trunk, storage, vents, active aero, and
  pop-up headlights. Each part has one toggle button.
- Use one EXPLODE/IMPLODE button to operate supported panels, active aero, and
  pop-up headlights together.
- Hide or restore individual side windows on supported cars in Free Roam. The
  tool affects both exterior and interior glass where those layers are found.
- Toggle a supported roof, reset panel state, and enable Max Detail.
- Bind a keyboard key or Xbox controller button combination to a toggle.
  Reusing one binding for several parts operates them together. Shortcuts work
  only while the game is focused.
- Use the two-column menu to find panels and window/dynamic controls quickly.

Side-window controls are available on Steam 6.440.853.0 and Microsoft
Store/Xbox app 3.440.853.0. They are not available on Steam 6.430.771.0.
Windows are a Free Roam feature; garage/Forzavista window hiding is not
supported. General headlight, taillight, indicator, hazard, and arbitrary
body-part/modelbin hiding controls are not included in this release.

## How to use

1. Start the game and enter Free Roam with a car loaded.
2. Start `Forzavista_v1.3.0.exe` from the release download. A locally built
   executable is named `Forzavista.exe`.
3. Wait for the status area to say the current session is ready.
4. Click a supported part to alternate between OPEN and CLOSE. The available
   controls depend on the car.

To bind a control, turn on **SET BINDING**, click an action, then press a
keyboard key or Xbox controller combination. You can give multiple actions
the same binding to operate them together. Right-click an action while SET
BINDING is on to clear its bindings; **CLEAR ALL BINDINGS** clears everything.
EXPLODE/IMPLODE also uses a single binding. Existing EXPLODE bindings are
carried over to it; an old IMPLODE binding is used if EXPLODE was unbound.

On cars without rear side windows, a shared four-window binding skips the
missing positions, so it can still hide and restore the front pair. To turn
window controls off for a session, set `FORZAVISTA_DISABLE_WINDOWS=1` before
launching the menu.

## Supported game builds

- Steam 6.430.771.0 (panels and other established controls; no windows)
- Steam 6.440.853.0
- Microsoft Store/Xbox app 3.440.853.0

Unknown game builds are rejected until their compatibility has been verified.

## Known issues

- Garage/Forzavista window hiding is not supported.
- Some cars lack individual panels or glass layers. If an animation does not
  play correctly, click **RESET STATE** and try again.
- Max Detail can lower world detail as well as changing the car. If Photo Mode
  changes the car detail state, turn Max Detail off and on again.
- At night, the game's automatic lighting may reopen pop-up headlights after
  the menu closes them.

## Screenshots

![Mazda RX-7 with doors, hood, and pop-up headlights open](assets/photos/Forza%20Horizon%206%209_25_2026%204_59_38%20PM.jpg)

![Mazda RX-7 with its side window hidden](assets/photos/Forza%20Horizon%206%209_25_2026%205_00_11%20PM.jpg)

![Mazda RX-7 with pop-up headlights open](assets/photos/Forza%20Horizon%206%209_25_2026%205_00_34%20PM.jpg)

![Mazda RX-7 with hood open](assets/photos/Forza%20Horizon%206%209_25_2026%205_01_11%20PM.jpg)

![Nissan Silvia with pop-up headlights open](assets/photos/Forza%20Horizon%206%209_25_2026%205_02_41%20PM.jpg)

![Nissan Silvia with panels and pop-up headlights open](assets/photos/Forza%20Horizon%206%209_25_2026%205_03_02%20PM.jpg)

![Nissan Silvia with pop-up headlights open, front view](assets/photos/Forza%20Horizon%206%209_25_2026%205_03_45%20PM.jpg)

## Credits

JXRDN for UI design and reverse engineering, with GPT-5.6 assistance on probes
and implementation.

See [CHANGELOG.md](CHANGELOG.md) for release history.
