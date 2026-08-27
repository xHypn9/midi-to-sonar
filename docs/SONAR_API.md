# SteelSeries GG Sonar local API research

## Scope and safety

Research was read-only for SteelSeries files and processes. The only SteelSeries state changes were normal local API calls that changed mixer volume/mute as explicitly requested.

Not used:

- Windows master volume or application mixer
- generic Windows audio endpoints
- UI automation as the control mechanism
- DLL injection, hooks, memory patching, or binary modification
- LAN listeners or remote services

Every runtime address is validated as loopback before a request is sent.

## Tested versions

- SteelSeries GG `115.0.0.0`
- SteelSeries Sonar `1.98.0.0`
- Windows 11
- Test date: 2026-07-26
- Sonar mode: `classic`

## How the API was found

1. `SteelSeriesSonar.exe` was running from:

   ```text
   C:\Program Files\SteelSeries\GG\apps\sonar\SteelSeriesSonar.exe
   ```

2. The Sonar process owned a dynamic TCP listener bound only to `127.0.0.1`.
3. GG's discovery file was found at:

   ```text
   %PROGRAMDATA%\SteelSeries\GG\coreProps.json
   ```

4. `ggEncryptedAddress` from that file identified GG's dynamic local HTTPS service.
5. `GET https://{ggEncryptedAddress}/subApps` returned Sonar state and `metadata.webServerAddress`.
6. `%PROGRAMDATA%\SteelSeries\GG\Logs\electron-log.txt` independently confirmed that GG registers a dynamic Sonar address and connects to `ws://{sonarAddress}/sock`.
7. The installed Electron `app.asar` contained the same `/volumeSettings`, `/mode`, `/chatMix`, and `/sock` route construction.
8. Open-source client code was used only to corroborate the discovered channel identifiers and route shapes:
   - <https://github.com/Mark7888/steelseries-sonar-rs>
   - <https://docs.rs/steelseries-sonar/latest/src/steelseries_sonar/sonar.rs.html>

No endpoint was invented.

## Dynamic discovery

### File

```text
%PROGRAMDATA%\SteelSeries\GG\coreProps.json
```

Relevant shape:

```json
{
  "ggEncryptedAddress": "127.0.0.1:{dynamic-gg-port}"
}
```

### GG request

```http
GET https://127.0.0.1:{dynamic-gg-port}/subApps
```

GG uses a local self-signed certificate. The client accepts certificate errors only when the request host is verified loopback.

Relevant response shape:

```json
{
  "subApps": {
    "sonar": {
      "isEnabled": true,
      "isReady": true,
      "isRunning": true,
      "metadata": {
        "webServerAddress": "http://127.0.0.1:{dynamic-sonar-port}"
      }
    }
  }
}
```

There is no hardcoded port. During local observation, Sonar used more than one port across starts, which confirms that rediscovery is required.

## Mode

```http
GET http://127.0.0.1:{sonar-port}/mode/
```

Observed response:

```json
"classic"
```

The current prototype intentionally refuses to write when mode is not `classic`; streamer mode has different volume paths and two output mixes.

## Read mixer state

```http
GET http://127.0.0.1:{sonar-port}/volumeSettings/classic
```

Observed response shape:

```json
{
  "masters": {
    "classic": {
      "volume": 0.8,
      "muted": false
    }
  },
  "devices": {
    "game": {
      "classic": {
        "volume": 0.8,
        "muted": false
      }
    },
    "chatRender": {
      "classic": {
        "volume": 0.3,
        "muted": false
      }
    },
    "media": {
      "classic": {
        "volume": 0.6,
        "muted": false
      }
    },
    "aux": {
      "classic": {
        "volume": 0.5,
        "muted": false
      }
    },
    "chatCapture": {
      "classic": {
        "volume": 0.4,
        "muted": false
      }
    }
  }
}
```

Volumes are floating-point values in `0.0..1.0`.

## Channel identifiers

| GUI label | Sonar identifier |
|---|---|
| Master | `master` |
| Game | `game` |
| Chat | `chatRender` |
| Media | `media` |
| Aux | `aux` |
| Mic | `chatCapture` |

## Set volume

```http
PUT http://127.0.0.1:{sonar-port}/volumeSettings/classic/{channel}/Volume/{value}
```

Examples:

```http
PUT /volumeSettings/classic/game/Volume/0.5
PUT /volumeSettings/classic/chatRender/Volume/0.3
PUT /volumeSettings/classic/chatCapture/Volume/0.4
```

No request body was required. A successful response returned JSON containing the updated master/device state.

### Master behavior

`master` is not independent of the playback faders:

- Reducing Master proportionally reduced Game, Chat, Media, and Aux.
- Mic was not scaled by playback Master.
- Raising a playback channel above the previous Master caused the reported Master to rise to the new maximum.

The app displays the state Sonar actually reports rather than attempting to emulate different semantics.

## Set mute

```http
PUT http://127.0.0.1:{sonar-port}/volumeSettings/classic/{channel}/Mute/{true|false}
```

Examples:

```http
PUT /volumeSettings/classic/game/Mute/true
PUT /volumeSettings/classic/game/Mute/false
```

No request body or authentication token was required in the tested version.

## WebSocket and bidirectional state

The installed client connects to:

```text
ws://127.0.0.1:{sonar-port}/sock
```

Messages use JSON with an `event` and `data` field. A controlled test connected to the socket, changed Game volume through the HTTP API, and received no volume notification. Therefore this prototype does not claim event-driven volume synchronization.

The GUI polls `GET /volumeSettings/classic` every 400 ms. This was verified to:

- initialize sliders from existing Sonar state before writes are enabled;
- reflect changes made directly in SteelSeries GG;
- reflect API/MIDI changes;
- rediscover after a stale address or GG/Sonar restart.

The official Sonar mixer visibly moved to the values set by the external API during the local test.

## Restart and failure behavior

The controller:

1. reads `coreProps.json`;
2. queries `/subApps`;
3. validates `isEnabled`, `isRunning`, and `isReady`;
4. validates the returned address as loopback;
5. checks `mode/`;
6. caches the current Sonar address;
7. invalidates it and performs discovery again after a failed request.

The GUI reports `Sonar: DISCONNECTED` while GG is closed, Sonar is disabled/not ready, or the API is unavailable. Polling continues so it reconnects automatically.

## Authentication

No Sonar token or authorization header was present or required on GG 115 / Sonar 1.98. The program contains no machine-specific address, port, identifier, or secret. If a future release introduces a token, it must be discovered at runtime and must never be logged.

## Live test evidence

Every write below was immediately followed by a fresh `GET /volumeSettings/classic`:

| Operation | Verified result |
|---|---|
| Game 20% | Game = 20%, not muted |
| Game 80% | Game = 80%, not muted |
| Chat 30% | Chat = 30%, not muted |
| Media 60% | Media = 60%, not muted |
| Aux 50% | Aux = 50%, not muted |
| Mic 40% | Mic = 40%, not muted |
| Master 75% | Playback channels scaled proportionally; Mic unchanged |
| Master/Game/Chat/Media/Aux/Mic mute on | Correct channel reported muted |
| Same six channels mute off | Correct channel reported active |
| GUI Game 80% -> 81% | API read Game = 81% and Master = 81% |
| API Chat 30% -> 31% | WPF value updated to 31% through polling |
| GUI Game MUTE | API read Game = muted; state was then restored |

Final visible/read state after restoring mutes and setting playback channels:

```text
MASTER 80.0% ACTIVE
GAME   80.0% ACTIVE
CHAT   30.0% ACTIVE
MEDIA  60.0% ACTIVE
AUX    50.0% ACTIVE
MIC    40.0% ACTIVE
```
