# Forzavista Anywhere

Version 1.2.0

## Features

- Supports Steam and Microsoft Store/Xbox app versions.
- Open and close individual supported panels.
- Explode or implode supported doors, panels, active aero, and pop-up
  headlights together without staggered delays.
- Reset panel state.
- Toggle the roof where the current car supports it.
- Open and close supported active-aero surfaces.
- Open and close pop-up headlights on supported cars.
- Toggle full-detail car presentation.
- Bind keyboard shortcuts to menu actions. They are active only while the game
  is focused, so they do not interfere with other applications.
- Bind Xbox controller buttons or button combinations to menu actions.

General lighting, indicator, and hazard controls are not included in this
release.

## How to use

- Start the game.
- Enter Free Roam with a car loaded.
- Start `Forzavista.exe`.
- The status area confirms when the current session is ready.
- Available panel controls depend on the selected car.
- To create a binding, enable the keyboard or controller bind mode, click an
  action, then press the desired key or controller combination.
- If you enter Photo Mode, you may need to disable Max Detail and then enable
  it again.

## Known bugs

- Sometimes animations do not play correctly. Click `RESET STATE` and try
  again.
- Max Detail affects world LOD slightly.
- At night, the game's automatic lighting controller may reopen pop-up
  headlights after the tool closes them.

## Supported game builds

- Steam 6.430.771.0
- Steam 6.440.853.0
- Microsoft Store/Xbox app 3.440.853.0

Unknown game builds are rejected until their compatibility has been verified.

## Build from source

Requirements:

- Windows 10 or Windows 11 x64
- .NET 10 SDK

```powershell
dotnet publish .\ForzavistaFreeRoam.csproj --configuration Release -p:PublishProfile=FolderProfile
```

The self-contained executable is written to `publish\Forzavista.exe`.

## Credits

JXRDN for designing the UI and RE. GPT 5.6 for making probes and coding a
functional tool.

## Here is the tool

<img width="1260" height="1071" alt="Forzavista Anywhere" src="https://github.com/user-attachments/assets/689be984-60c3-4fba-85b4-1906d19a1e80" />

## Here are some screenshots of the tool in use

<img width="2160" height="1215" alt="Forza Horizon 6 9_5_2026 10_14_28 AM_compressed" src="https://github.com/user-attachments/assets/d0a00a61-5dc3-483b-ab85-6240a687e2a9" />

<img width="3840" height="2160" alt="Forza Horizon 6 9_7_2026 5_50_48 PM" src="https://github.com/user-attachments/assets/1807586d-c790-45b3-9b0d-ced8be216839" />

<img width="3840" height="2160" alt="Forza Horizon 6 9_2_2026 8_36_34 PM_compressed" src="https://github.com/user-attachments/assets/c3a8a905-08b9-4f4d-ba14-805b8228dad4" />

<img width="3840" height="2160" alt="Forza Horizon 6 9_5_2026 12_08_52 PM" src="https://github.com/user-attachments/assets/91ac4a17-ee5e-42bf-ae8c-1bdccc290bf3" />

<img width="3840" height="2160" alt="Forzavista Anywhere vehicle example" src="https://github.com/user-attachments/assets/48b19d0d-5e3c-451f-b164-63b926b7556f" />

See [CHANGELOG.md](CHANGELOG.md) for the complete release history.
