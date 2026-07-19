# TVSync - Synchronized TV Playback for Lethal Company

![Downloads](https://img.shields.io/badge/Downloads-Thunderstore-blue) ![Version](https://img.shields.io/badge/Version-2.0.0-green) ![Game](https://img.shields.io/badge/Game-Lethal_Company-orange)

A Lethal Company mod that provides **perfectly synchronized** YouTube video playback on the ship television for all players in multiplayer. 

The experience feels like watching a movie together, every player sees the same frame and hears the same audio timestamp simultaneously. This is a complete networking rewrite and fork of the original YTTV / Television Controller mods, replacing their independent-client loading with a robust, host-authoritative synchronized system.

## Features

- **Perfect Sync (Host-Authoritative)** — The host controls everything; clients mirror the host's playback state.
- **Auto-Drift Correction** — Periodic sync broadcasts keep all clients within 150ms of the host (configurable). Small drifts are fixed by temporarily adjusting playback speed; large drifts trigger a hard seek.
- **Ready-Check System** — Playback does not start until *all* players have successfully downloaded and prepared the video.
- **Late Join Support {WIP}** — Players joining the lobby mid-video will automatically request the current playback state and seek to the correct timestamp to catch up.
- **Commands from anyone** — Any player can type commands, but only the host's game will authorize and orchestrate playback commands.
- **Client-Side Volume** — Each player controls their own volume independently.
- **Position Memory** — Optionally remembers the timestamp when toggling the TV off/on.
- **Multi-language** — English and Russian chat messages supported out of the box.

## Commands

| Command | Description |
|---------|-------------|
| `/tplay [URL]` | Play a YouTube video |
| `/ttime [TIME]` | Seek to a timestamp, e.g., `1:30` or `1:02:30` |
| `/treset` | Restart the current video from the beginning |
| `/tpause` | Pause synchronized playback |
| `/tresume` | Resume synchronized playback |
| `/tstop` | Stop playback entirely |
| `/tvolume [0-100]` | Set your local TV volume |
| `/tposition [true/false]` | Toggle position memory |
| `/thelp` | Show the help menu in-game |

## How the Sync Works

1. **Broadcast:** A player types `/tplay [URL]`, and the host broadcasts the URL to all clients via Unity Netcode (`CustomMessagingManager`).
2. **Download & Prepare:** Each client (and the host) downloads the video using `yt-dlp` and prepares it in Unity's `VideoPlayer`.
3. **Ready Check:** Each client sends a `ClientReady` message back to the host.
4. **Synchronized Start:** Once all clients are ready (or a 30s timeout expires), the host calculates a future `NetworkTime` and broadcasts a "start at time X" command. All clients begin playback at exactly the same network moment.
5. **Continuous Correction:** Every 3 seconds, the host broadcasts its current playback position. Clients calculate their expected position against the host's network time and apply drift correction if needed.

## Building from Source

To compile TVSync yourself, you will need the Lethal Company game assemblies.

1. Clone this repository
2. Create a folder named `References` in the root directory.
3. Copy the `Assembly-CSharp.dll` and `Unity.Netcode.Runtime.dll` (along with other standard Unity/Lethal Company DLLs) from your `Lethal Company/Lethal Company_Data/Managed/` folder into `References/`.
4. Open the project in Visual Studio or build via the .NET CLI:
   ```bash
   cd src
   dotnet build TVSync.csproj -c Release
   ```
5. The compiled plugin will be in `src/bin/Release/TVSync.dll`.

## Installation (for players)

If you just want to play the mod, please download it from **[Thunderstore](#)**.
1. Install BepInEx.
2. Drop `TVSync.dll` into your `BepInEx/plugins/` folder.
3. The mod will automatically download `yt-dlp` and create the required configuration files on its first run.

## Credits

- **Original Concept:** KoderTech (Television Controller)
- **Fork / Fixes:** TheByteNinja (Television Controller Fix)
- **Fork / Fixes:** MAR-Mods (YTTV)
- **Multiplayer Sync Rewrite:** TVSync (current)