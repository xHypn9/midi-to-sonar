# MIDI to Sonar

Windows WPF application that controls the **SteelSeries GG Sonar classic mixer**
with a MIDI controller through Sonar's local loopback HTTP API. It does not use
the Windows mixer, UI automation, code injection, game hooks or screen
coordinates.

## Download

Download the latest Windows installer from the
[GitHub Releases page](https://github.com/xHypn9/midi-to-sonar/releases/latest).

The installer is currently unsigned, so Windows SmartScreen may ask for manual
confirmation. Settings, MIDI mappings and logs remain local in
`%LOCALAPPDATA%\SonarMidiController` and are not included in the repository or
release.

## Verified environment

- Windows 11
- SteelSeries GG 115.0.0
- SteelSeries Sonar 1.98.0
- .NET 8
- Sonar classic mixer

The local verification on 2026-07-26 successfully read and changed:

| Channel | API identifier | Read | Volume write | Mute write |
|---|---|---:|---:|---:|
| Master | `master` | Yes | Yes | Yes |
| Game | `game` | Yes | Yes | Yes |
| Chat | `chatRender` | Yes | Yes | Yes |
| Media | `media` | Yes | Yes | Yes |
| Aux | `aux` | Yes | Yes | Yes |
| Mic | `chatCapture` | Yes | Yes | Yes |

The official Sonar mixer visibly reflected the values written through the API.

## Run the GUI

Open PowerShell in this folder:

```powershell
dotnet restore
dotnet build
dotnet run
```

The application reads the existing Sonar state before enabling any write handlers, so startup does not reset the mixer. MIDI fader input updates the GUI and widget immediately, while a protected 100 ms poll reflects changes made directly in SteelSeries GG without overlapping requests.

Closing the window with **X** keeps the controller running in the Windows notification area. Double-click the tray icon to reopen it. Right-click the icon and choose **Esci** to terminate the program.

Only one application instance can run. Launching it again restores the existing
window instead of opening a second background process.

The **Settings** window supports:

- starting the application with Windows for the current user;
- starting directly in the notification area;
- showing the semi-transparent always-on-top mixer widget;
- locking the widget in a click-through mode that does not take mouse focus;
- opening the local data folder.

The widget shows live Game, Chat, Media, Aux and Microphone levels, provides a
glowing read-only mute indicator for every channel, opens on the second display when possible,
and saves its last position and proportional size in the application settings.
The background and MUTE-button opacity can be adjusted independently without
fading the channel labels or fader indicators.

The widget is a normal standalone WPF desktop window. It does not inject into a
game, hook DirectX, inspect another process, read or write game memory, automate
mouse/keyboard input, or modify game files. Anti-cheat policies remain controlled
by each game publisher, so compatibility cannot be guaranteed for every title.

## CLI test

```powershell
dotnet run --project src/SonarController.Cli -- get
dotnet run --project src/SonarController.Cli -- game 50
dotnet run --project src/SonarController.Cli -- chat mute on
dotnet run --project src/SonarController.Cli -- chat mute off
```

The standalone PowerShell probe is also available:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\sonar-test.ps1 game 50
```

## MIDI

The app uses the stable `Melanchall.DryWetMidi` 8.0.3 package. No CC number is hardcoded.

1. Connect a MIDI controller.
2. Use the **VOL / Learn** row and move a fader to map volume.
3. Use the **MUTE / Learn** row and press/release a button to map mute.
4. The device, MIDI channel, message type and control number are saved locally.

Volume and mute mappings are independent. Volume learns Control Change messages.
Mute learns both Control Change and Note On/Off messages and automatically detects
Momentary or Toggle button behavior. No controller number is hardcoded.

The WORLDE EasyControl Plus was live-tested on 2026-08-05: a physical mute button
sent Note On value 127 and Note Off value 0, and toggled Sonar exactly once per press.

Mappings are stored at:

```text
%LOCALAPPDATA%\SonarMidiController\midi-mappings.json
```

Each channel has separate **Reset** buttons for volume and mute. Resetting one
mapping leaves the other one unchanged and saves the updated JSON atomically.

Application settings are stored at:

```text
%LOCALAPPDATA%\SonarMidiController\settings.json
```

MIDI values are clamped to `0..127`, normalized with `value / 127.0`, and duplicate consecutive values are suppressed. The MIDI service rescans every two seconds to handle disconnects and reconnects.

## Tests

```powershell
dotnet test tests\SonarController.Tests\SonarController.Tests.csproj
```

Tests cover MIDI normalization, channel identifiers, legacy JSON migration,
separate mappings, Control Change, Note On/Off, Momentary/Toggle behavior and
duplicate suppression.

## Installer

The WiX source and reproducible build script are in `installer`. The generated
64-bit MSI installs the self-contained application to:

```text
C:\Program Files\Midi2Sonar
```

It creates shortcuts in the Start menu and on the Desktop.

Build it with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

## Diagnostics

Application log:

```text
%LOCALAPPDATA%\SonarMidiController\logs\sonar-controller.log
```

No authentication secret or token is logged. Communication is rejected unless every discovered API address is loopback (`127.0.0.1`, `::1`, or `localhost`).

See [docs/SONAR_API.md](docs/SONAR_API.md) for discovery, endpoints, payloads, restart behavior, and test evidence.

## Important limitation

This is an unofficial integration with an internal, undocumented SteelSeries API. A future GG update can change it. Discovery is dynamic and survives port changes, but endpoint changes will require a code update.

SteelSeries, Sonar and SteelSeries GG are trademarks of their respective owner.
This project is not affiliated with or endorsed by SteelSeries.
