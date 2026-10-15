# Discord Stream Auto-Mute verification

## Architecture and scope

The repository is a .NET 8 C# WPF application. `App.xaml.cs` coordinates the
single instance; `MainWindow.xaml.cs` owns the tray, MIDI router, Sonar controller,
100 ms non-overlapping dispatcher poll and widget. DryWetMidi 8.0.3 handles MIDI;
its input worker rescans devices every two seconds. The Core project discovers
Sonar's classic mixer HTTP endpoint through GG's local `coreProps.json` and
loopback `/subApps`. Settings and mappings are separate atomically saved JSON
files. `FileSonarLogger` serializes local log writes. Tests use xUnit; WiX 5
packages the self-contained win-x64 single-file publish. No CI workflow exists.
History at implementation: `9aff4cd` README cleanup, `bc574bf` public 1.3.5.

Integration adds three isolated services (native COM declarations, signed-process
identification, polling worker), one setting and a Settings toggle. MainWindow
starts/applies/disposes that worker independently of MIDI and Sonar HTTP. No
packages, mapping, device-selection or hotkey changes are needed. The worker
has no persistent session cache: disappearing sessions need no explicit eviction.
Only `ISimpleAudioVolume.SetMute(true)` is used, never endpoint volume or unmute.
The endpoint filter accepts exactly the requested short name or the full native
friendly name observed on this machine:
`SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)`.

Native references:
- [IAudioSessionControl2](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessioncontrol2)
- [ISimpleAudioVolume](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-isimpleaudiovolume)

## Preparation

1. Build and run this checkout, with no older controller instance running:
   `dotnet run -c Release`.
2. Enable **Settings → Discord Stream Auto-Mute → Save**.
3. Open `sndvol.exe`, select the playback endpoint
   **SteelSeries Sonar - Microphone**. Do not change the capture endpoint.
4. Record microphone device volume/mute, Discord input device, Sonar Game/Chat/
   Media/Aux levels/mutes and existing MIDI mappings before testing.
5. Observe `%LOCALAPPDATA%\SonarMidiController\logs\sonar-controller.log`.
   New mute operations should contain
   `[DiscordAutoMute] Muted Discord session PID ... on SteelSeries Sonar - Microphone`.
6. Use a second participant/device to independently check call voice and stream
   audio. A moving local meter alone does not prove correct received audio.

## Required manual tests (not claimed as completed by automated tests)

| Test | Action | Expected result |
| --- | --- | --- |
| A | Start controller and Discord, no stream; speak in a call. | Call microphone works, device volume/mute and other endpoints unchanged. |
| B | Share one application with audio. | Normal app audio and call voice; no unintended mute. |
| C | Share entire monitor, stream audio OFF. | Call voice works; no unrelated session/device changes. |
| D | Enable monitor stream audio. | Newly created Discord playback session on the exact target endpoint mutes within approximately 1 second plus inspection time; log records its PID. Voice remains audible in the call and is absent from stream audio. |
| E | Stop and restart monitor stream with audio, at least three times. | Every recreated matching session is muted; no unmute when stopping. An already-muted session causes no repeated mute log each second. |
| F | Restart Discord, then recreate stream with audio. | New PID is recognized and new target session muted. |
| G | Move mapped MIDI faders and press mapped mute buttons before/during/after stream. | Same mappings, input behavior, Sonar levels and widget feedback as baseline. |

Also test disabling the toggle while streaming (existing mute remains, no new
mutes), reenabling (unmuted matching sessions mute), and exiting from tray (no
unmute). Other apps on the target endpoint and Discord on Game/Chat/Media/Aux
must stay untouched. Restart Sonar and repeat D. Start with Discord/GG closed,
then open them; the controller must remain usable and recover automatically.
Try a manual unmute while enabled: the service intentionally mutes it again.

## Verification boundaries and risks

On 2026-10-16, the user installed MSI 1.3.6, enabled the Settings toggle and
saved, then manually unmuted the Discord microphone stream session. The service
immediately muted it again, confirming the installed build's automatic mute path.
The complete A-G checklist above remains a separate verification procedure.

Automated tests cover exact endpoint/process-name filtering, rejection of the
test-runner process, default/legacy/false configuration persistence, existing
MIDI/UI tests, and read-only native render endpoint/session COM queries on this
Windows machine. They do not prove Discord stream routing, remote received audio,
or physical MIDI behavior. A signature/publisher or endpoint-name change fails
closed. Signature trust uses the local Windows trust store and cached information.
Core Audio session boundaries are controlled by Discord: if voice and stream
share the same playback session, this feature cannot distinguish them. Disable
the toggle if tests reveal this; capture sessions are always excluded.
