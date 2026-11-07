using fNbt;

namespace ProChunk.Core.Nbt;

/// <summary>NBT okuma/yazma ve küçük yardımcılar.</summary>
public static class NbtIO
{
    // ---------- Bedrock (little-endian) ----------
    public static NbtCompound ReadBedrock(byte[] data, int offset = 0)
    {
        var f = new NbtFile(NbtFlavor.Bedrock);
        f.LoadFromBuffer(data, offset, data.Length - offset, NbtCompression.None);
        return f.RootTag;
    }

    /// <summary>Arka arkaya yazılmış birden çok kökü okur (ör. blok varlıkları).</summary>
    public static List<NbtCompound> ReadBedrockMany(byte[] data)
    {
        var list = new List<NbtCompound>();
        int p = 0;
        while (p < data.Length)
        {
            var f = new NbtFile(NbtFlavor.Bedrock);
            long n = f.LoadFromBuffer(data, p, data.Length - p, NbtCompression.None);
            if (n <= 0) break;
            list.Add(f.RootTag);
            p += (int)n;
        }
        return list;
    }

    public static byte[] WriteBedrock(NbtCompound root) =>
        new NbtFile(Detached(root), NbtFlavor.Bedrock).SaveToBuffer(NbtCompression.None);

    public static byte[] WriteBedrockMany(IEnumerable<NbtCompound> roots)
    {
        using var ms = new MemoryStream();
        foreach (var r in roots) ms.Write(WriteBedrock(r));
        return ms.ToArray();
    }

    // ---------- Java (big-endian, gzip) ----------
    public static NbtCompound ReadJavaFile(string path) => new NbtFile(path).RootTag;

    public static void WriteJavaFile(string path, NbtCompound root)
    {
        var tmp = path + ".tmp";
        new NbtFile(Detached(root)).SaveToFile(tmp, NbtCompression.GZip);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Ebeveyni olmayan, adı boş olmayan bir kopya döndürür (NbtFile kökü için gerekli).</summary>
    public static NbtCompound Detached(NbtCompound c)
    {
        var copy = (NbtCompound)c.Clone();
        copy.Name ??= "";
        return copy;
    }

    // ---------- Etiket yardımcıları ----------
    /// <summary>Sayısal etiketi işaretli tam sayı olarak okur (byte -106 gibi değerler için).</summary>
    public static int? Int(NbtTag? t) => t switch
    {
        NbtByte b => (sbyte)b.Value,
        NbtShort s => s.Value,
        NbtInt i => i.Value,
        NbtLong l => (int)l.Value,
        _ => null,
    };

    public static void Set(NbtCompound c, NbtTag t)
    {
        c.Remove(t.Name!);
        c.Add(t);
    }

    public static NbtList EnsureList(NbtCompound parent, string name)
    {
        if (parent.Get(name) is NbtList l && (l.ListType == NbtTagType.Compound || l.Count == 0))
        {
            if (l.ListType != NbtTagType.Compound) { parent.Remove(name); l = new NbtList(name, NbtTagType.Compound); parent.Add(l); }
            return l;
        }
        parent.Remove(name);
        var list = new NbtList(name, NbtTagType.Compound);
        parent.Add(list);
        return list;
    }

    public static NbtCompound EnsureCompound(NbtCompound parent, string name)
    {
        if (parent.Get(name) is NbtCompound c) return c;
        parent.Remove(name);
        c = new NbtCompound(name);
        parent.Add(c);
        return c;
    }

    /// <summary>Etiketi aynı türde yeni bir değerle değiştirir. Başarısızsa false.</summary>
    public static bool TrySetValue(NbtCompound parent, string name, string text)
    {
        var old = parent.Get(name);
        NbtTag? t = old switch
        {
            NbtByte => text is "true" or "false" ? new NbtByte(name, (byte)(text == "true" ? 1 : 0))
                     : sbyte.TryParse(text, out var b) ? new NbtByte(name, unchecked((byte)b)) : null,
            NbtShort => short.TryParse(text, out var s) ? new NbtShort(name, s) : null,
            NbtInt => int.TryParse(text, out var i) ? new NbtInt(name, i) : null,
            NbtLong => long.TryParse(text, out var l) ? new NbtLong(name, l) : null,
            NbtFloat => float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? new NbtFloat(name, f) : null,
            NbtDouble => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? new NbtDouble(name, d) : null,
            NbtString or null => new NbtString(name, text),
            _ => null,
        };
        if (t is null) return false;
        Set(parent, t);
        return true;
    }
}
