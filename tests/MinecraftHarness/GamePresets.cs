using GiftDeck.Models;
using static Preset;

// The events of the three GiftDeck Games presets: cheap gifts do small things (one sheep, a few sand blocks),
// expensive ones big things (a wall of TNT, 200 blocks of concrete powder). Each preset mixes gifts that hurt
// the streamer with a few that help, so viewers can pick a side. The streamer starts the game on the Games page.
static class GameRules
{
    const string U = "{user}";

    public static List<Rule> BedrockBox() => new List<Rule>
    {
        GiftRule("Sand down the shaft", "Rose", 5655, true, 10, Command("bb_sand", new { count = "4", block = "sand", viewer = U })),
        GiftRule("Haste (help)", "GG", 6064, false, 0, Command("bb_haste", new { seconds = "20", viewer = U })),
        GiftRule("Heal (help)", "Ice Cream Cone", 5827, false, 0, Command("bb_heal", new { viewer = U })),
        GiftRule("A chicken in the box", "Heart Me", 7934, true, 5, Command("bb_mob", new { mob = "chicken", count = "1", viewer = U })),
        GiftRule("One TNT", "Finger Heart", 5487, true, 5, Command("bb_tnt", new { count = "1", viewer = U })),
        GiftRule("Drill 3 down (help)", "Perfume", 5658, false, 0, Command("bb_drill", new { layers = "3", viewer = U })),
        GiftRule("Three zombies", "Doughnut", 5879, false, 0, Command("bb_mob", new { mob = "zombie", count = "3", viewer = U })),
        GiftRule("Diamond pickaxe (help)", "Sunglasses", 5509, false, 0, Command("bb_pickaxe", new { tier = "diamond", viewer = U })),
        GiftRule("Three anvils", "Corgi", 0, false, 0, Command("bb_anvil", new { count = "3", viewer = U })),
        GiftRule("TNT rain", "Money Gun", 0, false, 0, Command("bb_tnt", new { count = "8", viewer = U })),
        GiftRule("Creepers and gravel", "Galaxy", 11046, false, 0,
            Command("bb_mob", new { mob = "creeper", count = "5", viewer = U }),
            Command("bb_sand", new { count = "32", block = "gravel", viewer = U })),
        GiftRule("Netherite pickaxe and a drill (help)", "Glowing Jellyfish", 8972, false, 0,
            Command("bb_pickaxe", new { tier = "netherite", viewer = U }),
            Command("bb_drill", new { layers = "10", viewer = U })),
        GiftRule("Everything at once", "Lion", 0, false, 0,
            Command("bb_tnt", new { count = "20", viewer = U }),
            Command("bb_mob", new { mob = "vindicator", count = "5", viewer = U })),
        OtherRule("New follower: haste", TriggerType.Follow, Command("bb_haste", new { seconds = "10", viewer = U })),
        OtherRule("Stream shared: drill 1", TriggerType.Share, Command("bb_drill", new { layers = "1", viewer = U })),
    };

    public static List<Rule> SandPour() => new List<Rule>
    {
        GiftRule("One sand block", "Rose", 5655, true, 20, Command("sp_pour", new { count = "1", block = "sand", viewer = U })),
        GiftRule("Some gravel", "GG", 6064, false, 0, Command("sp_pour", new { count = "3", block = "gravel", viewer = U })),
        GiftRule("Heal (help)", "Ice Cream Cone", 5827, false, 0, Command("sp_heal", new { viewer = U })),
        GiftRule("Red sand", "Heart Me", 7934, true, 5, Command("sp_pour", new { count = "4", block = "red_sand", viewer = U })),
        GiftRule("A 3x3 of sand", "Finger Heart", 5487, true, 5, Command("sp_pour", new { count = "9", block = "sand", viewer = U })),
        GiftRule("Dig out (help)", "Perfume", 5658, false, 0, Command("sp_dig", new { viewer = U })),
        GiftRule("Concrete powder", "Doughnut", 5879, false, 0, Command("sp_pour", new { count = "25", block = "concrete", viewer = U })),
        GiftRule("Diamond shovel (help)", "Sunglasses", 5509, false, 0, Command("sp_shovel", new { tier = "diamond", viewer = U })),
        GiftRule("Two anvils", "Corgi", 0, false, 0, Command("sp_pour", new { count = "2", block = "anvil", viewer = U })),
        GiftRule("Gravel avalanche", "Money Gun", 0, false, 0, Command("sp_pour", new { count = "49", block = "gravel", viewer = U })),
        GiftRule("100 sand", "Galaxy", 11046, false, 0, Command("sp_pour", new { count = "100", block = "sand", viewer = U })),
        GiftRule("Anvil storm", "Glowing Jellyfish", 8972, false, 0, Command("sp_pour", new { count = "6", block = "anvil", viewer = U })),
        GiftRule("Buried alive", "Lion", 0, false, 0, Command("sp_pour", new { count = "200", block = "concrete", viewer = U })),
        OtherRule("New follower: haste", TriggerType.Follow, Command("sp_haste", new { seconds = "15", viewer = U })),
        OtherRule("Stream shared: dig out", TriggerType.Share, Command("sp_dig", new { viewer = U })),
    };

    public static List<Rule> SheepOut() => new List<Rule>
    {
        GiftRule("One sheep", "Rose", 5655, true, 20, Command("so_sheep", new { count = "1", color = "random", viewer = U })),
        GiftRule("A white sheep", "GG", 6064, false, 0, Command("so_sheep", new { count = "1", color = "white", viewer = U })),
        GiftRule("Heal (help)", "Ice Cream Cone", 5827, false, 0, Command("so_heal", new { viewer = U })),
        GiftRule("Two pink sheep", "Heart Me", 7934, true, 5, Command("so_sheep", new { count = "2", color = "pink", viewer = U })),
        GiftRule("Three sheep", "Finger Heart", 5487, true, 5, Command("so_sheep", new { count = "3", color = "random", viewer = U })),
        GiftRule("Lightning on 3 sheep (help)", "Perfume", 5658, false, 0, Command("so_smite", new { count = "3", viewer = U })),
        GiftRule("Five rainbow sheep", "Doughnut", 5879, false, 0, Command("so_sheep", new { count = "5", color = "rainbow", viewer = U })),
        GiftRule("Diamond sword (help)", "Sunglasses", 5509, false, 0, Command("so_sword", new { tier = "diamond", viewer = U })),
        GiftRule("Ten black sheep", "Corgi", 0, false, 0, Command("so_sheep", new { count = "10", color = "black", viewer = U })),
        GiftRule("Lightning on 15 sheep (help)", "Money Gun", 0, false, 0, Command("so_smite", new { count = "15", viewer = U })),
        GiftRule("Twenty sheep", "Galaxy", 11046, false, 0, Command("so_sheep", new { count = "20", color = "random", viewer = U })),
        GiftRule("Netherite sword and a storm (help)", "Glowing Jellyfish", 8972, false, 0,
            Command("so_sword", new { tier = "netherite", viewer = U }),
            Command("so_smite", new { count = "30", viewer = U })),
        GiftRule("Thirty rainbow sheep", "Lion", 0, false, 0, Command("so_sheep", new { count = "30", color = "rainbow", viewer = U })),
        OtherRule("New follower: a sheep", TriggerType.Follow, Command("so_sheep", new { count = "1", color = "random", viewer = U })),
        OtherRule("Stream shared: lightning on 2", TriggerType.Share, Command("so_smite", new { count = "2", viewer = U })),
    };
}
