# Minecraft mini-games: GiftDeck Games (Paper plugin)

StreamToEarn sells streamer-vs-viewers Minecraft mini-game plugins (free and PRO). GiftDeck has its own,
free and open source (MIT): **GiftDeck Games**, a Paper plugin in `mods/minecraft/GiftDeckGames/`. Every
game is started, stopped and fed gifts through server commands, so GiftDeck only sends RCON commands; the
plugin has no connection of its own to GiftDeck.

## The games

All three build their arena in its own chunk far from spawn and high in the sky (default x 20000, z 0,
floor at y 200: Bedrock Box at x 20000, Sand Pour at 20032, Sheep Out at 20064), so nothing near the
streamer's base is touched. Starting a game saves the player's place, inventory, XP, game mode and health to
`plugins/GiftDeckGames/players/<uuid>.yml`, clears the inventory and hands out the game's kit. When the game
is decided the player gets a title (YOU WIN / VIEWERS WIN), and 5 seconds later (or at the respawn after
dying) everything is put back. Stop clears the arena (blocks, mobs, TNT, falling blocks, items) and gives
everything back. A save still on disk (crash, server stopped mid-game) is given back at the next join.

Only one game runs at a time: starting one stops the other. Each game shows a boss bar (progress, time
left, the latest gift: "Bob dropped 3 TNT into the box!") and a sidebar ranking the viewers by what they
sent. Mobs, TNT and sheep carry the viewer's name.

| Game | The streamer | Viewers hurt with | Viewers help with | Win / lose |
|---|---|---|---|---|
| **Bedrock Box** (`bedrockbox`) | stands on a 5x5 shaft of 30 layers (dirt, stone, deepslate, ores, one obsidian layer near the bottom) inside bedrock walls, with a stone pickaxe | `tnt`, `sand` (sand / red sand / gravel refills the shaft), `anvil`, `mob` | `pickaxe` (diamond or better cuts the obsidian), `haste`, `drill`, `heal` | win: reach the emerald floor; lose: die |
| **Sand Pour** (`sandpour`) | stands in a 9x9 glass pit with an iron shovel | `pour` sand / red sand / gravel / concrete powder / anvils; more blocks cover a wider square (1, 3x3, 5x5, 7x7) | `shovel`, `dig` (clears the 3x3 above their head), `haste`, `heal` | win: alive when the timer (3 min) runs out; lose: die |
| **Sheep Out** (`sheepout`) | stands in a 13x13 glass-fenced meadow with a stone sword | `sheep` (any colour, random or rainbow jeb_ sheep) | `sword`, `smite` (lightning removes sheep), `heal` | win: timer (3 min) runs out; lose: 40 sheep at once, or die |

## Commands

```
/gdg help | status | stopall
/gdg <bedrockbox|sandpour|sheepout> start [player] | stop | reset | status | help
/gdg bedrockbox tnt <1-20> [viewer]
/gdg bedrockbox sand <1-64> <sand|red_sand|gravel> [viewer]
/gdg bedrockbox anvil <1-10> [viewer]
/gdg bedrockbox mob <zombie|skeleton|creeper|...> <1-10> [viewer]
/gdg bedrockbox pickaxe <wooden|stone|iron|diamond|netherite> [viewer]
/gdg bedrockbox haste <5-120 s> [viewer]
/gdg bedrockbox drill <1-10> [viewer]
/gdg bedrockbox heal [viewer]
/gdg sandpour pour <1-200> <sand|red_sand|gravel|concrete|anvil> [viewer]   (anvils capped at 12)
/gdg sandpour shovel <tier> [viewer] | dig [viewer] | haste <s> [viewer] | heal [viewer]
/gdg sheepout sheep <1-30> <random|rainbow|white|red|...> [viewer]
/gdg sheepout sword <tier> [viewer] | smite <1-30> [viewer] | heal [viewer]
```

The viewer's name is always last and may contain spaces. Counts are clamped to the ranges above. Every reply
is one line; failures start with "Could not" (GiftDeck's `MinecraftTarget.IsError` shows them as failed),
e.g. a gift for a game that isn't running, or one that is already over. Permission `giftdeckgames.admin`
(ops); the console and RCON can always run them.

**No player online.** `start` with no name, a selector (`@p`, which is what GiftDeck sends when no player
name is set) or a name that isn't online still builds the arena and runs the game; the reply says so ("...
but nobody is online: they join the game when they log in"). Gifts then land in the empty arena (TNT falls
in the middle of the box, sheep fill the meadow, the Sand Pour timer runs) and the replies end with "(no
player in the game: it happened in the empty arena)". The first player to join (or the named one) is pulled
in. This is also how the live test runs without a Minecraft client.

`plugins/GiftDeckGames/config.yml`: arena world / x / z / y, Bedrock Box depth and obsidian layer, Sand Pour
seconds, Sheep Out seconds and max sheep.

## In GiftDeck

- **Install.** The built jar is committed as `Packs/minecraft/plugins/GiftDeckGames.jar` (copied to GiftDeck's
  output with the rest of `Packs\**`). `MinecraftServer.InstallPlugins()` copies every jar there into the
  server's `plugins/` folder at set-up and before every start: when missing, when GiftDeck's plugin.yml
  version is newer, or when the version is the same but the bytes differ (a rebuilt jar). A newer jar the
  user put there is kept. The console shows "[GiftDeck] Installed the server plugin GiftDeckGames 1.0.0".
- **Commands.** `Packs/minecraft/commands.json` has 29 more commands (target `minecraft:server`), in the
  categories "Mini-games", "Mini-game: Bedrock Box", "Mini-game: Sand Pour", "Mini-game: Sheep Out":
  `gdg_status`, `gdg_stopall`, `bb_start/stop/reset/tnt/sand/anvil/mob/pickaxe/haste/drill/heal`,
  `sp_start/stop/reset/pour/shovel/dig/haste/heal`, `so_start/stop/reset/sheep/sword/smite/heal`. Start uses
  `{player}`; gift commands take `count`/choices plus `viewer` (default `{user}`).
- **Presets** (one per game, 15 events each, ProfileService export layout, made by
  `MinecraftHarness make-game-presets Packs\minecraft\presets`): `minecraft-bedrock-box.giftdeck`,
  `minecraft-sand-pour.giftdeck`, `minecraft-sheep-out.giftdeck`. Cheap gifts do small things (Rose: 4 sand /
  1 sand / 1 sheep), expensive ones big things (Lion: 20 TNT and 5 vindicators / 200 concrete powder / 30
  rainbow sheep); Ice Cream Cone, Perfume, Sunglasses and Glowing Jellyfish help the streamer. Follow and
  Share have small events too. Listed in `pack.json`.
- **Panel.** `MinecraftServerPanel` has a "Mini-games" card: pick a game, Start (sends
  `gdg <game> start <player or @p>`), Start over, Stop; the plugin's reply is shown under it.

## Building

```
cd mods\minecraft\GiftDeckGames
gradlew build          (build\libs\GiftDeckGames.jar)
gradlew copyToPack     (also copies it to Packs\minecraft\plugins\, then commit that jar)
```

Gradle 9.8 wrapper, any JDK 17+ to run it. Compiled against `paper-api:1.20.1-R0.1-SNAPSHOT` with
`--release 17` and `api-version: '1.20'`, so one jar runs on 1.20.1 (Java 17) up to 26.x (Java 25). To stay
compatible it avoids what was renamed across those versions: potion effects go through the vanilla `effect`
command, enchantments and entity types through `Registry.*.get(NamespacedKey)`, sounds by their string id,
TNT/sheep by class (`world.spawn(loc, TNTPrimed.class)`), falling blocks by placing the block in the air, no
`switch` over Bukkit enums, and calls that may be missing (max health, item names) are wrapped. `build/` and
`.gradle/` are git-ignored; GiftDeck.csproj already excludes `mods\**`.

## Tested

`MinecraftHarness unit` checks the bundled jar, the plugin copy rules (missing / same / user's newer / older),
the new commands and the three presets. `MinecraftHarness games <scratch folder> [version]` downloads Paper
into a scratch folder, accepts the EULA for that throwaway server, installs the plugin the way GiftDeck does,
starts it on ports 25665/25675, and over RCON runs every game's start, every gift command through
`MinecraftTarget` (commands.json), checks the arenas with vanilla `execute if block` / `execute if entity`,
plays a Sheep Out loss (44 sheep), a Sand Pour timer win (30 s for the test), reset, stop and clean-up, then
reads the server log for errors and stops the server.

Results (2026-09-27): 64 of 64 checks passed on each of **Paper 26.2** build 129 (the newest stable; Java
25.0.4 Temurin JRE downloaded into the scratch folder by GiftDeck's own code), **1.21.11** build 132 (Java 21)
and **1.20.1** build 196 (Java 17). On all three the plugin loaded ("GiftDeck Games ready"), the server log had
no errors and no plugin warnings, and the server stopped with exit code 0. Sample replies:

```
bb_start  Started Bedrock Box at 20007 232 7, but nobody is online: they join the game when they log in. Gifts land in the empty arena until then.
bb_tnt    Bedrock Box: Tester "Q" 1 dropped 3 TNT into the box! (no player in the game: it happened in the empty arena)
bb_anvil  Bedrock Box: Tester "Q" 1 dropped 2 anvils! (...)
bb_drill  Bedrock Box: Tester "Q" 1 drilled 5 blocks down (...)
          gdg bedrockbox sand 4 lava Bob  ->  Could not use "lava": pick one of sand, red_sand, gravel
sp_start  Stopped Bedrock Box. Started Sand Pour at ...
sp_pour   Sand Pour: Tester "Q" 1 poured 200 sand! (...)          (anvils: "poured 12 anvils!" for 50)
status    Sand Pour: over (Time's up! The streamer survived.)
sp_pour   Could not pour: Sand Pour is over (Time's up! The streamer survived.). Reset or stop it.
status    Sheep Out: over (The meadow is full: 44 sheep!)
log       [GiftDeckGames] Sheep Out over: The meadow is full: 44 sheep! Top viewers: Tester "Q" 1 55
```

The first runs found two bugs, both fixed. Gifts arriving faster than blocks fall used to find the drop
column full ("2 anvils" dropped 1): blocks now go into the highest free spot and try other columns. Drill
with nobody in the box drilled 0: it now starts from the top of the stone.

## Needs a real player

The test has no Minecraft client, so these were not seen in a game: the kit and inventory save/restore
(teleport in, items back afterwards, also after dying and after a server stop mid-game), pulling in a
player who joins after the start, the Bedrock Box win at the emerald floor, TNT/anvil/sand damage and
suffocation on a player, the boss bar and sidebar on screen, titles and sounds, sword kills counting as
"cleared" in Sheep Out, the protected walls (glass can't be broken, the meadow can't be dug), and how hard
each game feels (depth 30 with one obsidian layer, 3-minute timers, 40 sheep: all in config.yml).
