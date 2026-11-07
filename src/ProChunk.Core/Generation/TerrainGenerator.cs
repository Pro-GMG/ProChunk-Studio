namespace ProChunk.Core.Generation;

public enum TerrainKind { Flat, Void, Ocean, Hills, Mountains }

/// <summary>Şablonlu chunk üretimi ayarları.</summary>
public sealed class TerrainOptions
{
    public TerrainKind Kind { get; set; } = TerrainKind.Flat;
    /// <summary>Düz dünya katmanları (alttan üste): blok, kalınlık.</summary>
    public List<(string Block, int Thickness)> Layers { get; set; } = new()
    {
        ("minecraft:bedrock", 1), ("minecraft:dirt", 2), ("minecraft:grass_block", 1),
    };
    public int BaseHeight { get; set; } = 64;
    public int SeaLevel { get; set; } = 62;
    public int Amplitude { get; set; } = 18;
    public long Seed { get; set; } = 12345;
    public bool Trees { get; set; } = true;
}

/// <summary>Basit, deterministik arazi üretici (düz, boş, okyanus, tepeler, dağlar).</summary>
public static class TerrainGenerator
{
    public static void Fill(ChunkVolume v, TerrainOptions o)
    {
        foreach (var s in v.Sections.Values) Array.Clear(s.Blocks);
        foreach (var s in v.Sections.Values) { s.Palette.Clear(); s.Palette.Add(BlockState.Air); }

        int minY = v.MinY;
        switch (o.Kind)
        {
            case TerrainKind.Void:
                return;

            case TerrainKind.Flat:
                int y = minY;
                foreach (var (block, n) in o.Layers)
                    for (int i = 0; i < n && y <= v.MaxY; i++, y++)
                        FillLayer(v, y, BlockState.Normalize(block));
                return;

            case TerrainKind.Ocean:
                FillLayer(v, minY, "minecraft:bedrock");
                for (int yy = minY + 1; yy < o.SeaLevel - 20; yy++) FillLayer(v, yy, "minecraft:stone");
                for (int yy = o.SeaLevel - 20; yy < o.SeaLevel - 16; yy++) FillLayer(v, yy, "minecraft:sand");
                for (int yy = o.SeaLevel - 16; yy <= o.SeaLevel; yy++) FillLayer(v, yy, "minecraft:water");
                return;

            default:
                Terrain(v, o);
                return;
        }
    }

    private static void FillLayer(ChunkVolume v, int y, string block)
    {
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                v.SetBlock(x, y, z, block);
    }

    private static void Terrain(ChunkVolume v, TerrainOptions o)
    {
        double scale = o.Kind == TerrainKind.Mountains ? 1 / 90.0 : 1 / 60.0;
        int amp = o.Kind == TerrainKind.Mountains ? o.Amplitude * 3 : o.Amplitude;
        var heights = new int[16, 16];

        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int wx = v.X * 16 + x, wz = v.Z * 16 + z;
            double n = Fbm(o.Seed, wx * scale, wz * scale, 4);
            if (o.Kind == TerrainKind.Mountains) n = Math.Sign(n) * Math.Pow(Math.Abs(n), 0.8) * 1.2;
            int h = Math.Clamp(o.BaseHeight + (int)(n * amp), v.MinY + 2, v.MaxY - 8);
            heights[x, z] = h;

            v.SetBlock(x, v.MinY, z, "minecraft:bedrock");
            for (int y = v.MinY + 1; y <= h; y++)
            {
                string b;
                if (y < h - 4) b = y < 0 ? "minecraft:deepslate" : "minecraft:stone";
                else if (h <= o.SeaLevel + 1) b = "minecraft:sand";
                else if (o.Kind == TerrainKind.Mountains && h > o.BaseHeight + amp * 0.6) b = y == h ? "minecraft:snow_block" : "minecraft:stone";
                else b = y == h ? "minecraft:grass_block" : "minecraft:dirt";
                v.SetBlock(x, y, z, b);
            }
            for (int y = h + 1; y <= o.SeaLevel; y++) v.SetBlock(x, y, z, "minecraft:water");
        }

        if (!o.Trees) return;
        var rng = new Random(HashCode.Combine(o.Seed, v.X, v.Z));
        int trees = rng.Next(0, 3);
        for (int t = 0; t < trees; t++)
        {
            int x = rng.Next(2, 14), z = rng.Next(2, 14);
            int h = heights[x, z];
            if (h <= o.SeaLevel + 1 || v.GetBlock(x, h, z) != "minecraft:grass_block") continue;
            int trunk = rng.Next(4, 7);
            for (int dy = -2; dy <= 1; dy++)
            {
                int r = dy >= 0 ? 1 : 2;
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                    if (Math.Abs(dx) + Math.Abs(dz) < r * 2 || dy < 0)
                        v.SetBlock(x + dx, h + trunk + dy, z + dz, "minecraft:oak_leaves[persistent=true]");
            }
            v.SetBlock(x, h, z, "minecraft:dirt");
            for (int dy = 1; dy <= trunk; dy++) v.SetBlock(x, h + dy, z, "minecraft:oak_log[axis=y]");
        }
    }

    // ---------- Gürültü ----------
    private static double Fbm(long seed, double x, double z, int octaves)
    {
        double sum = 0, amp = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value(seed + i * 7919, x, z) * amp;
            norm += amp; amp *= 0.5; x *= 2; z *= 2;
        }
        return sum / norm;
    }

    private static double Value(long seed, double x, double z)
    {
        int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
        double fx = x - x0, fz = z - z0;
        double sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
        double a = Hash(seed, x0, z0), b = Hash(seed, x0 + 1, z0), c = Hash(seed, x0, z0 + 1), d = Hash(seed, x0 + 1, z0 + 1);
        return Lerp(Lerp(a, b, sx), Lerp(c, d, sx), sz);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Hash(long seed, int x, int z)
    {
        ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)x * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(uint)z * 0x165667B19E3779F9UL;
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
        return (h & 0xFFFFFF) / (double)0xFFFFFF * 2 - 1;
    }
}
