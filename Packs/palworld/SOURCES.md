# Palworld pack: sources

Checked 2026-09-28. Tier "keys": no console, no commands.json.

## Why there's no console tier
- https://docs.palworldgame.com/settings-and-operation/commands
  The whole server command list: AdminPassword, Shutdown [seconds] [message], DoExit, Broadcast <message>,
  KickPlayer <SteamID>, BanPlayer <SteamID>, TeleportToPlayer <SteamID>, TeleportToMe <SteamID>,
  ShowPlayers, Info, Save, UnBanPlayer <SteamID>, ToggleSpectate. Nothing spawns Pals or items or changes
  weather/time. The teleport commands act on the admin's own in-game character, so they aren't usable
  from RCON either.
- https://docs.palworldgame.com/api/rcon/
  "RCON is now deprecated. Please consider to use REST API. RCON is scheduled to stop functioning in an
  upcoming update." Default RCON port 25575; multi-byte characters get cut.
- https://docs.palworldgame.com/settings-and-operation/configuration
  RCONEnabled, RCONPort, AdminPassword, RESTAPIEnabled, RESTAPIPort; Windows config file
  `steamapps\common\PalServer\Pal\Saved\Config\WindowsServer\PalWorldSettings.ini`, copied from
  `DefaultPalWorldSettings.ini` (editing the default file does nothing).
- Broadcast over RCON stops at the first space (only the first word arrives):
  https://github.com/thijsvanloef/palworld-server-docker/issues/296 ,
  https://github.com/Darkhand81/Palworld_broadcast_encoding_bug ,
  https://steamcommunity.com/app/1623730/discussions/0/4132683277287261970/
- GiftDeck's console protocols are rcon / telnet / tshock; Palworld's REST API (the non-deprecated one)
  isn't one of them, and it has the same admin-only feature set anyway.
- Palworld Dedicated Server app id 2394010: https://developer.valvesoftware.com/wiki/Dedicated_Servers_List

## Default keys
Two guides agree on these defaults (PC keyboard):
- https://game8.co/games/Palworld/archives/439684
- https://www.shacknews.com/article/138382/palworld-controls-pc-keybindings
W/A/S/D move, Space jump, Shift sprint, Ctrl roll, C crouch/slide, E summon Pal, Q throw Pal Sphere,
F partner skill, R reload, 1 / 3 change Pal left/right, 2 change sphere.
Left out because the sources disagree: 4 (emote wheel vs. Pal commands), and pick-up key (F vs. V).
Mouse buttons are left out (GiftDeck's key presses are keyboard only).

## Not verified
- windowTitle "Pal": the game is an Unreal project named "Pal" (Pal\Binaries\Win64\Palworld-Win64-Shipping.exe),
  but no source states the window title. "Pal" also matches "Palworld". Check it on a real PC.
