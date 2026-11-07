namespace ProChunk.App.Items;

/// <summary>Eşya seçici için sık kullanılan kimlikler (Java ve Bedrock'ta ortak adlar).</summary>
public static class ItemCatalog
{
    private static readonly string Raw = """
        diamond_sword netherite_sword iron_sword golden_sword stone_sword wooden_sword mace trident bow crossbow arrow spectral_arrow shield
        diamond_pickaxe netherite_pickaxe iron_pickaxe stone_pickaxe wooden_pickaxe golden_pickaxe
        diamond_axe netherite_axe iron_axe stone_axe wooden_axe diamond_shovel netherite_shovel iron_shovel diamond_hoe netherite_hoe
        shears flint_and_steel fishing_rod compass clock spyglass brush lead name_tag saddle bucket water_bucket lava_bucket milk_bucket
        diamond_helmet diamond_chestplate diamond_leggings diamond_boots netherite_helmet netherite_chestplate netherite_leggings netherite_boots
        iron_helmet iron_chestplate iron_leggings iron_boots golden_helmet golden_chestplate golden_leggings golden_boots
        leather_helmet leather_chestplate leather_leggings leather_boots chainmail_helmet chainmail_chestplate chainmail_leggings chainmail_boots
        turtle_helmet elytra totem_of_undying
        diamond emerald netherite_ingot netherite_scrap iron_ingot gold_ingot copper_ingot coal charcoal redstone lapis_lazuli quartz amethyst_shard
        raw_iron raw_gold raw_copper iron_nugget gold_nugget stick string feather leather paper book enchanted_book writable_book bone bone_meal
        gunpowder blaze_rod blaze_powder ender_pearl ender_eye ghast_tear magma_cream slime_ball nether_star experience_bottle firework_rocket
        apple golden_apple enchanted_golden_apple bread cooked_beef cooked_porkchop cooked_chicken cooked_mutton cooked_salmon cooked_cod
        carrot golden_carrot potato baked_potato melon_slice cookie cake pumpkin_pie sweet_berries honey_bottle
        wheat wheat_seeds pumpkin_seeds melon_seeds beetroot_seeds sugar_cane bamboo cactus egg
        potion splash_potion lingering_potion glass_bottle
        stone cobblestone deepslate cobbled_deepslate dirt grass_block sand red_sand gravel clay andesite diorite granite tuff calcite
        oak_log spruce_log birch_log jungle_log acacia_log dark_oak_log mangrove_log cherry_log oak_planks spruce_planks birch_planks
        glass white_wool red_wool blue_wool black_wool bricks stone_bricks sandstone obsidian crying_obsidian netherrack end_stone bedrock
        diamond_block emerald_block gold_block iron_block netherite_block coal_block redstone_block lapis_block copper_block amethyst_block
        diamond_ore deepslate_diamond_ore iron_ore gold_ore coal_ore emerald_ore ancient_debris
        chest trapped_chest ender_chest barrel shulker_box white_shulker_box red_shulker_box blue_shulker_box black_shulker_box purple_shulker_box
        crafting_table furnace blast_furnace smoker anvil enchanting_table bookshelf brewing_stand cauldron hopper dispenser dropper observer
        piston sticky_piston lever redstone_torch repeater comparator tnt rail powered_rail
        torch lantern soul_torch glowstone sea_lantern beacon conduit respawn_anchor lodestone
        oak_door iron_door oak_trapdoor ladder scaffolding oak_sign white_bed
        spawner command_block structure_block barrier light
        """;

    public static readonly IReadOnlyList<string> All =
        Raw.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(s => "minecraft:" + s).Distinct().ToList();
}
