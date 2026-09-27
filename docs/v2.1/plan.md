# GiftDeck v2.1: Game Packs (StreamToEarn-style game integrations)

Goal: what streamtoearn.io offers, inside GiftDeck (free, open source): a **Games** page where a
streamer picks a supported game, presses **Install**, and GiftDeck finds the game, downloads and
installs the mods, configures them, and loads a ready-made **preset** (events + gift board) that
fires in-game actions from TikTok gifts. Plus the stream tools around it (Gift Spinner, alerts and
interrupts, overlay templates).

Branch `v2.1` (from `v2` at 24f81dd). Each work package below is built in its own worktree and
merged back into `v2.1`. Keep to the files each package owns; shared files (Models.cs, Hub.cs,
RulesEngine.cs, ActionEditor, MainWindow) only get the small hooks named here.

## What StreamToEarn does (reference, from its public site, 2026-09-27)

- 66 games; each has a mod + ready-made presets ("if this gift, do this in-game" rules).
- GTA V: app preset "GTA5: legacy" -> Install Mod; the app finds the game folder itself. BattlEye must
  be off in the Rockstar launcher. Story Mode only (Online = ban risk). Uninstall from the preset.
  In game: green greeting text; **F10** opens the mod menu (arrows, Enter, Backspace).
  **530 triggers** in categories Player, Peds, Vehicle, Misc, Time, Weather, Screen, Meta, Lua, Arena,
  Parkour, Vehicles. Most are Chaos Mod V effects by name; extras include Spawn Vehicle (any model),
  Spawn ramp, Jail Player, Give Weapon, Set ammo, Set time, Set weather, wanted level +/-, Add/Set
  money, Arm/Remove spawned attackers, Skydive, Teleport to location, Random clothing, vehicle tuning,
  Chiliad timer, Monkey killers, Moto cops/bandits.
- Minecraft: local PaperMC server installed from the app (JDK, PaperMC 1.20.1, EULA, plugins), game
  commands (summon, weather, gamerules), mini-game plugins.
- Stream tools: presets (Pro 50 / Expert 100 events: GiftDeck has no limit), **alerts & interrupts**
  (a paid interrupt pauses the current preset, plays a sound and shows an alert video, e.g. a
  jumpscare WebM), **3 overlay templates** generated per preset for TikTok LIVE Studio, **Gift Spinner**
  overlay (random in-game event from a weighted pool, five rarity tiers, multi-spinner groups),
  chat TTS, TikTok gift catalog. Kick support (later).

## Architecture

### 1. GameLink (GiftDeck <-> game mods)

`Services/GameLink/`. GiftDeck keeps a registry of **game targets** (`IGameTarget`), each with a
command catalog. A rule action `ActionType.GameCommand` runs a command on a target.

Two kinds of target:
- **Mods that connect to GiftDeck** over a local WebSocket: `ws://127.0.0.1:21216/` (localhost only).
- **Built-in targets** GiftDeck talks to itself (Minecraft over RCON).

WebSocket protocol, JSON text frames, one object per frame:

```
mod -> GiftDeck   {"type":"hello","game":"gta5","mod":"chaosmod","name":"Chaos Mod V","version":"2.2.0",
                   "commands":[{"id":"player_kickflip","name":"Kickflip","category":"Player",
                                "description":"","args":[]}]}
                  (args: [{"name":"model","label":"Vehicle model","type":"text|number|choice",
                           "default":"adder","choices":["adder","rhino"]}])
GiftDeck -> mod   {"type":"trigger","id":"<guid>","command":"player_kickflip","args":{"model":"adder"},
                   "user":"nickname","gift":"Rose","count":1}
mod -> GiftDeck   {"type":"result","id":"<guid>","ok":true,"message":""}
mod -> GiftDeck   {"type":"status","text":"In Story Mode"}          (optional, shown on the Games page)
either            {"type":"ping"} / {"type":"pong"}                   (keep-alive, every 15 s)
```

Target id = `game` + ":" + `mod` (e.g. `gta5:chaosmod`, `gta5:giftdeck`). Several mods for one game
can be connected at once; the catalog shown for a game merges them. A pack also ships a static
`commands.json` (same shape as `hello.commands`) so events can be set up while the game is closed.

`RuleAction` for a game command: `Type = GameCommand`, `Text` = target id, `Text2` = command id,
`Args` = JSON object of argument values (templates like `{user}` allowed in text args).

### 2. Game Packs (install and presets)

`Services/GamePacks/`, `Views/GamesView`. A pack is a folder `Packs/<id>/` embedded in GiftDeck
(copied to output), later also updatable from GitHub raw:

```
Packs/gta5/pack.json
Packs/gta5/cover.png
Packs/gta5/commands.json          (static catalog, optional)
Packs/gta5/presets/*.giftdeck     (profile export: rules.json, overlays.json, files/)
```

`pack.json`:

```json
{
  "id": "gta5", "name": "GTA V (Legacy)", "description": "...", "cover": "cover.png",
  "detect": [ {"type":"steam","appId":271590},
              {"type":"registry","key":"HKLM\\SOFTWARE\\WOW6432Node\\Rockstar Games\\Grand Theft Auto V","value":"InstallFolder"},
              {"type":"epic","appName":"9d2d0eb64d5c44529cece33fe2a46482"} ],
  "exe": "GTA5.exe",
  "mustBeClosed": ["GTA5.exe", "PlayGTAV.exe"],
  "requirements": ["Turn off BattlEye: Rockstar Games Launcher > Grand Theft Auto V Legacy > Settings", "Story Mode only"],
  "components": [
    {"id":"scripthookv","name":"Script Hook V","license":"Free, by Alexander Blade (not redistributable: downloaded from dev-c.com)",
     "source":{"type":"url","url":"http://www.dev-c.com/files/ScriptHookV_1.0.3788.0.zip","pageUrl":"http://www.dev-c.com/gtav/scripthookv/"},
     "extract":"zip","files":[{"from":"bin/ScriptHookV.dll","to":"ScriptHookV.dll"},{"from":"bin/dinput8.dll","to":"dinput8.dll"}],
     "detectInstalled":"ScriptHookV.dll"},
    {"id":"chaosmod","name":"Chaos Mod V (GiftDeck build)", "source":{"type":"github","repo":"Obiwayne/ChaosModV-GiftDeck","asset":"\\.zip$"}, "extract":"zip","files":[{"from":"*","to":""}]}
  ],
  "edits": [ {"file":"chaosmod/configs/config.json","type":"json","path":"NewEffectSpawnTime","value":99999} ],
  "presets": ["presets/doomsday.giftdeck"],
  "commands": "commands.json",
  "targets": ["gta5:chaosmod","gta5:giftdeck"]
}
```

Install = check game found + closed -> download each component (GitHub releases/latest asset, or url;
sha256 when given) -> extract -> **back up every file it will overwrite** to
`%APPDATA%\GiftDeck\packs\<id>\backup\<stamp>\` -> copy -> apply edits (json path / ini key) ->
write `%APPDATA%\GiftDeck\packs\<id>\installed.json` (versions, files written, backups) ->
optionally import a preset as a new profile. Uninstall = delete written files, restore backups.
Game detection: Steam (`libraryfolders.vdf` + `appmanifest_<appId>.acf`), Epic
(`%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`), registry, then "Choose folder...".

### 3. GTA V mods

- **Chaos Mod V, GiftDeck build** (fork of gta-chaos-mod/ChaosModV, GPL-3): a GameLink client in the
  mod: on connect sends `hello` with **every registered effect** (id, name, category), runs a
  `trigger` by effect id immediately (no voting, no timer), answers `result`. Separate repo
  `C:\Users\wayne\ChaosModV-GiftDeck` (GPL stays in that repo; GiftDeck only downloads its release).
- **GiftDeck GTA script** (ScriptHookVDotNet 3, net48, `C:\Users\wayne\GiftDeck\mods\gta5\GiftDeckGTA\`,
  MIT): GameLink client `gta5:giftdeck` with the non-Chaos triggers (Spawn Vehicle by model, Spawn
  ramp, Jail Player, Give Weapon, ammo, time, weather, wanted +/-, money, attackers, skydive, teleports,
  random clothing, tuning, ...), green greeting text on load, **F10 menu** (status, connected, test
  any command, pause triggers). Builds with the SHVDN 3 reference assembly.

### 4. Stream tools

- **Gift Spinner**: overlay page `/overlay/spinner` + config (pools of actions with weights, 5 rarity
  tiers: Common, Uncommon, Rare, Epic, Legendary; several spinner groups). New action
  `ActionType.SpinWheel` (`Text` = spinner id): spins on stream, then runs the chosen entry's actions.
- **Alerts & interrupts**: `ActionType.Alert` (`Text` = alert id): shows an alert on the alerts overlay
  (image/GIF/WebM video, sound, text with `{user}`), optional **interrupt**: pauses the rules queue
  while it plays.
- **Overlay templates per profile**: the existing gift board plus two more (a vertical "gift list"
  and a compact "top 3 + next goal" strip), each with a copyable URL for TikTok LIVE Studio / OBS.

### 5. Minecraft pack

`Packs/minecraft/`, `Services/GameLink/MinecraftTarget.cs`: one-click local Paper server (Java 21
check or Temurin download, Paper download API for the chosen version, EULA accept with the user's
consent, server.properties with RCON on 127.0.0.1 and a random password), start/stop from GiftDeck,
an RCON client, a command catalog (summon mobs, TNT, weather, time, give items, effects, gamerules)
with args, and a preset.

**Server packs.** A pack with a `"server"` object in pack.json sets up a local server instead of copying
files into a game. It has no `detect`/`components`/`edits`; the Games page shows the pack's server panel
in place of Install/Uninstall (Minecraft: `new Views.MinecraftServerPanel()`, which controls `Hub.Minecraft`):

```json
"server": {"type":"paper", "target":"minecraft:server", "panel":"MinecraftServerPanel",
           "defaultVersion":"latest", "eulaUrl":"https://aka.ms/MinecraftEULA",
           "join":"In Minecraft: Multiplayer > Direct Connection > localhost"}
```

Paper comes from PaperMC's Fill API v3 (`https://fill.papermc.io/v3/projects/paper`; the old
`api.papermc.io/v2` answers 410 Gone), sha256-checked; the API also says which Java each version needs
(1.20.x: 17, 1.21.x: 21, 26.x: 25). Java: GiftDeck uses the oldest installed Java that is new enough (its
own downloads, JAVA_HOME, PATH, Program Files vendors, the Minecraft Launcher's runtimes), or downloads an
Eclipse Temurin JRE (Adoptium API) into `%LOCALAPPDATA%\GiftDeck\java\` after asking. Servers live in
`%LOCALAPPDATA%\GiftDeck\minecraft\<name>\`; settings (player, ports, random RCON password) in
`minecraft.json` in GiftDeck's data folder. `eula.txt` is only written when the user ticks the box.
server.properties: `enable-rcon=true`, `server-ip=127.0.0.1` (game and RCON only on this PC, so no
firewall prompt; "Let other PCs on my network join" clears it), `broadcast-rcon-to-ops=false`,
`online-mode` left true.

Minecraft's commands.json adds fields the other readers ignore: `run` (a server command or a list),
`repeat` (the number arg that says how many times) + `maxRepeat`, `warning`, and
`legacy: [{"before":"1.21.11","run":...,"values":{"keep_inventory":"keepInventory"}}]` for older servers.
Template tokens: `{player}`, `{arg}`, `{arg:text}` / `{arg:nbttext}` (quoted, escaped text components;
nbttext is the pre-1.21.5 JSON-in-SNBT form), `{rand:-4:4}`, `{?arg}...{/arg}` (only when arg isn't empty).

## Shared hooks (already in the skeleton commit)

- `ActionType.GameCommand`, `ActionType.SpinWheel`, `ActionType.Alert`; `RuleAction.Args` (JSON string).
- `Hub.GameLink` (`GameLinkService`), `Hub.Packs` (`GamePackService`).
- `RulesEngine.RunAction` calls `Hub.GameLink.RunAsync(...)` / `Hub.Spinners.SpinAsync` / `Hub.Alerts.ShowAsync`.
- Nav item `games` -> `Views/GamesView`.

## Rules for everyone

- Match the surrounding code (plain words in UI text, comments that say why, no new NuGet packages
  unless unavoidable; say so if you add one).
- Never touch the user's real game folder, OBS files or `%APPDATA%\GiftDeck*` data in tests: use a
  scratch folder as a fake game folder and `GIFTDECK_DATA` pointing at a scratch data folder.
- Never send fake chat/gift events to a running GiftDeck (TTS reads chat aloud; gifts press keys).
- Don't launch or restart GiftDeck/OBS/GTA: the user is using them. Build with
  `dotnet build -c Debug -o <scratch>` so the running app's files aren't locked.
- Commit on your worktree branch with clear messages; don't push.
