using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GiftDeckGTA
{
    // What each command in Catalog.cs does. A handler returns a short message for GiftDeck's log, or throws
    // CommandFailed with the reason it couldn't run.
    public partial class GiftDeckScript
    {
        Dictionary<string, Func<Trigger, string>> _commands;

        static Ped Player => Game.Player.Character;

        void InitCommands()
        {
            _commands = new Dictionary<string, Func<Trigger, string>>
            {
                // Player
                ["heal"] = t => { Player.Health = Player.MaxHealth; return "Healed"; },
                ["armour"] = t => { Player.Armor = MaxArmour(); return "Armour on"; },
                ["heal_armour"] = t => { Player.Health = Player.MaxHealth; Player.Armor = MaxArmour(); return "Healed with armour"; },
                ["kill_player"] = t => { if (Player.IsDead) throw new CommandFailed("Already dead"); Player.Kill(); return "Wasted"; },
                ["wanted_up"] = t => SetWanted(WantedLevel() + 1),
                ["wanted_down"] = t => SetWanted(WantedLevel() - 1),
                ["wanted_max"] = t => SetWanted(5),
                ["wanted_clear"] = t => SetWanted(0),
                ["add_money"] = t => SetMoney((long)Game.Player.Money + ArgInt(t, "amount", -100000000, 100000000)),
                ["set_money"] = t => SetMoney(ArgInt(t, "amount", 0, int.MaxValue)),
                ["random_clothing"] = t => RandomClothing(),
                ["jail_player"] = t => Jail(ArgInt(t, "seconds", 5, 600)),

                // Vehicle
                ["spawn_vehicle"] = t => SpawnVehicleCommand(Arg(t, "model"), IsYes(Arg(t, "enter"))),
                ["spawn_random_vehicle"] = t => SpawnVehicleCommand(Pick(Catalog.PopularVehicles), false),
                ["spawn_random_vehicle_drive"] = t => SpawnVehicleCommand(Pick(Catalog.PopularVehicles.Where(IsRoadVehicle).ToArray()), true),
                ["delete_vehicle"] = t => DeleteVehicle(),
                ["repair_vehicle"] = t => RepairVehicle(),
                ["explode_vehicles"] = t => ExplodeNearby(ArgInt(t, "radius", 5, 200)),
                ["spawn_ramp"] = t => SpawnRamp(),
                ["tune_random"] = t => Tune(false),
                ["tune_full"] = t => Tune(true),

                // Weapons
                ["give_weapon"] = t => GiveWeapon(Arg(t, "weapon"), ArgInt(t, "ammo", 1, 9999)),
                ["give_random_weapon"] = t => GiveWeapon(Pick(Catalog.Weapons), 250),
                ["set_ammo"] = t => SetAmmo(ArgInt(t, "ammo", 0, 9999)),
                ["remove_weapons"] = t => { Function.Call(N.REMOVE_ALL_PED_WEAPONS, Player.Handle, true); return "Weapons removed"; },

                // Peds
                ["spawn_attackers"] = t => SpawnAttackers(ArgInt(t, "count", 1, 15), Arg(t, "weapon")),
                ["arm_attackers"] = t => ArmAttackers(Arg(t, "weapon")),
                ["remove_attackers"] = t => RemoveAttackers(),
                ["moto_cops"] = t => SpawnMotos(ArgInt(t, "count", 1, 6), true),
                ["moto_bandits"] = t => SpawnMotos(ArgInt(t, "count", 1, 6), false),
                ["monkey_killers"] = t => SpawnMonkeys(ArgInt(t, "count", 1, 10)),
                ["spawn_pigeons"] = t => SpawnAnimals(new[] { "a_c_pigeon" }, ArgInt(t, "count", 1, 30)),
                ["spawn_poodles"] = t => SpawnAnimals(new[] { "a_c_poodle" }, ArgInt(t, "count", 1, 20)),
                ["spawn_random_animals"] = t => SpawnAnimals(RandomAnimals, ArgInt(t, "count", 1, 20)),

                // World
                ["set_time"] = t => SetTime(ArgInt(t, "hour", 0, 23)),
                ["set_weather"] = t => SetWeather(Arg(t, "weather")),
                ["earthquake"] = t => StartEarthquake(ArgInt(t, "seconds", 3, 120)),

                // Teleport
                ["tp_airport"] = t => TeleportTo(Places[0]),
                ["tp_maze_bank"] = t => TeleportTo(Places[1]),
                ["tp_fort_zancudo"] = t => TeleportTo(Places[2]),
                ["tp_chiliad"] = t => TeleportTo(Places[3]),
                ["tp_random"] = t => TeleportTo(Places[_rng.Next(Places.Length)]),
                ["tp_waypoint"] = t => TeleportToWaypoint(),

                // Fun
                ["skydive"] = t => Skydive(ArgInt(t, "height", 100, 3000)),
                ["launch_up"] = t => LaunchUp(),
                ["drunk"] = t => StartDrunk(ArgInt(t, "seconds", 3, 300)),
                ["nothing"] = t => "Nothing happened (as asked)",
            };

            foreach (var c in Catalog.Commands)
                if (!_commands.ContainsKey(c.Id)) Logger.Write("No handler for catalog command " + c.Id);
        }

        // ---------------------------------------------------------------- arguments

        // An argument's value as text: what GiftDeck sent, else the catalog default.
        static string Arg(Trigger t, string name)
        {
            if (t.Args != null && t.Args.TryGetValue(name, out var v) && v != null)
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
                if (s.Length > 0) return s;
            }
            return Catalog.Find(t.Command)?.Args.FirstOrDefault(a => a.Name == name)?.Default ?? "";
        }

        static int ArgInt(Trigger t, string name, int min, int max)
        {
            string s = Arg(t, name);
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new CommandFailed($"'{s}' isn't a number ({name})");
            return (int)Math.Max(min, Math.Min(max, Math.Round(d)));
        }

        static bool IsYes(string s) => s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);

        T Pick<T>(T[] items) => items[_rng.Next(items.Length)];

        float Rand(float min, float max) => min + (float)_rng.NextDouble() * (max - min);

        // ---------------------------------------------------------------- helpers

        static Model LoadModel(string name, string kind)
        {
            var model = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hash)
                ? new Model(hash)
                : new Model(name);
            if (!model.IsInCdImage || !model.IsValid) throw new CommandFailed($"There's no {kind} called '{name}'");
            if (!model.Request(3000)) throw new CommandFailed($"The {kind} '{name}' didn't load in time");
            return model;
        }

        // Somewhere near the player, on the ground.
        Vector3 NearPlayer(float minDist, float maxDist)
        {
            Vector3 p = Player.Position;
            for (int i = 0; i < 6; i++)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                float d = Rand(minDist, maxDist);
                var c = new Vector3(p.X + (float)Math.Cos(a) * d, p.Y + (float)Math.Sin(a) * d, p.Z);
                float g = World.GetGroundHeight(new Vector3(c.X, c.Y, p.Z + 5f));
                if (g > 0 && Math.Abs(g - p.Z) < 6f) return new Vector3(c.X, c.Y, g + 0.5f);
            }
            return p + Player.ForwardVector * minDist;
        }

        // A spot on a road some way behind the player, for things that drive up.
        Vector3 RoadBehind(float distance)
        {
            Entity from = (Entity)Player.CurrentVehicle ?? Player;
            Vector3 guess = from.Position - from.ForwardVector * distance;
            Vector3 street = World.GetNextPositionOnStreet(guess, true);
            return street == Vector3.Zero || street.DistanceTo(from.Position) > distance * 3 ? guess : street;
        }

        int MaxArmour() => Function.Call<int>(N.GET_PLAYER_MAX_ARMOUR, Game.Player.Handle);

        // ---------------------------------------------------------------- player

        static int WantedLevel() => Function.Call<int>(N.GET_PLAYER_WANTED_LEVEL, Game.Player.Handle);

        static string SetWanted(int stars)
        {
            stars = Math.Max(0, Math.Min(5, stars));
            Function.Call(N.SET_PLAYER_WANTED_LEVEL, Game.Player.Handle, stars, false);
            Function.Call(N.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player.Handle, false);
            return stars == 0 ? "Wanted level cleared" : $"Wanted level {stars}";
        }

        static string SetMoney(long amount)
        {
            int money = (int)Math.Max(0, Math.Min(int.MaxValue, amount));
            Game.Player.Money = money;
            return "Money is now $" + money.ToString("N0", CultureInfo.InvariantCulture);
        }

        string RandomClothing()
        {
            Ped ped = Player;
            // Face (0) and hair (2) stay; everything else gets a random valid drawable and texture
            foreach (int component in new[] { 1, 3, 4, 5, 6, 7, 8, 9, 10, 11 })
            {
                int drawables = Function.Call<int>(N.GET_NUMBER_OF_PED_DRAWABLE_VARIATIONS, ped.Handle, component);
                if (drawables <= 0) continue;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    int drawable = _rng.Next(drawables);
                    int textures = Math.Max(1, Function.Call<int>(N.GET_NUMBER_OF_PED_TEXTURE_VARIATIONS, ped.Handle, component, drawable));
                    int texture = _rng.Next(textures);
                    if (!Function.Call<bool>(N.IS_PED_COMPONENT_VARIATION_VALID, ped.Handle, component, drawable, texture)) continue;
                    Function.Call(N.SET_PED_COMPONENT_VARIATION, ped.Handle, component, drawable, texture, 0);
                    break;
                }
            }
            Function.Call(N.CLEAR_ALL_PED_PROPS, ped.Handle);
            Function.Call(N.SET_PED_RANDOM_PROPS, ped.Handle);
            return "New outfit";
        }

        // ---------------------------------------------------------------- vehicles

        static bool IsRoadVehicle(string name)
        {
            var m = new Model(name);
            return m.IsCar || m.IsBike || m.IsQuadBike;
        }

        string SpawnVehicleCommand(string name, bool enter)
        {
            Model model = LoadModel(name, "vehicle");
            if (!model.IsVehicle) throw new CommandFailed($"'{name}' isn't a vehicle");

            Ped ped = Player;
            Vehicle current = ped.CurrentVehicle;
            Vector3 pos;
            float heading;
            if (current != null)
            {
                // Ahead of the car, so it doesn't land on it; keep the speed if we move the player across
                heading = current.Heading;
                pos = current.Position + current.ForwardVector * (8f + current.Speed * 0.5f);
            }
            else
            {
                heading = ped.Heading + 90f;
                pos = ped.Position + ped.ForwardVector * (model.IsHelicopter || model.IsPlane || model.IsBigVehicle ? 10f : 5f);
            }
            if (model.IsHelicopter || model.IsPlane) pos += new Vector3(0f, 0f, 1f);

            Vehicle v = World.CreateVehicle(model, pos, heading);
            model.MarkAsNoLongerNeeded();
            if (v == null || !v.Exists()) throw new CommandFailed("The game wouldn't spawn " + name + " here");
            v.PlaceOnGround();
            Function.Call(N.SET_VEHICLE_ENGINE_ON, v.Handle, true, true, false);

            if (enter)
            {
                float speed = current?.Speed ?? 0f;
                if (current == null) v.Heading = ped.Heading;
                Function.Call(N.SET_PED_INTO_VEHICLE, ped.Handle, v.Handle, -1);
                if (speed > 1f) Function.Call(N.SET_VEHICLE_FORWARD_SPEED, v.Handle, speed);
            }
            // The game cleans it up when the player leaves it behind
            v.MarkAsNoLongerNeeded();
            return "Spawned " + name + (enter ? " (player in it)" : "");
        }

        // The vehicle the player is in, or the one they just got out of.
        static Vehicle PlayerVehicle()
        {
            Ped ped = Player;
            Vehicle v = ped.CurrentVehicle;
            if (v == null)
            {
                Vehicle last = ped.LastVehicle;
                if (last != null && last.Exists() && last.Position.DistanceTo(ped.Position) < 30f) v = last;
            }
            return v != null && v.Exists() ? v : null;
        }

        string DeleteVehicle()
        {
            Vehicle v = PlayerVehicle() ?? throw new CommandFailed("The player has no vehicle");
            if (Player.IsInVehicle(v)) Function.Call(N.CLEAR_PED_TASKS_IMMEDIATELY, Player.Handle);
            v.IsPersistent = true; // only mission entities can be deleted
            v.Delete();
            return "Vehicle deleted";
        }

        string RepairVehicle()
        {
            Vehicle v = PlayerVehicle() ?? throw new CommandFailed("The player has no vehicle");
            v.Repair();
            Function.Call(N.SET_VEHICLE_DIRT_LEVEL, v.Handle, 0f);
            return "Vehicle repaired";
        }

        string ExplodeNearby(int radius)
        {
            Vehicle mine = Player.CurrentVehicle;
            int n = 0;
            foreach (Vehicle v in World.GetNearbyVehicles(Player, radius))
            {
                if (v == null || !v.Exists() || v == mine || v.IsDead) continue;
                Function.Call(N.EXPLODE_VEHICLE, v.Handle, true, false);
                if (++n >= 20) break;
            }
            if (n == 0) throw new CommandFailed("No vehicles nearby");
            return $"Exploded {n} vehicle{(n == 1 ? "" : "s")}";
        }

        string SpawnRamp()
        {
            Model model = LoadModel("prop_mp_ramp_03", "prop");
            Entity from = (Entity)Player.CurrentVehicle ?? Player;
            float ahead = from is Vehicle car ? 12f + car.Speed * 0.8f : 6f;
            Vector3 pos = from.Position + from.ForwardVector * ahead;
            Prop ramp = World.CreateProp(model, pos, false, true);
            model.MarkAsNoLongerNeeded();
            if (ramp == null) throw new CommandFailed("The ramp wouldn't spawn here");
            // Same way round as Chaos Mod's ramps: facing the way the player is going
            Function.Call(N.SET_ENTITY_ROTATION, ramp.Handle, 0f, 0f, from.Heading, 2, true);
            Function.Call(N.PLACE_OBJECT_ON_GROUND_PROPERLY, ramp.Handle);
            ramp.IsPositionFrozen = true;
            Expire(ramp, 90000, true);
            return "Ramp spawned";
        }

        string Tune(bool full)
        {
            Vehicle v = PlayerVehicle() ?? throw new CommandFailed("The player has no vehicle to tune");
            int h = v.Handle;
            Function.Call(N.SET_VEHICLE_MOD_KIT, h, 0);
            if (!full && (v.Model.IsCar))
                Function.Call(N.SET_VEHICLE_WHEEL_TYPE, h, _rng.Next(8) == 6 ? 0 : _rng.Next(8)); // 6 = bike wheels

            for (int type = 0; type <= 48; type++)
            {
                if (type >= 17 && type <= 22) continue; // toggles (turbo, xenon...) and tyre smoke, below
                int count = Function.Call<int>(N.GET_NUM_VEHICLE_MODS, h, type);
                if (count <= 0) continue;
                int index = full ? count - 1 : _rng.Next(-1, count);
                Function.Call(N.SET_VEHICLE_MOD, h, type, index, false);
            }
            Function.Call(N.TOGGLE_VEHICLE_MOD, h, 18, full || _rng.Next(2) == 0); // turbo
            Function.Call(N.TOGGLE_VEHICLE_MOD, h, 22, full || _rng.Next(2) == 0); // xenon lights
            Function.Call(N.TOGGLE_VEHICLE_MOD, h, 20, true);                      // tyre smoke
            Function.Call(N.SET_VEHICLE_TYRE_SMOKE_COLOR, h, _rng.Next(256), _rng.Next(256), _rng.Next(256));
            Function.Call(N.SET_VEHICLE_COLOURS, h, _rng.Next(160), _rng.Next(160));
            Function.Call(N.SET_VEHICLE_EXTRA_COLOURS, h, _rng.Next(160), _rng.Next(160));
            Function.Call(N.SET_VEHICLE_WINDOW_TINT, h, _rng.Next(7));
            bool neon = full || _rng.Next(2) == 0;
            for (int i = 0; i < 4; i++) Function.Call(N.SET_VEHICLE_NEON_ENABLED, h, i, neon);
            Function.Call(N.SET_VEHICLE_NEON_COLOUR, h, _rng.Next(256), _rng.Next(256), _rng.Next(256));
            if (full) Function.Call(N.SET_VEHICLE_TYRES_CAN_BURST, h, false);
            return full ? "Fully tuned" : "Randomly tuned";
        }

        // ---------------------------------------------------------------- weapons

        static uint WeaponHash(string name)
        {
            name = name.Trim().Replace(' ', '_').ToUpperInvariant();
            if (!name.StartsWith("WEAPON_") && !name.StartsWith("GADGET_")) name = "WEAPON_" + name;
            uint hash = unchecked((uint)Game.GenerateHash(name));
            if (!Function.Call<bool>(N.IS_WEAPON_VALID, hash)) throw new CommandFailed($"There's no weapon called '{name}'");
            return hash;
        }

        static string GiveWeapon(string name, int ammo)
        {
            uint hash = WeaponHash(name);
            Function.Call(N.GIVE_WEAPON_TO_PED, Player.Handle, hash, ammo, false, true);
            return "Gave " + name;
        }

        static string SetAmmo(int ammo)
        {
            uint current = Function.Call<uint>(N.GET_SELECTED_PED_WEAPON, Player.Handle);
            if (current == N.WEAPON_UNARMED) throw new CommandFailed("The player has no weapon in their hands");
            Function.Call(N.SET_PED_AMMO, Player.Handle, current, ammo, false);
            return "Ammo set to " + ammo;
        }

        // ---------------------------------------------------------------- world

        static string SetTime(int hour)
        {
            Function.Call(N.SET_CLOCK_TIME, hour, 0, 0);
            return $"Time set to {hour:00}:00";
        }

        static string SetWeather(string name)
        {
            string weather = Catalog.WeatherTypes.FirstOrDefault(w => w.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (weather == null) throw new CommandFailed($"Unknown weather '{name}'");
            Function.Call(N.CLEAR_OVERRIDE_WEATHER);
            Function.Call(N.SET_WEATHER_TYPE_NOW_PERSIST, weather);
            return "Weather: " + weather;
        }

        // ---------------------------------------------------------------- teleports

        class Place
        {
            public string Name;
            public Vector3 Pos;
            public Place(string name, float x, float y, float z) { Name = name; Pos = new Vector3(x, y, z); }
        }

        // The first four are the named teleports; tp_random picks from the whole list.
        static readonly Place[] Places =
        {
            new Place("LS Airport", -1336.0f, -3044.0f, 13.9f),
            new Place("Maze Bank top", -75.0f, -818.0f, 326.2f),
            new Place("Fort Zancudo", -2047.4f, 3132.1f, 32.8f),
            new Place("Mount Chiliad", 501.8f, 5604.8f, 797.9f),
            new Place("Vinewood sign", 711.4f, 1198.1f, 348.5f),
            new Place("Del Perro Pier", -1850.1f, -1231.7f, 13.0f),
            new Place("Sandy Shores airfield", 1747.0f, 3273.7f, 41.1f),
            new Place("Paleto Bay", -275.5f, 6635.8f, 7.5f),
            new Place("Grove Street", -43.8f, -1447.4f, 32.4f),
            new Place("Vespucci Beach", -1392.0f, -1567.0f, 2.2f),
            new Place("Alamo Sea", 1300.0f, 4220.0f, 33.9f),
            new Place("Humane Labs", 3614.4f, 3752.0f, 28.7f),
            new Place("Kortz Center", -2243.8f, 264.5f, 174.6f),
            new Place("Legion Square", 195.2f, -933.8f, 30.7f),
            new Place("Sandy Shores", 1961.0f, 3740.0f, 32.3f),
        };

        string TeleportTo(Place place)
        {
            MovePlayer(place.Pos);
            return "Teleported to " + place.Name;
        }

        string TeleportToWaypoint()
        {
            if (World.WaypointBlip == null) throw new CommandFailed("No waypoint is set on the map");
            Vector3 w = World.WaypointPosition;
            // Ground height is only known once the area has loaded: go high, wait for collision, then drop down
            MovePlayer(new Vector3(w.X, w.Y, 800f));
            float ground = 0f;
            for (int i = 0; i < 40 && ground <= 0f; i++)
            {
                Function.Call(N.REQUEST_COLLISION_AT_COORD, w.X, w.Y, 800f);
                Wait(50);
                ground = World.GetGroundHeight(new Vector3(w.X, w.Y, 1000f));
            }
            MovePlayer(new Vector3(w.X, w.Y, ground > 0f ? ground + 1f : 200f));
            return "Teleported to the waypoint";
        }

        // Takes the player's vehicle along when they're driving it.
        static void MovePlayer(Vector3 pos)
        {
            Ped ped = Player;
            Vehicle v = ped.CurrentVehicle;
            Function.Call(N.REQUEST_COLLISION_AT_COORD, pos.X, pos.Y, pos.Z);
            if (v != null && v.Driver == ped)
            {
                v.Position = pos;
                v.Velocity = Vector3.Zero;
            }
            else
            {
                if (v != null) Function.Call(N.CLEAR_PED_TASKS_IMMEDIATELY, ped.Handle);
                ped.Position = pos;
            }
        }

        // ---------------------------------------------------------------- fun

        string Skydive(int height)
        {
            Ped ped = Player;
            if (ped.CurrentVehicle != null) Function.Call(N.CLEAR_PED_TASKS_IMMEDIATELY, ped.Handle);
            Function.Call(N.GIVE_WEAPON_TO_PED, ped.Handle, N.GADGET_PARACHUTE, 1, false, false);
            ped.Position = ped.Position + new Vector3(0f, 0f, height);
            return $"Skydiving from {height} m up";
        }

        string LaunchUp()
        {
            Ped ped = Player;
            Vehicle v = ped.CurrentVehicle;
            if (v != null)
            {
                v.Velocity = v.Velocity + new Vector3(0f, 0f, 35f);
                return "Vehicle launched";
            }
            Function.Call(N.SET_PED_TO_RAGDOLL, ped.Handle, 5000, 5000, 0, false, false, false);
            Wait(0);
            ped.Velocity = new Vector3(0f, 0f, 35f);
            return "Player launched";
        }
    }
}
