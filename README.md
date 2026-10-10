# Forzavista Anywhere

**Version 1.3.1.1** · Windows x64 · Forza Horizon 6

An external vehicle-control menu for Free Roam, with panel and window controls,
DRL colors, smooth color cycles, strobe, fade timing, and reusable lighting presets.

Version 1.3.1.1 fixes side-window controls on Microsoft Store / Xbox app
3.461.691.0 by using the detected platform mapping for window updates and
restoration. RESET STATE also restores tracked side windows. Existing settings and
lighting features are retained.

## Download and start

1. Download **Forzavista_v1.3.1.1.exe** from this repository's Releases page.
2. Load a car in Free Roam, then start the executable and wait for the ready status.
3. Use the available controls. The version appears at the top right of the title bar.

The release is one self-contained executable. You do not need Visual Studio,
the .NET SDK, or a separate `bin` folder.

## Vehicle controls

- Toggle supported doors, hood, trunk, storage, vents, active aero, and pop-up headlights.
- Use **EXPLODE / IMPLODE** to operate supported panels together.
- Hide or restore individual side windows in Free Roam, where the car provides them.
- Toggle supported roofs, use **RESET STATE**, and enable **MAX DETAIL**.
- Assign keyboard keys or Xbox controller combinations. Shared bindings operate
  several controls together; shortcuts activate only while the game is focused.

For bindings, enable **SET BINDING**, click an action, and press the desired key
or controller combination. Right-click an action in binding mode to clear it.
**CLEAR ALL BINDINGS** clears the full set.

## Lighting

| Control | What it does |
| --- | --- |
| FRONT DRL / REAR DRL / HEADLIGHTS / ALL LIGHTS | Switch supported lamp groups on or off. |
| COLOR PICKER | Choose a fixed DRL color using quick colors, the chart, RGB values, or HEX. **Original** restores the captured stock color. |
| RGB / CYCLE | Fade smoothly through a rainbow, or through your own ordered list of 2–12 colors. Adjust the full-loop speed from 1–12 seconds. |
| BRIGHTNESS | Adjust DRL emission from 0–400%. Lower values can preserve saturated colors on very bright lamps. |
| STROBE | Use the game's native strobe timing, or uncheck **Native** for custom speed from 1–20 flashes per second. |
| FADE ON / OFF | Fade the light switches over 0.1–5 seconds. Press again during a fade to reverse it. |
| LIGHTING PRESETS | Save and explicitly apply named combinations of color/cycle, brightness, speed, strobe, and fade settings. |

Expand **DRL EFFECT SETTINGS** for cycle editing and speed controls. Custom
cycles blend each stop into the next, including the return to the first color.
Presets are shared across cars and apply only when you choose **APPLY SELECTED**.
Opening the tool or switching cars does not automatically apply a lighting effect.

Keep the engine running for headlight/rear-light control and native strobe.
Turn **FRONT DRL** on to see DRL effects. Native timing uses the game's timing;
the speed slider applies to custom strobe. Headlight and rear-light colors are
not customizable in this release. Hazards and turn signals are deferred.

The DRL catalog contains **292 candidate vehicles and 944 material entries**.
It includes multiple shader families, inherited colors, the M2 FE, and the PG bus.
**46 vehicles have user-confirmed color tests**; the entire catalog has not been
visually tested. Availability depends on the loaded car's validated materials.

## Game compatibility

| Game build | Vehicle controls | New lighting features |
| --- | --- | --- |
| Steam 6.461.691.0 | Mapped | Mapped |
| Microsoft Store / Xbox app 3.461.691.0 | Mapped | Mapped |
| Steam 6.440.853.0 | Retained | Not mapped |
| Microsoft Store / Xbox app 3.440.853.0 | Retained | Not mapped |
| Steam 6.430.771.0 | Retained; no window controls | Not mapped |

Xbox app support means the Windows PC version, not an Xbox console. Unknown
game builds are rejected. Xbox E60 M5 fixed color, RGB, and native strobe were
visually confirmed, with stock colors restored after every test. Steam lighting
has been checked across the user-confirmed cars above.

## Notes

- Window hiding is a Free Roam feature; garage/Forzavista window hiding is unsupported.
- DRL colors can work in the garage when its loaded materials validate. A car or
  scene change stops the old effect; choose the effect again for the new scene.
- Some cars have additional lamp layers or tinted lenses. A validated color
  route does not guarantee identical color or brightness on every car.
- Maximum brightness can wash colors toward white. Daytime visibility varies by car.
- If a part animation behaves unexpectedly, use **RESET STATE**. At night, the
  game's automatic lights may reopen pop-up headlights.
- Max Detail can affect world detail too. Toggle it again after Photo Mode if needed.

Bindings, lighting preferences, and presets normally live in
`%APPDATA%\ForzavistaFreeRoam`. Presets use `lighting-presets.json` and keep a
previous-file `.bak` on save. Existing choices are remembered without starting
effects automatically.

## Crash diagnostics

This hotfix records local action and shutdown logs in **Diagnostics**, beside
the executable. It records car/process identity, render-mode requests, native
call results, and each cleanup step. It does not log every animation frame.
Each session keeps an active log and, if needed, one previous 8 MB segment.
Nothing is uploaded automatically. If the executable's folder is read-only,
logging can fail without preventing the menu from running.

After a game crash, optionally run **Collect-Crash-Diagnostics.ps1** beside the
EXE to collect recent session logs, Windows Forza crash events, and the latest
existing Windows crash dump. It only reads existing records. Download the
script as an optional release asset; it is not needed to run the tool.

`FORZAVISTA_SESSION_LOG_DIR` can select an absolute log directory;
`FORZAVISTA_DISABLE_SESSION_LOG=1` disables session logging.

The hotfix prevents freeing native-call code after an uncertain thread wait,
and binds presentation cleanup to the car/process that received the change.
The intermittent zero-count vehicle-table game crash remains under
investigation; these guards have not been established as its complete fix.

## Screenshots

<img src="assets/photos/Forza%20Horizon%206%2010_7_2026%209_00_48%20PM.jpg" alt="BMW with cyan DRL rings and front doors open" width="820">

<img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_54_09%20PM.jpg" alt="Audi RS6 with pink front DRLs" width="820">

<details>
<summary>More colors and vehicle controls</summary>

<table><tr><td><img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_52_45%20PM.jpg" alt="Red DRLs" width="410"><br>Red DRLs</td><td><img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_52_52%20PM.jpg" alt="Blue DRLs" width="410"><br>Blue DRLs</td></tr></table>

<table><tr><td><img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_53_01%20PM.jpg" alt="Green DRLs" width="410"><br>Green DRLs</td><td><img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_53_16%20PM.jpg" alt="Pink DRLs" width="410"><br>Pink DRLs</td></tr></table>

<table><tr><td><img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_53_26%20PM.jpg" alt="Rear lamps switched off" width="410"><br>Rear lamps switched off</td><td><img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_55_43%20PM.jpg" alt="Lamborghini with blue DRLs and open doors" width="410"><br>Lamborghini with blue DRLs and open doors</td></tr></table>

<img src="assets/photos/Forza%20Horizon%206%2010_7_2026%208_56_13%20PM.jpg" alt="Lamborghini with doors and engine cover open" width="820">

<table><tr><td><img src="assets/photos/Forza%20Horizon%206%209_22_2026%204_26_47%20PM.jpg" alt="Aston Martin with a door open" width="410"><br>Aston Martin with a door open</td><td><img src="assets/photos/Forza%20Horizon%206%209_22_2026%209_36_13%20PM.jpg" alt="Audi with all doors and hood open" width="410"><br>Audi with all doors and hood open</td></tr></table>

</details>

## Build from source

Install the .NET 10 SDK with Windows desktop support, then run:

```powershell
dotnet build .\ForzavistaFreeRoam.csproj -c Release
dotnet publish .\ForzavistaFreeRoam.csproj -c Release -p:PublishProfile=FolderProfile
```

The publish profile creates a self-contained Windows x64 executable.

## Credits

JXRDN for UI design and reverse engineering, with AI assistance on probes and implementation.

See [CHANGELOG.md](CHANGELOG.md) for release history.
