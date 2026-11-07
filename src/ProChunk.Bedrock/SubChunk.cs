using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using ProChunk.Core;

namespace ProChunk.Bedrock;

/// <summary>Bedrock SubChunk (16x16x16) çözücü/kodlayıcı. Okuma: sürüm 1, 8, 9. Yazma: sürüm 9.</summary>
public static class SubChunk
{
    /// <summary>Bir blok katmanı. Indeks sırası XZY: (x &lt;&lt; 8) | (z &lt;&lt; 4) | y.</summary>
    public sealed record Layer(string[] Palette, ushort[] Indices)
    {
        public string Get(int x, int y, int z) => Palette[Indices[(x << 8) | (z << 4) | y]];
    }

    /// <summary>Durum metni → orijinal LE-NBT palet girdisi (yazarken aynen kullanılır).</summary>
    public static readonly ConcurrentDictionary<string, byte[]> RawPalette = new();

    public static int XzyIndex(int x, int y, int z) => (x << 8) | (z << 4) | y;

    public static List<Layer> Decode(byte[] d)
    {
        var layers = new List<Layer>();
        if (d.Length < 2) return layers;
        int p = 0;
        byte version = d[p++];
        int count;
        switch (version)
        {
            case 1: count = 1; break;
            case 8: count = d[p++]; break;
            case 9: count = d[p++]; p++; break; // y indeksi
            default: return layers; // eski (0, 2–7) sürümler desteklenmiyor
        }

        for (int s = 0; s < count && p < d.Length; s++)
        {
            int bits = d[p++] >> 1;
            var idx = new ushort[4096];
            if (bits > 0)
            {
                int perWord = 32 / bits;
                int words = (4096 + perWord - 1) / perWord;
                uint mask = (1u << bits) - 1;
                for (int w = 0, i = 0; w < words; w++)
                {
                    uint word = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + w * 4));
                    for (int k = 0; k < perWord && i < 4096; k++, i++)
                        idx[i] = (ushort)((word >> (k * bits)) & mask);
                }
                p += words * 4;
            }
            int palCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
            var palette = new string[Math.Max(palCount, 1)];
            palette[0] = BlockState.Air;
            for (int i = 0; i < palCount; i++)
            {
                int start = p;
                var state = LittleNbt.ReadBlockState(d, ref p);
                palette[i] = state;
                RawPalette.TryAdd(state, d.AsSpan(start, p - start).ToArray());
            }
            for (int i = 0; i < idx.Length; i++) if (idx[i] >= palette.Length) idx[i] = 0;
            layers.Add(new Layer(palette, idx));
        }
        return layers;
    }

    public static byte[] Encode(sbyte y, IReadOnlyList<Layer> layers)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(9);
        ms.WriteByte((byte)layers.Count);
        ms.WriteByte((byte)y);
        Span<byte> four = stackalloc byte[4];
        foreach (var layer in layers)
        {
            int n = layer.Palette.Length;
            int bits = new[] { 1, 2, 3, 4, 5, 6, 8, 16 }.First(b => (1 << b) >= n);
            ms.WriteByte((byte)(bits << 1)); // kalıcı (persistent) palet
            int perWord = 32 / bits;
            int words = (4096 + perWord - 1) / perWord;
            for (int w = 0, i = 0; w < words; w++)
            {
                uint word = 0;
                for (int k = 0; k < perWord && i < 4096; k++, i++) word |= (uint)layer.Indices[i] << (k * bits);
                BinaryPrimitives.WriteUInt32LittleEndian(four, word);
                ms.Write(four);
            }
            BinaryPrimitives.WriteInt32LittleEndian(four, n);
            ms.Write(four);
            foreach (var state in layer.Palette)
                ms.Write(RawPalette.TryGetValue(state, out var raw) ? raw : LittleNbt.WriteBlockState(state));
        }
        return ms.ToArray();
    }
}

/// <summary>Little-endian NBT yardımcıları (Bedrock).</summary>
public static class LittleNbt
{
    /// <summary>Palet girdisini "name[k=v,...]" durum metnine çevirir.</summary>
    public static string ReadBlockState(byte[] d, ref int p)
    {
        byte type = d[p++];
        if (type != 10) throw new InvalidDataException("Palet girdisi compound değil");
        SkipString(d, ref p);
        string name = BlockState.Air;
        var props = new List<(string, string)>();
        while (true)
        {
            byte t = d[p++];
            if (t == 0) break;
            var tagName = ReadString(d, ref p);
            if (t == 8 && tagName == "name") name = ReadString(d, ref p);
            else if (t == 10 && tagName == "states")
            {
                while (true)
                {
                    byte st = d[p++];
                    if (st == 0) break;
                    var key = ReadString(d, ref p);
                    switch (st)
                    {
                        case 1: props.Add((key, d[p] switch { 0 => "false", 1 => "true", var b => b.ToString() })); p += 1; break;
                        case 3: props.Add((key, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)).ToString())); p += 4; break;
                        case 8: props.Add((key, ReadString(d, ref p))); break;
                        default: SkipPayload(d, ref p, st); break;
                    }
                }
            }
            else SkipPayload(d, ref p, t);
        }
        return BlockState.Format(name, props);
    }

    /// <summary>Bilinmeyen durum için yeni palet girdisi. Özellikler string olarak yazılır.</summary>
    public static byte[] WriteBlockState(string state)
    {
        var (name, props) = BlockState.Parse(state);
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)10); WriteString(w, "");
        w.Write((byte)8); WriteString(w, "name"); WriteString(w, name);
        w.Write((byte)10); WriteString(w, "states");
        foreach (var (k, v) in props)
        {
            if (v is "true" or "false") { w.Write((byte)1); WriteString(w, k); w.Write((byte)(v == "true" ? 1 : 0)); }
            else if (int.TryParse(v, out var iv)) { w.Write((byte)3); WriteString(w, k); w.Write(iv); }
            else { w.Write((byte)8); WriteString(w, k); WriteString(w, v); }
        }
        w.Write((byte)0);
        w.Write((byte)3); WriteString(w, "version"); w.Write(18168865);
        w.Write((byte)0);
        return ms.ToArray();
    }

    /// <summary>level.dat gibi tam bir LE-NBT kökünden üst düzey bir string alanını okur.</summary>
    public static string? ReadTopLevelString(byte[] d, int p, string field)
    {
        if (d[p++] != 10) return null;
        SkipString(d, ref p);
        while (p < d.Length)
        {
            byte t = d[p++];
            if (t == 0) break;
            var n = ReadString(d, ref p);
            if (t == 8 && n == field) return ReadString(d, ref p);
            SkipPayload(d, ref p, t);
        }
        return null;
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)b.Length); w.Write(b);
    }

    private static string ReadString(byte[] d, ref int p)
    {
        int len = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2;
        var s = Encoding.UTF8.GetString(d, p, len); p += len;
        return s;
    }

    private static void SkipString(byte[] d, ref int p) => p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));

    private static int ReadInt(byte[] d, ref int p) { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }

    private static void SkipPayload(byte[] d, ref int p, byte t)
    {
        switch (t)
        {
            case 1: p += 1; break;
            case 2: p += 2; break;
            case 3: case 5: p += 4; break;
            case 4: case 6: p += 8; break;
            case 7: p += ReadInt(d, ref p); break;
            case 8: SkipString(d, ref p); break;
            case 9:
                byte et = d[p++];
                int n = ReadInt(d, ref p);
                for (int i = 0; i < n; i++) SkipPayload(d, ref p, et);
                break;
            case 10:
                while (true)
                {
                    byte ct = d[p++];
                    if (ct == 0) break;
                    SkipString(d, ref p);
                    SkipPayload(d, ref p, ct);
                }
                break;
            case 11: p += ReadInt(d, ref p) * 4; break;
            case 12: p += ReadInt(d, ref p) * 8; break;
            default: throw new InvalidDataException($"Bilinmeyen NBT türü {t}");
        }
    }
}
