# DT Playable Madelle (DarkAccomplice)

A [BepInEx](https://github.com/BepInEx/BepInEx) 5 plugin for **Deadly Trick** that adds a second Dark player, the **accomplice**, who plays as **Madeline**.

**Host-only:** only the player who hosts the lobby needs the mod. Everyone else joins with the normal, unmodified game.

## What it does

At the start of a round, besides the Mastermind, one random player becomes the accomplice (a second Dark). The accomplice:

- **Starts as Madeline** (the white-haired character that regular players cannot pick) and keeps that look after the round ends.
- Has a custom **freeze ability**: the target mark works like Louis' one, and the target is frozen like Seol's TimeStop (grey and unable to move) for a few seconds.
- Can **shapeshift** into any other player (skin and nickname) for a limited time, using a command typed into a terminal (see below).

Shapeshift ends by itself when the time is up, when a body is found, or when the round ends; the accomplice then goes back to Madeline with the real nickname.

## Requirements

- Deadly Trick on Steam (developed and tested on version 0.1.14).
- BepInEx **5.4.23.5, 64-bit** (Windows).
- At least 3 real players in the lobby (fewer players: no accomplice is picked, unless you enable the debug option below).

## Installation

1. **Install BepInEx** (skip if you already have it).
   - Download `BepInEx_win_x64_5.4.23.5.zip` from the [BepInEx release page](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).
   - Extract it into the game folder (`...\steamapps\common\Deadly Trick`) so that `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder sit next to `DeadlyTrick.exe`.
   - Start the game once from Steam and close it. BepInEx creates its folders, including `BepInEx\plugins`.
2. **Install the plugin.** Download `DarkAccomplice.dll` from the [`release`](release) folder of this repository and put it into `BepInEx\plugins`.
3. **Start the game and host a lobby.** BepInEx writes `DarkAccomplice 1.0.0 loaded` to its log (`BepInEx\LogOutput.log`).

To uninstall, delete `DarkAccomplice.dll` from `BepInEx\plugins`.

> Optional: [BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) lets you change the settings in game (F1).

## How to play the accomplice

1. When the round starts, about 5 seconds later the accomplice gets the list of players in a **terminal** (the in-game chat device). It looks like `Players: 1:Alice, 2:Bob, 3:Carol` and appears when you open any terminal. The numbers are the ones used by the command below.
2. **Shapeshift:** open a terminal and type `!N` in the **normal** (not secret) chat, where `N` is a player number from the list. The terminal answers `shapeshifted`, and everyone sees you as that player (skin and nickname). Typing your own number turns you back early.
   - Only the normal terminal chat works. The command is swallowed silently anywhere else (so nobody else sees it), and an invalid number does nothing.
   - The list of players is sent only once, automatically. There is no manual `!help`.
3. **Freeze:** use the ability button as with any other character ability. Walk up to a player until the target mark appears, then press it.

## Configuration

The config file is `BepInEx\config\com.oreny.darkaccomplice.cfg` (created after the first launch).

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Turn the mod on or off (only matters if you are the host) |
| Debug | `Host Is Accomplice` | `false` | The host always becomes the accomplice and starts as Madeline (for solo testing) |
| Freeze Ability | `Duration Seconds` | `10` | How long the target stays frozen |
| Freeze Ability | `Cooldown Seconds` | `45` | Ability cooldown |
| Transform | `Duration Seconds` | `30` | How long a shapeshift lasts |
| Transform | `Cooldown Seconds` | `15` | Pause before the next shapeshift (counted from the moment you turn back) |

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer) and a Deadly Trick installation with BepInEx already set up (the project references its libraries).

```
dotnet build -c Release -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick"
```

`GamePath` defaults to the standard Steam location. If the folder `BepInEx\plugins` exists there, the built DLL is copied into it automatically. The build output is `bin\Release\net472\DarkAccomplice.dll`.

## Notes

- The mod patches game code (Harmony), so a game update can break it. If the log shows `Patches failed to apply`, the mod needs an update.
- This is an unofficial fan mod and is not affiliated with the game's developers.
