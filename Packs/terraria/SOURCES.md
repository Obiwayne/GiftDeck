# Terraria pack: sources

Checked 2026-09-28 against TShock 6.2.1 for Terraria 1.4.5.8 (released 2026-09-27). The `general-devel`
Commands.cs was identical to the v6.2.1 tag.

## TShock REST API
- https://github.com/Pryaxis/TShock/blob/v6.2.1/TShockAPI/Rest/RestManager.cs
  `/v3/server/rawcmd` (`cmd`, `token`), permission `RestPermissions.restrawcommand`, runs
  `Commands.HandleCommand(new TSRestPlayer(...), cmd)` and returns `{"status":"200","response":[lines]}`.
  `/server/rawcmd` redirects to v3.
- https://github.com/Pryaxis/TShock/blob/v6.2.1/TShockAPI/Rest/SecureRest.cs
  Token read from the `token` parameter; checks session tokens, then `ApplicationRestTokens` from config
  and `--rest-token` startup tokens (loaded once, at start). Errors: 401 "Not authorized. The specified API
  endpoint requires a token.", 403 "...the provided token was not valid." TokenData = `Username`, `UserGroupName`.
- https://github.com/Pryaxis/TShock/blob/v6.2.1/TShockAPI/Configuration/TShockConfig.cs
  `RestApiEnabled` (default false), `RestApiPort = 7878`, `ApplicationRestTokens` (dictionary). Config is
  `ConfigFile<TShockSettings>` so the keys sit inside `"Settings"`.
- https://github.com/Pryaxis/TShock/blob/v6.2.1/TShockAPI/TShock.cs
  `SavePath = "tshock"` relative to TShock.Server.exe (so `tshock\config.json`); `-rest-token` startup
  tokens use group `superadmin`.
- https://github.com/Pryaxis/TShock/blob/v6.2.1/TShockAPI/DB/GroupManager.cs
  `superadmin` is a built-in in-memory group (SuperAdminGroup), so it works as `UserGroupName`.

## Why no /spawnmob, /spawnboss, /item, /buff, /tp
- Commands.cs `HandleCommand`: strips the first character (the `/` specifier) and refuses commands with
  `AllowServer = false` when `!player.RealPlayer` ("You must use this command in-game.").
- TSPlayer.cs: `TSRestPlayer` uses the `TSPlayer(string)` constructor, `Index = -1`, so `RealPlayer` is false.
- AllowServer = false in v6.2.1: spawnmob/sm, spawnboss/sb, item/i, buff, tp, tphere, tpnpc, tppos, home,
  spawn, grow, pos, setspawn, setdungeon, party, wallow, death, pvpdeath, tpallow.
- `/rocket` needs server-side characters (SSC) and a logged-in player, so it's left out.

## Command syntax used (all from Commands.cs v6.2.1)
- `/worldevent meteor|fullmoon|bloodmoon|eclipse|invasion <goblins|snowmen|pirates|pumpkinmoon [wave]|frostmoon [wave]|martians>|sandstorm|rain [slime|coin]|lanterns|meteorshower`
  bloodmoon, eclipse, sandstorm, rain, lanterns, meteorshower toggle; invasion ends a running invasion.
- `/give <item type/id> <player> [amount] [prefix]`, `/gbuff <player> <buff name|ID> [seconds]`,
  `/heal <player> [amount]`, `/slap <player> [damage]` (not clamped when the group has `kill`),
  `/kill <player>`, `/respawn <player>`, `/godmode <player>` (toggle), `/annoy <player> <seconds>`,
  `/firework <player> [red|green|blue|yellow|star|spiral|rings|flower]`, `/butcher`,
  `/time day|night|noon|midnight|hh:mm`, `/wind <-40..40>`, `/bc <message>`,
  `/playing` (no permission needed: the test command).
- Quoting: `ParseParameters` handles `"..."` and `\"`, `\\` escapes, so `{player:str}` / `{item:str}` work
  for names with spaces.
- Name lookup (Utils.cs): exact item/buff name (current language, then English) wins over partial matches.

## Item and buff names
Extracted from the English localisation embedded in `bin\OTAPI.dll` of the TShock 6.2.1 win-x64 zip
(the Terraria 1.4.5.8 server's own ItemName / BuffName tables). Every choice in commands.json matches an
exact English name there. Not found and left out: "Well Fed", "Slimed", "Venom", "Shimmer", "Sugar Rush",
"Lucky", "Cake", "Rubber Chicken".

## Windows install
- https://github.com/Pryaxis/TShock/releases/tag/v6.2.1 (win-x64 zip contains TShock.Server.exe, bin\,
  ServerPlugins\; `TShockAPI.deps.json` targets .NETCoreApp 9.0, hence the .NET 9 Runtime).
- Vanilla/TShock server default game port 7777 (TShockConfig `ServerPort`).
- https://developer.valvesoftware.com/wiki/Dedicated_Servers_List (Terraria dedicated server = 105600,
  same as the game).
