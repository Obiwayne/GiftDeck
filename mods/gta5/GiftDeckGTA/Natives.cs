using GTA.Native;

namespace GiftDeckGTA
{
    // Raw native hashes, so the script works the same on SHVDN 3.6 and the 3.7 nightlies (their wrapper
    // names and enums have changed between builds; the natives haven't).
    internal static class N
    {
        // UI and sound
        public const Hash PLAY_SOUND_FRONTEND = (Hash)0x67C540AA08E4A6F5;
        public const Hash DISABLE_ALL_CONTROL_ACTIONS = (Hash)0x5F4B6931816E599B;
        public const Hash BEGIN_TEXT_COMMAND_THEFEED_POST = (Hash)0x202709F4C58A0424;
        public const Hash ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME = (Hash)0x6C188BE134E074AA;
        public const Hash END_TEXT_COMMAND_THEFEED_POST_TICKER = (Hash)0x2ED7843F8F801023;

        // Player
        public const Hash SET_PLAYER_WANTED_LEVEL = (Hash)0x39FF19C64EF7DA5B;
        public const Hash SET_PLAYER_WANTED_LEVEL_NOW = (Hash)0xE0A7D1E497FFCD6F;
        public const Hash GET_PLAYER_WANTED_LEVEL = (Hash)0xE28E54788CE8F12D;
        public const Hash GET_PLAYER_MAX_ARMOUR = (Hash)0x92659B4CE1863CB3;
        public const Hash CLEAR_PED_TASKS_IMMEDIATELY = (Hash)0xAAA34F8A7CB32098;
        public const Hash SET_PED_TO_RAGDOLL = (Hash)0xAE99FB955581844A;
        public const Hash REQUEST_COLLISION_AT_COORD = (Hash)0x07503F7948F491A7;

        // Clothing
        public const Hash GET_NUMBER_OF_PED_DRAWABLE_VARIATIONS = (Hash)0x27561561732A7842;
        public const Hash GET_NUMBER_OF_PED_TEXTURE_VARIATIONS = (Hash)0x8F7156A3142A6BAD;
        public const Hash IS_PED_COMPONENT_VARIATION_VALID = (Hash)0xE825F6B6CEA7671D;
        public const Hash SET_PED_COMPONENT_VARIATION = (Hash)0x262B14F48D29DE80;
        public const Hash SET_PED_RANDOM_PROPS = (Hash)0xC44AA05345C992C6;
        public const Hash CLEAR_ALL_PED_PROPS = (Hash)0xCD8A7537A9B52F06;

        // Weapons
        public const Hash GIVE_WEAPON_TO_PED = (Hash)0xBF0FD6E56C964FCB;
        public const Hash SET_PED_AMMO = (Hash)0x14E56BC5B5DB6A19;
        public const Hash GET_SELECTED_PED_WEAPON = (Hash)0x0A6DB4965674D243;
        public const Hash REMOVE_ALL_PED_WEAPONS = (Hash)0xF25DF915FA38C5F3;
        public const Hash IS_WEAPON_VALID = (Hash)0x937C71165CF334B3;

        // Peds
        public const Hash ADD_RELATIONSHIP_GROUP = (Hash)0xF372BC22FCB88606;
        public const Hash SET_RELATIONSHIP_BETWEEN_GROUPS = (Hash)0xBF25EB89375A37AD;
        public const Hash SET_PED_RELATIONSHIP_GROUP_HASH = (Hash)0xC80A74AC829DDD92;
        public const Hash SET_PED_COMBAT_ATTRIBUTES = (Hash)0x9F7794730795E019;
        public const Hash SET_PED_HEARING_RANGE = (Hash)0x33A8F7F7D5F7F33C;
        public const Hash SET_PED_CONFIG_FLAG = (Hash)0x1913FE4CBF41C463;
        public const Hash SET_PED_KEEP_TASK = (Hash)0x971D38760FBC02EF;
        public const Hash SET_BLOCKING_OF_NON_TEMPORARY_EVENTS = (Hash)0x9F8AA94D6D97DBF4;
        public const Hash TASK_COMBAT_PED = (Hash)0xF166E48407BAC484;
        public const Hash TASK_WANDER_STANDARD = (Hash)0xBB9CE077274F6A1B;
        public const Hash SET_PED_INTO_VEHICLE = (Hash)0xF75B0D629E1C063D;

        // Vehicles
        public const Hash EXPLODE_VEHICLE = (Hash)0xBA71116ADF5B514C;
        public const Hash SET_VEHICLE_ENGINE_ON = (Hash)0x2497C4717C8B881E;
        public const Hash SET_VEHICLE_FORWARD_SPEED = (Hash)0xAB54A438726D25D5;
        public const Hash SET_VEHICLE_SIREN = (Hash)0xF4924635A19EB37D;
        public const Hash SET_VEHICLE_DIRT_LEVEL = (Hash)0x79D3B596FE44EE8B;
        public const Hash SET_VEHICLE_MOD_KIT = (Hash)0x1F2AA07F00B3217A;
        public const Hash GET_NUM_VEHICLE_MODS = (Hash)0xE38E9162A2500646;
        public const Hash SET_VEHICLE_MOD = (Hash)0x6AF0636DDEDCB6DD;
        public const Hash TOGGLE_VEHICLE_MOD = (Hash)0x2A1F4F37F95BAD08;
        public const Hash SET_VEHICLE_WHEEL_TYPE = (Hash)0x487EB21CC7295BA1;
        public const Hash SET_VEHICLE_COLOURS = (Hash)0x4F1D4BE3A7F24601;
        public const Hash SET_VEHICLE_EXTRA_COLOURS = (Hash)0x2036F561ADD12E33;
        public const Hash SET_VEHICLE_WINDOW_TINT = (Hash)0x57C51E6BAD752696;
        public const Hash SET_VEHICLE_NEON_ENABLED = (Hash)0x2AA720E4287BF269;
        public const Hash SET_VEHICLE_NEON_COLOUR = (Hash)0x8E0A582209A62695;
        public const Hash SET_VEHICLE_TYRE_SMOKE_COLOR = (Hash)0xB5BA80F839791C0F;
        public const Hash SET_VEHICLE_TYRES_CAN_BURST = (Hash)0xEB9DC3C7D8596C46;

        // Props
        public const Hash PLACE_OBJECT_ON_GROUND_PROPERLY = (Hash)0x58A850EAEE20FAA3;
        public const Hash SET_ENTITY_ROTATION = (Hash)0x8524A8B0171D5E07;

        // World and screen
        public const Hash SET_CLOCK_TIME = (Hash)0x47C3B5848C3E45D8;
        public const Hash SET_WEATHER_TYPE_NOW_PERSIST = (Hash)0xED712CA327900C8A;
        public const Hash CLEAR_OVERRIDE_WEATHER = (Hash)0x338D2E3477711050;
        public const Hash SHAKE_GAMEPLAY_CAM = (Hash)0xFD55E49555E017CF;
        public const Hash STOP_GAMEPLAY_CAM_SHAKING = (Hash)0x0EF93E9F3D08C178;
        public const Hash SET_TIMECYCLE_MODIFIER = (Hash)0x2C933ABF17A1DF41;
        public const Hash SET_TIMECYCLE_MODIFIER_STRENGTH = (Hash)0x82E7FFCD5B2326B3;
        public const Hash CLEAR_TIMECYCLE_MODIFIER = (Hash)0x0F07E7745A236711;

        // Drunk
        public const Hash SET_PED_IS_DRUNK = (Hash)0x95D2D383D5396B8A;
        public const Hash REQUEST_CLIP_SET = (Hash)0xD2A71E1A77418A49;
        public const Hash HAS_CLIP_SET_LOADED = (Hash)0x318234F4F3738AF3;
        public const Hash SET_PED_MOVEMENT_CLIPSET = (Hash)0xAF8A94EDE7712BEF;
        public const Hash RESET_PED_MOVEMENT_CLIPSET = (Hash)0xAA74EC0CB0AAEA2C;

        public const uint WEAPON_UNARMED = 0xA2719263;
        public const uint GADGET_PARACHUTE = 0xFBAB5776;
    }
}
