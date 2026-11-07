namespace ProChunk.Core;

/// <summary>16x16x16 blok bölümü. Indeks: (y &lt;&lt; 8) | (z &lt;&lt; 4) | x.</summary>
public sealed class ChunkSection
{
    public int Y { get; }
    public List<string> Palette { get; private set; }
    public ushort[] Blocks { get; private set; }

    /// <summary>Sürüme özgü ek veri (Java: section NBT, Bedrock: ek katmanlar). Değiştirilmez.</summary>
    public object? Native { get; set; }

    public ChunkSection(int y, List<string> palette, ushort[] blocks, object? native = null)
    {
        Y = y; Palette = palette; Blocks = blocks; Native = native;
    }

    public static ChunkSection Empty(int y) => new(y, new List<string> { BlockState.Air }, new ushort[4096]);

    public static int Index(int x, int y, int z) => (y << 8) | (z << 4) | x;

    public string Get(int x, int y, int z) => Palette[Blocks[Index(x, y, z)]];

    public void Set(int x, int y, int z, string state) => Blocks[Index(x, y, z)] = (ushort)IndexOf(state);

    public int IndexOf(string state)
    {
        int i = Palette.IndexOf(state);
        if (i >= 0) return i;
        Palette.Add(state);
        return Palette.Count - 1;
    }

    public bool IsAllAir()
    {
        var used = new bool[Palette.Count];
        foreach (var b in Blocks) used[b] = true;
        for (int i = 0; i < used.Length; i++)
            if (used[i] && !BlockColors.IsAir(Palette[i])) return false;
        return true;
    }

    /// <summary>Kullanılmayan palet girdilerini atar, aynı durumları birleştirir.</summary>
    public void Compact()
    {
        var map = new int[Palette.Count];
        var newPalette = new List<string>();
        var lookup = new Dictionary<string, int>();
        var used = new bool[Palette.Count];
        foreach (var b in Blocks) used[b] = true;
        for (int i = 0; i < Palette.Count; i++)
        {
            if (!used[i]) continue;
            if (!lookup.TryGetValue(Palette[i], out var ni)) { ni = newPalette.Count; newPalette.Add(Palette[i]); lookup[Palette[i]] = ni; }
            map[i] = ni;
        }
        if (newPalette.Count == 0) newPalette.Add(BlockState.Air);
        for (int i = 0; i < Blocks.Length; i++) Blocks[i] = (ushort)map[Blocks[i]];
        Palette = newPalette;
    }

    /// <summary>Palet düzeyinde değiştirme. Dönüş: değişen blok sayısı.</summary>
    public int Replace(Func<string, string?> map)
    {
        var changed = new bool[Palette.Count];
        bool any = false;
        for (int i = 0; i < Palette.Count; i++)
        {
            var to = map(Palette[i]);
            if (to is not null && to != Palette[i]) { Palette[i] = to; changed[i] = any = true; }
        }
        if (!any) return 0;
        int count = Blocks.Count(b => changed[b]);
        Compact();
        return count;
    }

    public ChunkSection Clone() => new(Y, new List<string>(Palette), (ushort[])Blocks.Clone(), Native);
}

/// <summary>Sürümden bağımsız, düzenlenebilir chunk blok verisi.</summary>
public sealed class ChunkVolume
{
    public int X { get; }
    public int Z { get; }
    public int MinSectionY { get; }
    public int MaxSectionY { get; }
    public SortedDictionary<int, ChunkSection> Sections { get; } = new();

    /// <summary>Sürüme özgü kök veri (Java: chunk NBT kökü).</summary>
    public object? Native { get; set; }

    public ChunkVolume(int x, int z, int minSectionY, int maxSectionY)
    {
        X = x; Z = z; MinSectionY = minSectionY; MaxSectionY = maxSectionY;
    }

    public int MinY => MinSectionY * 16;
    public int MaxY => MaxSectionY * 16 + 15;

    public string GetBlock(int x, int y, int z) =>
        Sections.TryGetValue(y >> 4, out var s) ? s.Get(x, y & 15, z) : BlockState.Air;

    public bool SetBlock(int x, int y, int z, string state)
    {
        int sy = y >> 4;
        if (sy < MinSectionY || sy > MaxSectionY) return false;
        if (!Sections.TryGetValue(sy, out var s))
        {
            if (BlockColors.IsAir(state)) return false;
            Sections[sy] = s = ChunkSection.Empty(sy);
        }
        s.Set(x, y & 15, z, state);
        return true;
    }

    public Dictionary<string, int> CountBlocks()
    {
        var counts = new Dictionary<string, int>();
        foreach (var s in Sections.Values)
        {
            var c = new int[s.Palette.Count];
            foreach (var b in s.Blocks) c[b]++;
            for (int i = 0; i < c.Length; i++)
                if (c[i] > 0) counts[s.Palette[i]] = counts.GetValueOrDefault(s.Palette[i]) + c[i];
        }
        return counts;
    }

    public int Replace(Func<string, string?> map) => Sections.Values.Sum(s => s.Replace(map));

    public ChunkVolume Clone()
    {
        var v = new ChunkVolume(X, Z, MinSectionY, MaxSectionY) { Native = Native };
        foreach (var (y, s) in Sections) v.Sections[y] = s.Clone();
        return v;
    }

    /// <summary>Başka bir chunk'ın blok verisini bu chunk'a kopyalar (Y aralığı kesişimi).</summary>
    public void CopyBlocksFrom(ChunkVolume src)
    {
        for (int sy = MinSectionY; sy <= MaxSectionY; sy++)
        {
            if (src.Sections.TryGetValue(sy, out var s))
            {
                var native = Sections.TryGetValue(sy, out var old) ? old.Native : null;
                Sections[sy] = new ChunkSection(sy, new List<string>(s.Palette), (ushort[])s.Blocks.Clone(), native);
            }
            else if (Sections.TryGetValue(sy, out var old) && sy >= src.MinSectionY && sy <= src.MaxSectionY)
            {
                Sections[sy] = new ChunkSection(sy, new List<string> { BlockState.Air }, new ushort[4096], old.Native);
            }
        }
    }
}
