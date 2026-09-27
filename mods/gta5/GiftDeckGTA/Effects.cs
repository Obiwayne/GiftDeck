using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GiftDeckGTA
{
    // Commands that last a while or leave things in the world: attackers, animals, the jail cage, drunk,
    // earthquake. They're kept track of here so they can be ended on time and cleaned up when the script stops.
    public partial class GiftDeckScript
    {
        static readonly string[] RandomAnimals =
        {
            "a_c_boar", "a_c_cat_01", "a_c_chickenhawk", "a_c_chimp", "a_c_cow", "a_c_coyote", "a_c_crow", "a_c_deer",
            "a_c_hen", "a_c_husky", "a_c_mtlion", "a_c_pig", "a_c_pigeon", "a_c_poodle", "a_c_pug", "a_c_rabbit_01",
            "a_c_rat", "a_c_retriever", "a_c_rhesus", "a_c_rottweiler", "a_c_seagull", "a_c_shepherd", "a_c_westy",
        };
        static readonly string[] AttackerModels = { "g_m_y_ballaorig_01", "g_m_y_mexgoon_01", "g_m_y_lost_01", "g_m_y_salvagoon_01", "g_m_m_chigoon_01" };

        class Expiring
        {
            public Entity Entity;
            public int Until;
            public bool Delete; // false = just let the game clean it up
        }

        readonly List<Expiring> _expiring = new List<Expiring>();
        readonly List<Ped> _hostiles = new List<Ped>();
        readonly List<Vehicle> _hostileVehicles = new List<Vehicle>();
        int _hostileGroup;

        int _drunkUntil;
        bool _drunkWalkSet;
        const string DrunkClipSet = "move_m@drunk@verydrunk";

        int _quakeUntil, _nextQuakeJolt;

        Prop _cage;
        int _cageUntil;

        void Expire(Entity e, int ms, bool delete) => _expiring.Add(new Expiring { Entity = e, Until = Now + ms, Delete = delete });

        void UpdateEffects(Ped ped, int now)
        {
            for (int i = _expiring.Count - 1; i >= 0; i--)
            {
                var x = _expiring[i];
                if (now < x.Until && x.Entity.Exists()) continue;
                Release(x.Entity, x.Delete);
                _expiring.RemoveAt(i);
            }

            // Dead attackers: let the game take the bodies away
            for (int i = _hostiles.Count - 1; i >= 0; i--)
            {
                Ped p = _hostiles[i];
                if (p.Exists() && !p.IsDead) continue;
                if (p.Exists())
                {
                    p.AttachedBlip?.Delete();
                    p.MarkAsNoLongerNeeded();
                }
                _hostiles.RemoveAt(i);
            }

            if (_drunkUntil != 0)
            {
                if (now >= _drunkUntil || ped.IsDead) StopDrunk();
                else if (!_drunkWalkSet && Function.Call<bool>(N.HAS_CLIP_SET_LOADED, DrunkClipSet))
                {
                    Function.Call(N.SET_PED_MOVEMENT_CLIPSET, ped.Handle, DrunkClipSet, 1.0f);
                    _drunkWalkSet = true;
                }
            }

            if (_quakeUntil != 0)
            {
                if (now >= _quakeUntil) StopEarthquake();
                else if (now >= _nextQuakeJolt)
                {
                    _nextQuakeJolt = now + 700;
                    Function.Call(N.SHAKE_GAMEPLAY_CAM, "SMALL_EXPLOSION_SHAKE", 0.6f);
                    foreach (Vehicle v in World.GetNearbyVehicles(ped, 60f))
                        if (v != null && v.Exists() && _rng.Next(3) == 0)
                            v.Velocity = v.Velocity + new Vector3(Rand(-1.5f, 1.5f), Rand(-1.5f, 1.5f), Rand(1.5f, 3.5f));
                }
            }

            if (_cage != null && (now >= _cageUntil || !_cage.Exists()))
            {
                if (_cage.Exists()) _cage.Delete();
                _cage = null;
                Notify("~g~Out of jail!");
            }
        }

        static void Release(Entity e, bool delete)
        {
            if (e == null || !e.Exists()) return;
            if (delete) e.Delete();
            else e.MarkAsNoLongerNeeded();
        }

        void CleanUpEffects()
        {
            try
            {
                foreach (var x in _expiring) Release(x.Entity, x.Delete);
                _expiring.Clear();
                RemoveAttackers();
                if (_cage != null && _cage.Exists()) _cage.Delete();
                _cage = null;
                if (_drunkUntil != 0) StopDrunk();
                if (_quakeUntil != 0) StopEarthquake();
            }
            catch (Exception ex) { Logger.Write("Clean-up: " + ex.Message); }
        }

        // ---------------------------------------------------------------- attackers

        int HostileGroup()
        {
            if (_hostileGroup != 0) return _hostileGroup;
            var result = new OutputArgument();
            Function.Call(N.ADD_RELATIONSHIP_GROUP, "GIFTDECK_HOSTILE", result);
            _hostileGroup = result.GetResult<int>();
            int player = Game.GenerateHash("PLAYER");
            Function.Call(N.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _hostileGroup, player); // 5 = hate
            Function.Call(N.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, player, _hostileGroup);
            Function.Call(N.SET_RELATIONSHIP_BETWEEN_GROUPS, 1, _hostileGroup, _hostileGroup); // 1 = respect: they don't fight each other
            return _hostileGroup;
        }

        static uint AttackerWeapon(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "none": return 0;
                case "smg": return WeaponHash("microsmg");
                case "rifle": return WeaponHash("assaultrifle");
                case "shotgun": return WeaponHash("pumpshotgun");
                case "knife": return WeaponHash("knife");
                case "pistol": return WeaponHash("pistol");
                default: return WeaponHash(name); // any weapon name works too
            }
        }

        // Makes a ped hate the player and go after them, with a red blip so the streamer can see them coming.
        Ped MakeHostile(Ped p, uint weapon)
        {
            int h = p.Handle;
            Function.Call(N.SET_PED_RELATIONSHIP_GROUP_HASH, h, HostileGroup());
            Function.Call(N.SET_PED_HEARING_RANGE, h, 9999f);
            Function.Call(N.SET_PED_CONFIG_FLAG, h, 281, true); // don't writhe on the floor when hurt
            Function.Call(N.SET_PED_COMBAT_ATTRIBUTES, h, 5, true);  // fight armed peds when unarmed
            Function.Call(N.SET_PED_COMBAT_ATTRIBUTES, h, 46, true); // always fight
            if (weapon != 0) Function.Call(N.GIVE_WEAPON_TO_PED, h, weapon, 9999, false, true);
            Function.Call(N.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, h, false);
            Function.Call(N.TASK_COMBAT_PED, h, Player.Handle, 0, 16);
            Function.Call(N.SET_PED_KEEP_TASK, h, true);
            var blip = p.AddBlip();
            if (blip != null)
            {
                blip.Color = BlipColor.Red;
                blip.Scale = 0.7f;
            }
            _hostiles.Add(p);
            return p;
        }

        string SpawnAttackers(int count, string weaponName)
        {
            uint weapon = AttackerWeapon(weaponName);
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                Model model = LoadModel(Pick(AttackerModels), "ped");
                Ped p = World.CreatePed(model, NearPlayer(18f, 30f), 0f);
                model.MarkAsNoLongerNeeded();
                if (p == null) continue;
                p.Accuracy = 25;
                MakeHostile(p, weapon);
                made++;
            }
            if (made == 0) throw new CommandFailed("No attackers could be spawned here");
            return $"{made} attacker{(made == 1 ? "" : "s")} on the way";
        }

        string ArmAttackers(string weaponName)
        {
            uint weapon = AttackerWeapon(weaponName);
            if (weapon == 0) throw new CommandFailed("Pick a weapon to arm them with");
            int n = 0;
            foreach (Ped p in _hostiles)
            {
                if (!p.Exists() || p.IsDead) continue;
                Function.Call(N.GIVE_WEAPON_TO_PED, p.Handle, weapon, 9999, false, true);
                n++;
            }
            if (n == 0) throw new CommandFailed("There are no attackers to arm");
            return $"Armed {n} attacker{(n == 1 ? "" : "s")}";
        }

        string RemoveAttackers()
        {
            int n = 0;
            foreach (Ped p in _hostiles)
            {
                if (!p.Exists()) continue;
                p.AttachedBlip?.Delete();
                p.Delete();
                n++;
            }
            _hostiles.Clear();
            foreach (Vehicle v in _hostileVehicles)
                if (v.Exists() && !Player.IsInVehicle(v)) v.Delete();
            _hostileVehicles.Clear();
            return n == 0 ? "No attackers to remove" : $"Removed {n} attacker{(n == 1 ? "" : "s")}";
        }

        string SpawnMotos(int count, bool cops)
        {
            string bikeName = cops ? "policeb" : Pick(new[] { "sanchez", "bati", "hexer", "daemon" });
            string pedName = cops ? "s_m_y_cop_01" : "g_m_y_lost_01";
            Model bike = LoadModel(bikeName, "vehicle");
            Model rider = LoadModel(pedName, "ped");
            uint weapon = WeaponHash(cops ? "pistol" : "microsmg");
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = RoadBehind(60f + i * 8f);
                float heading = Player.Heading;
                Vehicle v = World.CreateVehicle(bike, pos, heading);
                if (v == null) continue;
                v.PlaceOnGround();
                Ped p = v.CreatePedOnSeat(VehicleSeat.Driver, rider);
                if (p == null)
                {
                    v.Delete();
                    continue;
                }
                if (cops) Function.Call(N.SET_VEHICLE_SIREN, v.Handle, true);
                Function.Call(N.SET_VEHICLE_ENGINE_ON, v.Handle, true, true, false);
                p.Accuracy = 20;
                MakeHostile(p, weapon);
                _hostileVehicles.Add(v);
                made++;
            }
            bike.MarkAsNoLongerNeeded();
            rider.MarkAsNoLongerNeeded();
            if (made == 0) throw new CommandFailed("No bikes could be spawned here");
            return $"{made} {(cops ? "moto cop" : "moto bandit")}{(made == 1 ? "" : "s")} coming";
        }

        string SpawnMonkeys(int count)
        {
            Model model = LoadModel("a_c_chimp", "ped");
            // A melee weapon: animals can't aim guns. Chaos Mod's angry chimp uses the same.
            uint weapon = WeaponHash("stone_hatchet");
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                Ped p = World.CreatePed(model, NearPlayer(8f, 16f), 0f);
                if (p == null) continue;
                p.MaxHealth = 300;
                p.Health = 300;
                MakeHostile(p, weapon);
                made++;
            }
            model.MarkAsNoLongerNeeded();
            if (made == 0) throw new CommandFailed("No monkeys could be spawned here");
            return $"{made} killer monkey{(made == 1 ? "" : "s")}";
        }

        string SpawnAnimals(string[] models, int count)
        {
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                Model model = LoadModel(Pick(models), "animal");
                Ped p = World.CreatePed(model, NearPlayer(3f, 10f), Rand(0f, 360f));
                model.MarkAsNoLongerNeeded();
                if (p == null) continue;
                Function.Call(N.TASK_WANDER_STANDARD, p.Handle, 10f, 10);
                Expire(p, 120000, false);
                made++;
            }
            if (made == 0) throw new CommandFailed("No animals could be spawned here");
            return $"{made} animal{(made == 1 ? "" : "s")} spawned";
        }

        // ---------------------------------------------------------------- jail

        string Jail(int seconds)
        {
            Ped ped = Player;
            if (ped.CurrentVehicle != null) Function.Call(N.CLEAR_PED_TASKS_IMMEDIATELY, ped.Handle);
            if (_cage != null && _cage.Exists())
            {
                _cageUntil = Now + seconds * 1000; // already locked up: start the sentence again
                return $"Jail time reset to {seconds} s";
            }
            // The heist gold cage fits over a standing ped
            Model model = LoadModel("prop_gold_cont_01", "prop");
            Vector3 pos = ped.Position;
            float ground = World.GetGroundHeight(pos + new Vector3(0f, 0f, 2f));
            var at = new Vector3(pos.X, pos.Y, ground > 0f && Math.Abs(ground - pos.Z) < 3f ? ground : pos.Z - 1f);
            ped.Velocity = Vector3.Zero;
            _cage = World.CreateProp(model, at, false, false);
            model.MarkAsNoLongerNeeded();
            if (_cage == null) throw new CommandFailed("The cage wouldn't spawn here");
            _cage.Heading = ped.Heading;
            _cage.IsPositionFrozen = true;
            _cageUntil = Now + seconds * 1000;
            return $"Jailed for {seconds} s";
        }

        // ---------------------------------------------------------------- drunk and earthquake

        string StartDrunk(int seconds)
        {
            Ped ped = Player;
            bool already = _drunkUntil != 0;
            _drunkUntil = Now + seconds * 1000;
            if (already) return $"Still drunk, {seconds} s more";
            Function.Call(N.REQUEST_CLIP_SET, DrunkClipSet);
            Function.Call(N.SET_PED_IS_DRUNK, ped.Handle, true);
            Function.Call(N.SHAKE_GAMEPLAY_CAM, "DRUNK_SHAKE", 2.0f);
            Function.Call(N.SET_TIMECYCLE_MODIFIER, "Drunk");
            Function.Call(N.SET_TIMECYCLE_MODIFIER_STRENGTH, 1.0f);
            _drunkWalkSet = false;
            return $"Drunk for {seconds} s";
        }

        void StopDrunk()
        {
            Ped ped = Player;
            _drunkUntil = 0;
            _drunkWalkSet = false;
            if (ped != null && ped.Exists())
            {
                Function.Call(N.SET_PED_IS_DRUNK, ped.Handle, false);
                Function.Call(N.RESET_PED_MOVEMENT_CLIPSET, ped.Handle, 1.0f);
            }
            Function.Call(N.STOP_GAMEPLAY_CAM_SHAKING, true);
            Function.Call(N.CLEAR_TIMECYCLE_MODIFIER);
        }

        string StartEarthquake(int seconds)
        {
            _quakeUntil = Now + seconds * 1000;
            _nextQuakeJolt = 0;
            return $"Earthquake for {seconds} s";
        }

        void StopEarthquake()
        {
            _quakeUntil = 0;
            // Don't cut the drunk wobble short if both were running
            if (_drunkUntil == 0) Function.Call(N.STOP_GAMEPLAY_CAM_SHAKING, false);
        }
    }
}
