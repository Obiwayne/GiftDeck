# Sources (7 Days to Die pack)

- https://wiki.7d2d.net/game/commands/ - full console command list and usage (read from a V3.2.0 b9 dedicated server with `help outputdetailed`): spawnentity, spawnscouts, spawnwandering, spawnairdrop, give, givexp, buffplayer, killall, kill, settime, weather, say, listplayers, version; Server vs Client commands
- https://raw.githubusercontent.com/tassoneroberto/7dtd-assets/main/v3.2/Config/entityclasses.xml - V3.2 entity class names and UserSpawnType (zombies, animals)
- https://raw.githubusercontent.com/tassoneroberto/7dtd-assets/main/v3.2/Config/items.xml - V3.2 item names used by `give`
- https://raw.githubusercontent.com/tassoneroberto/7dtd-assets/main/v3.2/Config/buffs.xml - V3.2 buff names and durations used by `buffplayer`
- https://raw.githubusercontent.com/tassoneroberto/7dtd-assets/main/v3.2/Config/biomes.xml - biome names for `weather Storm`
- https://github.com/JaithWraith/7dtd_nogore - V2.0 entityclasses.xml/items.xml, used to cross-check names
- https://commands.gg/7dtd/spawnentity - spawnentity takes a player entity ID plus an entity class name
- https://7daystodie.wiki.gg/wiki/Entity-ID - spawnentity usage (numeric player ID)
- https://7daystodie.wiki.gg/wiki/Server:_serverconfig.xml - TelnetEnabled, TelnetPort, TelnetPassword (loopback only without a password)
- https://hub.tcno.co/games/7daystodie/dedicated_server/ - dedicated server app 294420, install folder, startdedicated.bat, joining 127.0.0.1:26900, telnet on 8081
- https://github.com/GameServerManagers/LinuxGSM/discussions/3636 - "*** ERROR: unknown command" reply over telnet
