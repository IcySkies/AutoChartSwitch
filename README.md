# Auto Chart Switch

Auto Chart Switch is a Windows operator application for preparing an ordered chart queue and publishing chart metadata and media to OBS. It targets OBS 28 or newer with the built-in obs-websocket v5 server.

## Requirements

- Windows 10 or 11 x64
- .NET 8 Desktop Runtime to run published builds
- .NET 8 SDK to build from source
- OBS Studio 28+ with WebSocket server enabled

## OBS setup

Create eight distinct inputs before configuring the app:

| Mapping | Compatible OBS input | Updated setting |
| --- | --- | --- |
| Title | GDI+ or FreeType text | `text` |
| Artist | GDI+ or FreeType text | `text` |
| Illustrator / Charter | FreeType text | `text` |
| Difficulty Name | GDI+ or FreeType text | `text` |
| Difficulty Number | GDI+ or FreeType text | `text` |
| Jacket | Image | `file` |
| Difficulty Image | Image | `file` |
| Showcase Video | Media Source | `local_file` |

Open **OBS and source configuration**, enter the WebSocket URL/password, connect, and select every mapping. Set the difficulty image folder and the entry/exit scenes. The difficulty image for a chart named `Master` is loaded from `DifficultyCustomPath\Master.png`.

The separate **Auto Chart Switch Tech Stats** window is a transparent, draggable capture surface for OBS. It starts blank, then renders the current chart's CHIP, TECH, STREAM, CHORD, BURST, and optional GIMMICK values using the `vivid/stasis` layout. Capture this window directly in OBS; it replaces the former Stat Media source.

Auto-switch uses OBS's currently selected transition. On Pop it changes to the configured entry scene before modifying any mapped source, waits for the configured **Source update delay** (0 to 60000 ms), then updates sources and restarts the Showcase Video. It changes to the exit scene only after OBS reports natural playback completion. Showcase looping must be disabled.

## Queue workflow

- Insert records at the front or back, edit/delete them, and reorder by button or drag.
- Missing files are shown as queue warnings. They do not prevent queue preparation, but they block Pop.
- Pop updates OBS transactionally and removes the front record only after all acknowledgements succeed.
- Quick Edit changes the committed on-air record. Retry Sync republishes it without another entry-scene switch.
- The current display is deliberately blank after every application launch and is never sent to OBS automatically.

Queue and settings autosave under `%LOCALAPPDATA%\SVC-AS\AutoChartSwitch`. The OBS password is encrypted for the current Windows user with DPAPI. Chart lists can also be imported or exported as UTF-8 schema-versioned JSON.

## JSON format

```json
{
  "schemaVersion": 1,
  "charts": [
    {
      "id": "6d5bc343-9fae-4b9b-971e-8683c2dd83ec",
      "title": "Example Song",
      "artist": "Example Artist",
      "illustrator": "Example Illustrator",
      "charter": "Example Charter",
      "difficultyName": "Master",
      "difficultyNumber": 12.3,
      "jacketPath": "assets/jacket.png",
      "techStats": {
        "chip": 110,
        "tech": 95,
        "stream": 180,
        "chord": 72,
        "burst": 130,
        "gimmick": 0
      },
      "showcaseVideoPath": "assets/showcase.mp4"
    }
  ]
}
```

Relative imported paths resolve against the JSON file. Exported paths are absolute. Missing or empty IDs are replaced with new stable IDs.

## Build and test

```powershell
dotnet restore AutoChartSwitch.sln --disable-parallel
dotnet build AutoChartSwitch.sln --configuration Release --no-restore
dotnet test tests\AutoChartSwitch.Tests\AutoChartSwitch.Tests.csproj --configuration Release --no-restore
```

Run the app during development:

```powershell
dotnet run --project src\AutoChartSwitch.App\AutoChartSwitch.App.csproj
```

## Project layout

- `AutoChartSwitch.Core`: chart records, validation, queue, workflow, formatting, JSON, and persistence primitives
- `AutoChartSwitch.Obs`: OBS WebSocket transport, mapping validation, transactional publishing, rollback, and media lifecycle
- `AutoChartSwitch.App`: WPF operator UI, transparent `vivid/stasis` tech-stat capture surface, and Windows settings persistence
- `AutoChartSwitch.Tests`: formatting, validation, queue, interchange, workflow, and OBS contract tests
