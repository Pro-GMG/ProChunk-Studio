namespace ProChunk.Core;

/// <summary>Harita pikselleri için ortak renk/gölge yardımcıları.</summary>
public static class MapPainter
{
    public const uint WaterColor = 0xFF3F76E4;
    public const uint UnknownChunk = 0xFF404048;

    /// <summary>Bir region'u (32x32 chunk) 512x512 piksel olarak çizer.</summary>
    public static uint[] RenderRegion(int rx, int rz, Func<int, int, ChunkVolume?> read)
    {
        var pixels = new uint[512 * 512];
        var heights = new int[512 * 512];

        for (int lz = 0; lz < 32; lz++)
        for (int lx = 0; lx < 32; lx++)
        {
            ChunkVolume? v;
            try { v = read(rx * 32 + lx, rz * 32 + lz); }
            catch { FillChunk(pixels, lx, lz, UnknownChunk); continue; }
            if (v is null) continue;
            if (v.Sections.Count == 0) { FillChunk(pixels, lx, lz, UnknownChunk); continue; }
            if (v.Sections.Values.All(s => s.Palette.All(BlockColors.IsAir))) continue;   // henüz üretilmemiş: boş bırak
            RenderChunk(v, pixels, heights, lx * 16, lz * 16, 512);
        }

        ApplyRelief(pixels, heights);
        return pixels;
    }

    /// <summary>Bir chunk'ın üstten görünümünü hedef diziye yazar.</summary>
    public static void RenderChunk(ChunkVolume v, uint[] pixels, int[] heights, int ox, int oz, int stride, int maxY = int.MaxValue)
    {
        // Palet başına önbellek: 0 = hava, 1 = su, 2 = katı
        var sections = v.Sections.Values.Reverse().Where(s => s.Y * 16 <= maxY).Select(s =>
        {
            var kind = new byte[s.Palette.Count];
            var color = new uint[s.Palette.Count];
            for (int i = 0; i < kind.Length; i++)
            {
                var n = s.Palette[i];
                kind[i] = BlockColors.IsAir(n) ? (byte)0 : BlockColors.IsWater(n) ? (byte)1 : (byte)2;
                color[i] = BlockColors.Get(n);
            }
            return (s, kind, color);
        }).ToList();

        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int depth = 0; uint c = 0xFF000000; int h = 0; bool done = false;
            foreach (var (s, kind, color) in sections)
            {
                int top = Math.Min(15, maxY - s.Y * 16);
                for (int y = top; y >= 0; y--)
                {
                    int pi = s.Blocks[ChunkSection.Index(x, y, z)];
                    if (kind[pi] == 0) continue;
                    h = s.Y * 16 + y;
                    if (kind[pi] == 1)
                    {
                        if (++depth >= 12) { c = WaterColor; done = true; break; }
                        continue;
                    }
                    c = depth > 0 ? UnderWater(color[pi], depth) : color[pi];
                    done = true; break;
                }
                if (done) break;
            }
            if (!done && depth > 0) c = WaterColor;
            int i = (oz + z) * stride + ox + x;
            pixels[i] = c; heights[i] = h;
        }
    }

    /// <summary>Kuzey komşusuna göre yükseklik gölgesi (Minecraft harita tarzı).</summary>
    public static void ApplyRelief(uint[] pixels, int[] heights, int size = 512)
    {
        for (int z = size - 1; z >= 1; z--)
        for (int x = 0; x < size; x++)
        {
            int i = z * size + x;
            if (pixels[i] == 0 || pixels[i - size] == 0) continue;
            int d = heights[i] - heights[i - size];
            if (d > 0) pixels[i] = Shade(pixels[i], 1.12);
            else if (d < 0) pixels[i] = Shade(pixels[i], 0.85);
        }
    }

    /// <summary>Su altındaki zemin rengini derinliğe göre suyla karıştırır.</summary>
    public static uint UnderWater(uint ground, int depth) =>
        Blend(ground, WaterColor, Math.Min(0.55 + depth * 0.04, 0.95));

    public static void FillChunk(uint[] p, int localCx, int localCz, uint c)
    {
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
            p[(localCz * 16 + z) * 512 + localCx * 16 + x] = c;
    }

    public static uint Shade(uint c, double f)
    {
        uint r = (uint)Math.Min(255, ((c >> 16) & 0xFF) * f);
        uint g = (uint)Math.Min(255, ((c >> 8) & 0xFF) * f);
        uint b = (uint)Math.Min(255, (c & 0xFF) * f);
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }

    public static uint Blend(uint a, uint b, double t)
    {
        uint Mix(int sh) => (uint)(((a >> sh) & 0xFF) * (1 - t) + ((b >> sh) & 0xFF) * t);
        return 0xFF000000 | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }
}
