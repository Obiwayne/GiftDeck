# Sources (Project Zomboid pack)

- https://github.com/Ketum-Git/PZ-Javacode/tree/main/PZ_42.20.2/zombie/commands/serverCommands - Build 42.20.2 server commands (names, argument patterns, replies): createhorde, chopper, gunshot, godmodplayer, addxp, additem, addvehicle, startrain, stoprain, startstorm, stopweather, thunder, lightning, servermsg, players
- https://github.com/Ketum-Git/PZ-Javacode/tree/main/PZ_41.78/zombie/commands/serverCommands - Build 41.78 versions of the same commands, used to check B41 differences
- https://github.com/Ketum-Git/PZ-Javacode/blob/main/PZ_42.20.2/zombie/commands/CommandBase.java - command matching (no leading slash), quoted arguments
- https://github.com/Ketum-Git/PZ-Javacode/blob/main/PZ_42.20.2/zombie/network/GameServer.java - RCON command handling, "Unknown command" reply, RCON starts only with a non-empty RCONPassword
- https://github.com/Ketum-Git/PZ-Javacode/blob/main/PZ_42.20.2/zombie/network/RCONServer.java - RCON listener
- https://github.com/Ketum-Git/PZ-Javacode/blob/main/PZ_42.20.2/zombie/network/ServerOptions.java - RCONPort default 27015, DefaultPort 16261
- https://github.com/Ketum-Git/PZ-Javacode/blob/main/PZ_42.20.2/zombie/characters/skills/PerkFactory.java - perk (skill) ids for addxp
- https://github.com/wink-/pzmcp/tree/HEAD/media/scripts - Build 42 item and vehicle script names
- https://github.com/Niteghxst/Project-Zomboid-Media-Files/tree/HEAD/media/scripts - Build 41 item and vehicle script names
- https://steamcommunity.com/sharedfiles/filedetails/?id=2846078620 - dedicated server set-up, StartServer64.bat memory (-Xms/-Xmx), servertest settings
- https://esagames.ro/how-to-make-a-project-zomboid-server.php - first run asks for an admin password, C:\Users\<you>\Zomboid\Server\servertest.ini, port 16261
