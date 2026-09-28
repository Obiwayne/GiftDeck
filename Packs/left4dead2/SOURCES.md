# Left 4 Dead 2 pack: sources

Checked 2026-09-28.

## Commands, flags and defaults
From the Valve Developer Community cvar dump (flags as the game reports them; "sv"+"cheat" = server side,
needs sv_cheats 1):
https://developer.valvesoftware.com/wiki/List_of_Left_4_Dead_2_console_commands_and_variables
(read through web.archive.org because VDC blocks scripts)

| command | default | flags | used for |
|---|---|---|---|
| director_force_panic_event | cmd | sv, cheat | Horde |
| director_panic_forever | 0 | sv, cheat | Endless horde |
| z_common_limit | 30 | sv, cheat | More zombies |
| z_speed | 250 | cheat, rep, cl | Fast zombies |
| z_health | 50 | sv, cheat | Tough zombies |
| z_difficulty | Normal | rep, cl | Easy, Normal, Hard, Impossible |
| nb_delete_all | cmd | sv, cheat | "Delete all non-player NextBot entities" |
| director_stop / director_start | cmd | sv, cheat | stop / restore all spawning |
| director_no_specials | 0 | sv, cheat | no special infected |
| god | 0 | sv, cheat, nf | "Survivors don't take damage" |
| sv_infinite_ammo | 0 | cheat, rep, cl | infinite ammo |
| sv_gravity | 800 | nf, rep, cl, launcher | low gravity (not a cheat cvar) |
| host_timescale | 1.0 | cheat, rep | slow motion |
| sb_stop | 0 | sv, cheat | "Forces survivor bots to stand still" |
| say | cmd | sv | chat message |
| status | cmd | | test command |
| sv_allow_lobby_connect_only | 1 | | must be 0 to join by IP |
| sv_lan | 0 | | LAN only, no heartbeat |
| sv_cheats | 0 | nf, rep | |

## Left out: need a player's console, not the server's
- z_spawn / z_spawn_old ("Spawns the specified zombie(s) under your cursor, or out in the world ... if auto
  ... is specified"), give, hurtme, kill, explode, upgrade_add, warp_all_survivors_here: all act on the
  player who typed them. Running them from the server console does nothing:
  - https://forums.alliedmods.net/archive/index.php/t-157504.html ("z_spawn or give commands won't work
    server side (yes i have sv_cheats 1)")
  - https://forums.alliedmods.net/archive/index.php/t-238674.html (ServerCommand("z_spawn_old tank auto")
    with sv_cheats enabled: "No tanks spawned")
  - https://forums.alliedmods.net/archive/index.php/t-291255.html (plugins use FakeClientCommand on a client)
- director_force_tank / director_force_witch: cvars exist (sv, cheat) but have no description and we found
  no reliable account of what they do, so they're left out.
- VScript through the `script` command (sv, cheat; ZSpawn, GiveItem, SetHealth ... per
  https://developer.valvesoftware.com/wiki/Left_4_Dead_2/Scripting/Script_Functions) runs on the server and
  could give items/health or spawn infected from RCON, but it's untested over RCON (Source splits commands
  on ';' and quotes) so nothing uses it yet.

## Server, RCON, VAC
- Left 4 Dead 2 Dedicated Server = app 222860 (anonymous download: yes):
  https://developer.valvesoftware.com/wiki/Dedicated_Servers_List
- `-insecure`: "Starts the server without Valve Anti-Cheat"; `-usercon` is documented for CS:GO servers only:
  https://developer.valvesoftware.com/wiki/Command_line_options
- RCON uses the game port (TCP 27015); set with rcon_password:
  https://zap-hosting.com/guides/docs/l4d2-rcon/
- VAC "is an automated system designed to detect cheats installed on users' computers":
  https://support.steampowered.com/kb/7849-RADZ-6869/
- Listen servers (a game you host from the lobby): we found no reliable source that RCON works on an L4D2
  listen server, so the guide uses the dedicated server only.
