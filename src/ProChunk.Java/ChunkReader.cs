using System.Numerics;
using fNbt;
using ProChunk.Core;

namespace ProChunk.Java;

/// <summary>1.18+ Java chunk NBT'si ile <see cref="ChunkVolume"/> arasında dönüşüm.</summary>
public static class ChunkReader
{
    public static int BitsFor(int paletteCount, int minBits = 4) =>
        paletteCount <= 1 ? 0 : Math.Max(minBits, BitOperations.Log2((uint)(paletteCount - 1)) + 1);

    /// <summary>Paletli, uzunluk sınırı aşmayan (1.16+) paketlenmiş diziyi açar.</summary>
    public static int[] Unpack(long[]? data, int paletteCount, int count = 4096, int minBits = 4)
    {
        var result = new int[count];
        if (data is null || data.Length == 0 || paletteCount <= 1) return result;
        int bits = BitsFor(paletteCount, minBits);
        int perLong = 64 / bits;
        long mask = (1L << bits) - 1;
        for (int i = 0; i < count; i++)
        {
            int li = i / perLong;
            if (li >= data.Length) break;
            result[i] = (int)((data[li] >> ((i % perLong) * bits)) & mask);
        }
        return result;
    }

    public static long[] Pack(ushort[] values, int paletteCount, int minBits = 4)
    {
        int bits = BitsFor(paletteCount, minBits);
        if (bits == 0) return Array.Empty<long>();
        int perLong = 64 / bits;
        var data = new long[(values.Length + perLong - 1) / perLong];
        for (int i = 0; i < values.Length; i++)
            data[i / perLong] |= (long)values[i] << ((i % perLong) * bits);
        return data;
    }

    public static string StateOf(NbtCompound entry)
    {
        var name = entry.Get<NbtString>("Name")?.Value ?? BlockState.Air;
        var props = entry.Get<NbtCompound>("Properties");
        if (props is null || props.Count == 0) return name;
        return BlockState.Format(name, props.Select(p => (p.Name!, p.StringValue)));
    }

    public static NbtCompound EntryOf(string state)
    {
        var (name, props) = BlockState.Parse(state);
        var c = new NbtCompound { new NbtString("Name", name) };
        if (props.Count > 0)
        {
            var p = new NbtCompound("Properties");
            foreach (var (k, v) in props) p.Add(new NbtString(k, v));
            c.Add(p);
        }
        return c;
    }

    public static ChunkVolume ToVolume(NbtCompound root, int cx, int cz)
    {
        var sections = new List<ChunkSection>();
        if (root.Get<NbtList>("sections") is { } list)
        {
            foreach (var tag in list)
            {
                if (tag is not NbtCompound s) continue;
                var states = s.Get<NbtCompound>("block_states");
                var palTag = states?.Get<NbtList>("palette");
                if (palTag is null || palTag.Count == 0) continue;
                var palette = palTag.Select(p => p is NbtCompound c ? StateOf(c) : BlockState.Air).ToList();
                int y = s.Get<NbtByte>("Y")?.Value is byte b ? (sbyte)b : 0;
                var idx = Unpack(states!.Get<NbtLongArray>("data")?.Value, palette.Count);
                var blocks = new ushort[4096];
                for (int i = 0; i < 4096; i++) blocks[i] = (ushort)Math.Min(idx[i], palette.Count - 1);
                sections.Add(new ChunkSection(y, palette, blocks, s));
            }
        }

        int minY = root.Get<NbtInt>("yPos")?.Value ?? (sections.Count > 0 ? sections.Min(s => s.Y) : -4);
        // 1.18+ overworld: -4..19 (Y -64..319); nether/end: 0..15 (Y 0..255)
        int defaultMax = minY < 0 ? minY + 23 : minY + 15;
        int maxY = sections.Count > 0 ? Math.Max(sections.Max(s => s.Y), defaultMax) : defaultMax;
        var v = new ChunkVolume(cx, cz, minY, maxY) { Native = root };
        foreach (var s in sections) v.Sections[s.Y] = s;
        return v;
    }

    /// <summary>Volume'daki blokları kök NBT'nin bir kopyasına yazar.</summary>
    public static NbtCompound FromVolume(ChunkVolume v, bool clearBlockEntities)
    {
        var root = v.Native is NbtCompound n ? (NbtCompound)n.Clone() : NewRoot(v);
        var oldSections = root.Get<NbtList>("sections");
        var byY = new SortedDictionary<int, NbtCompound>();
        if (oldSections is not null)
            foreach (var t in oldSections)
                if (t is NbtCompound s) byY[s.Get<NbtByte>("Y")?.Value is byte b ? (sbyte)b : 0] = (NbtCompound)s.Clone();

        foreach (var (y, sec) in v.Sections)
        {
            sec.Compact();
            if (!byY.TryGetValue(y, out var s))
            {
                s = new NbtCompound { new NbtByte("Y", unchecked((byte)(sbyte)y)) };
                var biomes = new NbtCompound("biomes") { new NbtList("palette", new[] { new NbtString("minecraft:plains") }) };
                s.Add(biomes);
                byY[y] = s;
            }
            var palette = new NbtList("palette", NbtTagType.Compound);
            foreach (var st in sec.Palette) palette.Add(EntryOf(st));
            var states = new NbtCompound("block_states") { palette };
            if (sec.Palette.Count > 1) states.Add(new NbtLongArray("data", Pack(sec.Blocks, sec.Palette.Count)));
            Set(s, states);
            s.Remove("SkyLight"); s.Remove("BlockLight"); // oyun yeniden hesaplar
        }

        var newList = new NbtList("sections", NbtTagType.Compound);
        foreach (var s in byY.Values) newList.Add(s);
        Set(root, newList);
        root.Remove("Heightmaps");                 // oyun yeniden üretir
        Set(root, new NbtByte("isLightOn", 0));    // ışık yeniden hesaplansın
        if (clearBlockEntities) Set(root, new NbtList("block_entities", NbtTagType.Compound));
        return root;
    }

    private static NbtCompound NewRoot(ChunkVolume v) => new("")
    {
        new NbtInt("xPos", v.X), new NbtInt("zPos", v.Z), new NbtInt("yPos", v.MinSectionY),
        new NbtString("Status", "minecraft:full"), new NbtInt("DataVersion", 3953),
    };

    private static void Set(NbtCompound c, NbtTag t) { c.Remove(t.Name!); c.Add(t); }
}
