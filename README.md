# Forzavista Free Roam

Version 1.0.0

Standalone source for an external Free Roam Forzavista menu.

## Features

- Open and close individual supported panels.
- Open all, close all, and reset panel state.
- Toggle the roof where the current car supports it.
- Toggle full-detail car presentation.
- Bind global hotkeys to menu actions.

## Build from source

Requirements: Windows and the .NET 10 SDK.

```powershell
dotnet publish .\ForzavistaFreeRoam.csproj --configuration Release -p:PublishProfile=FolderProfile
```

This produces a self-contained, single-file Windows x64 executable at `publish\ForzavistaFreeRoam.exe`. The publish profile embeds the native WPF components required by the application.

## Use

Start the game, enter Free Roam with a car loaded, then start the application. The status area confirms when the current session is ready. Available panel controls depend on the selected car.

## Source ZIP contents

Include the project files, `assets` folder, `Properties\PublishProfiles\FolderProfile.pubxml`, `README.md`, `LICENSE`, and `.gitignore`. Do not include `.vs`, `bin`, `obj`, `publish`, or `*.pubxml.user`; they are generated locally when building, publishing, or debugging.

## License

The project source code is released under the [MIT License](LICENSE).
