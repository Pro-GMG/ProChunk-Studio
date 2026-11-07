namespace ProChunk.Core;

/// <summary>Blok adından harita rengi (0xAARRGGBB) üretir.</summary>
public static class BlockColors
{
    private static readonly Dictionary<string, uint> Known = new(StringComparer.Ordinal)
    {
        ["grass_block"] = 0xFF7CBD4B, ["dirt"] = 0xFF866043, ["coarse_dirt"] = 0xFF77553B,
        ["podzol"] = 0xFF5B3F1E, ["mycelium"] = 0xFF6F6265, ["dirt_path"] = 0xFF94793F,
        ["farmland"] = 0xFF5A3A1E, ["mud"] = 0xFF3C3A3D, ["moss_block"] = 0xFF5A7A2D,
        ["stone"] = 0xFF7D7D7D, ["cobblestone"] = 0xFF7A7A7A, ["deepslate"] = 0xFF505055,
        ["granite"] = 0xFF9A6B57, ["diorite"] = 0xFFBDBDBD, ["andesite"] = 0xFF888889,
        ["tuff"] = 0xFF6C6D66, ["calcite"] = 0xFFDFE0DC, ["gravel"] = 0xFF857F7E,
        ["sand"] = 0xFFDBCFA3, ["red_sand"] = 0xFFBE6621, ["sandstone"] = 0xFFD8CB9B,
        ["red_sandstone"] = 0xFFB5621F, ["clay"] = 0xFFA0A6B3, ["terracotta"] = 0xFF985E43,
        ["water"] = 0xFF3F76E4, ["lava"] = 0xFFCF5B13, ["flowing_lava"] = 0xFFCF5B13,
        ["grass"] = 0xFF7CBD4B, ["grass_path"] = 0xFF94793F, ["tallgrass"] = 0xFF6FA840, ["reeds"] = 0xFF94C065, ["ice"] = 0xFF91B7FD,
        ["packed_ice"] = 0xFF8DB4FA, ["blue_ice"] = 0xFF74A8FD, ["snow"] = 0xFFF5FBFB,
        ["snow_block"] = 0xFFF5FBFB, ["powder_snow"] = 0xFFF7FDFD,
        ["netherrack"] = 0xFF6F3634, ["soul_sand"] = 0xFF51402F, ["soul_soil"] = 0xFF4B3A2D,
        ["basalt"] = 0xFF505052, ["blackstone"] = 0xFF2A2329, ["crimson_nylium"] = 0xFF831F1F,
        ["warped_nylium"] = 0xFF2B7265, ["magma_block"] = 0xFF8E3F1F, ["glowstone"] = 0xFFAB8654,
        ["end_stone"] = 0xFFDBDE9E, ["obsidian"] = 0xFF0F0B19, ["bedrock"] = 0xFF555555,
        ["short_grass"] = 0xFF6FA840, ["tall_grass"] = 0xFF6FA840, ["fern"] = 0xFF5E8F38,
        ["sugar_cane"] = 0xFF94C065, ["cactus"] = 0xFF587F2A, ["pumpkin"] = 0xFFC67718,
        ["melon"] = 0xFF71921D, ["hay_block"] = 0xFFA68B0C, ["bookshelf"] = 0xFF6B5839,
        ["glass"] = 0xFFC0F5FE, ["bricks"] = 0xFF966153, ["mossy_cobblestone"] = 0xFF6E775F,
        ["stone_bricks"] = 0xFF7A7979, ["chest"] = 0xFF9C6E27, ["torch"] = 0xFFFFD866,
    };

    private static readonly (string Prefix, uint Color)[] Dyes =
    {
        ("light_blue_", 0xFF3AAFD9), ("light_gray_", 0xFF8E8E86), ("white_", 0xFFE9ECEC),
        ("orange_", 0xFFF07613), ("magenta_", 0xFFBD44B3), ("yellow_", 0xFFF8C527),
        ("lime_", 0xFF70B919), ("pink_", 0xFFED8DAC), ("gray_", 0xFF3E4447),
        ("cyan_", 0xFF158991), ("purple_", 0xFF792AAC), ("blue_", 0xFF35399D),
        ("brown_", 0xFF724728), ("green_", 0xFF546D1B), ("red_", 0xFFA12722), ("black_", 0xFF141519),
    };

    public static bool IsAir(string state) =>
        BlockState.BaseName(state) is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";

    public static bool IsWater(string state) =>
        BlockState.BaseName(state) is "minecraft:water" or "minecraft:flowing_water" or "minecraft:bubble_column" or "minecraft:kelp" or "minecraft:kelp_plant"
            or "minecraft:seagrass" or "minecraft:tall_seagrass";

    public static uint Get(string state)
    {
        var name = BlockState.BaseName(state);
        var n = name.StartsWith("minecraft:", StringComparison.Ordinal) ? name[10..] : name;
        if (Known.TryGetValue(n, out var c)) return c;

        foreach (var (prefix, color) in Dyes)
            if (n.StartsWith(prefix, StringComparison.Ordinal)) return color;

        if (n.Contains("leaves")) return 0xFF48802A;
        if (n.Contains("log") || n.Contains("wood") || n.Contains("stem")) return 0xFF6B5132;
        if (n.Contains("planks") || n.Contains("slab") || n.Contains("stairs") || n.Contains("fence")) return 0xFFA2834F;
        if (n.Contains("deepslate")) return 0xFF505055;
        if (n.Contains("stone") || n.Contains("ore")) return 0xFF7D7D7D;
        if (n.Contains("sand")) return 0xFFDBCFA3;
        if (n.Contains("snow")) return 0xFFF5FBFB;
        if (n.Contains("flower") || n.Contains("tulip") || n.Contains("poppy") || n.Contains("dandelion")) return 0xFFC94F4F;
        if (n.Contains("copper")) return 0xFFC06B4F;

        // Bilinmeyen: isimden türetilmiş sabit, yumuşak bir renk
        uint h = 2166136261;
        foreach (char ch in n) h = (h ^ ch) * 16777619;
        return 0xFF000000 | ((0x50 + (h & 0x7F)) << 16) | ((0x50 + ((h >> 8) & 0x7F)) << 8) | (0x50 + ((h >> 16) & 0x7F));
    }
}
