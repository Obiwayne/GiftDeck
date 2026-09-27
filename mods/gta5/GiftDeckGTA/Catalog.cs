using System.Collections.Generic;
using System.Linq;

namespace GiftDeckGTA
{
    public class ArgInfo
    {
        public string Name, Label, Type = "text", Default = "";
        public string[] Choices = new string[0];

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = Name,
                ["label"] = Label,
                ["type"] = Type,
                ["default"] = Default,
            };
            if (Choices.Length > 0) d["choices"] = Choices.ToList();
            return d;
        }
    }

    public class CommandInfo
    {
        public string Id, Name, Category, Description = "";
        public List<ArgInfo> Args = new List<ArgInfo>();

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["name"] = Name,
            ["category"] = Category,
            ["description"] = Description,
            ["args"] = Args.Select(a => (object)a.ToJson()).ToList(),
        };
    }

    // Every command this script offers GiftDeck: the triggers StreamToEarn has for GTA that aren't Chaos Mod
    // effects (Chaos Mod V covers those). Only names and arguments live here, so the test harness can build
    // Packs/gta5/commands.json from it without the game; what each one does is in Commands.cs.
    public static class Catalog
    {
        public const string Game = "gta5";
        public const string Mod = "giftdeck";
        public const string Name = "GiftDeck GTA";
        public const string Version = "1.0.0";
        public static string TargetId => Game + ":" + Mod;

        public static readonly string[] Categories = { "Player", "Vehicle", "Peds", "Weapons", "World", "Teleport", "Fun" };

        public static readonly string[] PopularVehicles =
        {
            "adder", "zentorno", "t20", "osiris", "entityxf", "turismor", "infernus", "cheetah", "banshee", "comet2",
            "elegy2", "sultan", "kuruma", "dominator", "gauntlet", "blista", "panto", "faggio", "bati", "akuma",
            "sanchez", "bmx", "monster", "sandking", "bodhi2", "dune", "blazer", "rhino", "insurgent", "dump",
            "bus", "firetruk", "police", "ambulance", "taxi", "stretch", "tractor", "mower", "caddy", "forklift",
            "towtruck", "phantom", "buzzard", "maverick", "frogger", "cargobob", "lazer", "duster", "jetmax", "seashark",
        };

        public static readonly string[] Weapons =
        {
            "pistol", "combatpistol", "appistol", "pistol50", "revolver", "microsmg", "smg", "assaultsmg", "mg", "combatmg",
            "assaultrifle", "carbinerifle", "advancedrifle", "specialcarbine", "bullpuprifle", "pumpshotgun", "sawnoffshotgun",
            "assaultshotgun", "sniperrifle", "heavysniper", "rpg", "grenadelauncher", "minigun", "firework", "railgun",
            "hominglauncher", "grenade", "stickybomb", "molotov", "flaregun", "stungun", "knife", "bat", "crowbar", "golfclub",
            "hammer", "machete", "gusenberg", "musket", "flare", "snowball", "ball",
        };

        public static readonly string[] WeatherTypes =
        {
            "EXTRASUNNY", "CLEAR", "CLOUDS", "SMOG", "FOGGY", "OVERCAST", "RAIN", "THUNDER", "CLEARING",
            "NEUTRAL", "SNOW", "BLIZZARD", "SNOWLIGHT", "XMAS", "HALLOWEEN",
        };

        public static readonly string[] AttackerWeapons = { "pistol", "smg", "rifle", "shotgun", "knife", "none" };

        public static readonly List<CommandInfo> Commands = Build();

        public static CommandInfo Find(string id) => Commands.FirstOrDefault(c => c.Id == id);

        public static Dictionary<string, object> Hello() => new Dictionary<string, object>
        {
            ["type"] = "hello",
            ["game"] = Game,
            ["mod"] = Mod,
            ["name"] = Name,
            ["version"] = Version,
            ["commands"] = Commands.Select(c => (object)c.ToJson()).ToList(),
        };

        // Packs/gta5/commands.json: the same list as hello.commands, each command also naming its target
        // (the pack lists gta5:chaosmod and gta5:giftdeck).
        public static List<object> PackCommands() => Commands.Select(c =>
        {
            var d = c.ToJson();
            d["target"] = TargetId;
            return (object)d;
        }).ToList();

        static List<CommandInfo> Build()
        {
            var list = new List<CommandInfo>();
            void Add(string id, string name, string category, string description, params ArgInfo[] args) =>
                list.Add(new CommandInfo { Id = id, Name = name, Category = category, Description = description, Args = args.ToList() });
            ArgInfo Number(string name, string label, int def) => new ArgInfo { Name = name, Label = label, Type = "number", Default = def.ToString() };
            ArgInfo Choice(string name, string label, string def, string[] choices) => new ArgInfo { Name = name, Label = label, Type = "choice", Default = def, Choices = choices };

            // ---- Player
            Add("heal", "Heal", "Player", "Full health.");
            Add("armour", "Give armour", "Player", "Full body armour.");
            Add("heal_armour", "Heal and armour", "Player", "Full health and full body armour.");
            Add("kill_player", "Kill player", "Player", "Wasted.");
            Add("wanted_up", "Wanted level +1", "Player", "One more wanted star.");
            Add("wanted_down", "Wanted level -1", "Player", "One wanted star less.");
            Add("wanted_max", "Wanted level max", "Player", "Five stars.");
            Add("wanted_clear", "Clear wanted level", "Player", "The police lose interest.");
            Add("add_money", "Add money", "Player", "Adds money to the current character (a negative amount takes it away).",
                Number("amount", "Amount ($)", 1000));
            Add("set_money", "Set money", "Player", "Sets the current character's money.", Number("amount", "Amount ($)", 0));
            Add("random_clothing", "Random clothing", "Player", "Puts the player in a random outfit.");
            Add("jail_player", "Jail player", "Player", "Locks the player in a cage for a while.", Number("seconds", "Seconds in jail", 30));

            // ---- Vehicle
            Add("spawn_vehicle", "Spawn vehicle", "Vehicle", "Spawns a vehicle next to the player. Any model name works, not only the ones in the list.",
                Choice("model", "Vehicle model", "adder", PopularVehicles),
                Choice("enter", "Put the player in it", "no", new[] { "no", "yes" }));
            Add("spawn_random_vehicle", "Spawn random vehicle", "Vehicle", "Spawns a random vehicle next to the player.");
            Add("spawn_random_vehicle_drive", "Spawn random vehicle and drive", "Vehicle", "Puts the player in the driver's seat of a random vehicle.");
            Add("delete_vehicle", "Delete player vehicle", "Vehicle", "Removes the vehicle the player is in (or just got out of).");
            Add("repair_vehicle", "Repair vehicle", "Vehicle", "Fixes and cleans the player's vehicle.");
            Add("explode_vehicles", "Explode vehicles nearby", "Vehicle", "Blows up the vehicles around the player (not the player's own).",
                Number("radius", "Radius (m)", 40));
            Add("spawn_ramp", "Spawn ramp", "Vehicle", "Drops a stunt ramp in front of the player.");
            Add("tune_random", "Random vehicle tuning", "Vehicle", "Random mods, paint, wheels and neon on the player's vehicle.");
            Add("tune_full", "Full vehicle tuning", "Vehicle", "Best engine, brakes, gearbox, armour and turbo, plus a new paint job.");

            // ---- Weapons
            Add("give_weapon", "Give weapon", "Weapons", "Gives the player a weapon with ammo. Any weapon name works (e.g. pistol, WEAPON_RPG).",
                Choice("weapon", "Weapon", "pistol", Weapons), Number("ammo", "Ammo", 250));
            Add("give_random_weapon", "Give random weapon", "Weapons", "Gives the player a random weapon with ammo.");
            Add("set_ammo", "Set ammo", "Weapons", "Sets the ammo of the weapon in the player's hands.", Number("ammo", "Ammo", 9999));
            Add("remove_weapons", "Remove weapons", "Weapons", "Takes all of the player's weapons.");

            // ---- Peds
            Add("spawn_attackers", "Spawn attackers", "Peds", "Hostile peds that hunt the player.",
                Number("count", "How many", 3), Choice("weapon", "Their weapon", "pistol", AttackerWeapons));
            Add("arm_attackers", "Arm attackers", "Peds", "Gives every spawned attacker a better gun.",
                Choice("weapon", "Weapon", "rifle", AttackerWeapons));
            Add("remove_attackers", "Remove attackers", "Peds", "Removes every attacker, monkey and moto spawned by GiftDeck.");
            Add("moto_cops", "Moto cops", "Peds", "Police on motorbikes come after the player.", Number("count", "How many", 2));
            Add("moto_bandits", "Moto bandits", "Peds", "Armed bikers come after the player.", Number("count", "How many", 2));
            Add("monkey_killers", "Monkey killers", "Peds", "Armed chimps that attack the player.", Number("count", "How many", 3));
            Add("spawn_pigeons", "Spawn pigeons", "Peds", "A flock of pigeons around the player.", Number("count", "How many", 10));
            Add("spawn_poodles", "Spawn poodles", "Peds", "Poodles around the player.", Number("count", "How many", 5));
            Add("spawn_random_animals", "Spawn random animals", "Peds", "Random animals around the player.", Number("count", "How many", 5));

            // ---- World
            Add("set_time", "Set time", "World", "Sets the clock (hour 0 to 23).", Number("hour", "Hour", 0));
            Add("set_weather", "Set weather", "World", "Changes the weather.", Choice("weather", "Weather", "THUNDER", WeatherTypes));
            Add("earthquake", "Earthquake", "World", "The ground shakes and cars jump for a while.", Number("seconds", "Seconds", 15));

            // ---- Teleport
            Add("tp_airport", "Teleport to LS Airport", "Teleport", "Los Santos International Airport.");
            Add("tp_maze_bank", "Teleport to Maze Bank top", "Teleport", "The roof of the Maze Bank Tower.");
            Add("tp_fort_zancudo", "Teleport to Fort Zancudo", "Teleport", "Inside the army base. Expect company.");
            Add("tp_chiliad", "Teleport to Mount Chiliad", "Teleport", "The top of Mount Chiliad.");
            Add("tp_random", "Teleport to a random place", "Teleport", "Somewhere random on the map.");
            Add("tp_waypoint", "Teleport to waypoint", "Teleport", "The waypoint set on the map, if there is one.");

            // ---- Fun
            Add("skydive", "Skydive", "Fun", "Sends the player high into the sky with a parachute.", Number("height", "Height (m)", 800));
            Add("launch_up", "Launch player up", "Fun", "Throws the player (or their car) into the air.");
            Add("drunk", "Drunk", "Fun", "Wobbly walk, blurry screen and a shaky camera for a while.", Number("seconds", "Seconds", 30));
            Add("nothing", "Nothing", "Fun", "Does nothing: for testing that gifts reach the game.");

            return list;
        }
    }
}
