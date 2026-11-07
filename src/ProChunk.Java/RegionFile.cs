using System.Buffers.Binary;
using System.IO.Compression;
using fNbt;

namespace ProChunk.Java;

/// <summary>Anvil (.mca) region dosyası. Bir region 32x32 chunk içerir.</summary>
public sealed class RegionFile
{
    private readonly byte[] _data;
    private readonly int[] _locations = new int[1024];
    private readonly int[] _timestamps = new int[1024];

    public string Path { get; }
    public int RegionX { get; }
    public int RegionZ { get; }

    private RegionFile(string path, int rx, int rz, byte[] data)
    {
        Path = path; RegionX = rx; RegionZ = rz; _data = data;
        if (data.Length >= 8192)
            for (int i = 0; i < 1024; i++)
            {
                _locations[i] = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i * 4, 4));
                _timestamps[i] = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4096 + i * 4, 4));
            }
    }

    public static RegionFile Open(string path)
    {
        if (!TryParseName(System.IO.Path.GetFileName(path), out var rx, out var rz))
            throw new ArgumentException($"Geçersiz region dosya adı: {path}");
        return new RegionFile(path, rx, rz, File.ReadAllBytes(path));
    }

    /// <summary>"r.X.Z.mca" biçimindeki adı çözer.</summary>
    public static bool TryParseName(string fileName, out int rx, out int rz)
    {
        rx = rz = 0;
        var p = fileName.Split('.');
        return p.Length == 4 && p[0] == "r" && p[3] == "mca"
               && int.TryParse(p[1], out rx) && int.TryParse(p[2], out rz);
    }

    public bool HasChunk(int localX, int localZ) => _locations[localX + localZ * 32] != 0;

    /// <summary>Sektör içindeki ham kayıt: [uzunluk(4)][sıkıştırma(1)][veri]. Yoksa null.</summary>
    private byte[]? RawEntry(int index)
    {
        int loc = _locations[index];
        if (loc == 0) return null;
        long offset = (long)(loc >> 8) * 4096;
        if (offset + 5 > _data.Length) return null;
        int length = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan((int)offset, 4));
        if (length < 1 || offset + 4 + length > _data.Length) return null;
        return _data.AsSpan((int)offset, 4 + length).ToArray();
    }

    /// <summary>Chunk NBT kökünü döndürür; chunk yoksa veya okunamıyorsa null.</summary>
    public NbtCompound? ReadChunk(int localX, int localZ)
    {
        var raw = RawEntry(localX + localZ * 32);
        if (raw is null) return null;
        byte compression = raw[4];
        Stream src;
        if ((compression & 0x80) != 0)
        {
            var mcc = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!,
                $"c.{RegionX * 32 + localX}.{RegionZ * 32 + localZ}.mcc");
            if (!File.Exists(mcc)) return null;
            src = new MemoryStream(File.ReadAllBytes(mcc));
            compression &= 0x7F;
        }
        else src = new MemoryStream(raw, 5, raw.Length - 5, writable: false);

        using (src)
        {
            Stream? s = compression switch
            {
                1 => new GZipStream(src, CompressionMode.Decompress),
                2 => new ZLibStream(src, CompressionMode.Decompress),
                3 => src,
                _ => null, // 4 = LZ4 (henüz desteklenmiyor)
            };
            if (s is null) return null;
            using (s)
            {
                var file = new NbtFile();
                file.LoadFromStream(s, NbtCompression.None);
                return file.RootTag;
            }
        }
    }

    /// <summary>
    /// Region dosyasını yeniden yazar. changes: yerel indeks (x + z*32) → yeni kök (null = sil).
    /// Değişmeyen chunk'lar bayt bayt korunur. Hiç chunk kalmazsa dosya silinir.
    /// </summary>
    public static void Write(string path, RegionFile? original, IReadOnlyDictionary<int, NbtCompound?> changes)
    {
        TryParseName(System.IO.Path.GetFileName(path), out var rx, out var rz);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var header = new byte[8192];
        using var body = new MemoryStream();
        int sector = 2;
        int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int count = 0;

        for (int i = 0; i < 1024; i++)
        {
            byte[]? entry;
            int timestamp;
            if (changes.TryGetValue(i, out var root))
            {
                var mcc = System.IO.Path.Combine(dir, $"c.{rx * 32 + (i & 31)}.{rz * 32 + (i >> 5)}.mcc");
                if (File.Exists(mcc)) File.Delete(mcc);
                if (root is null) continue;
                var compressed = Compress(root);
                if (compressed.Length + 5 > 255 * 4096)
                {
                    File.WriteAllBytes(mcc, compressed);   // büyük chunk → harici dosya
                    entry = new byte[] { 0, 0, 0, 1, 0x82 };
                }
                else
                {
                    entry = new byte[5 + compressed.Length];
                    BinaryPrimitives.WriteInt32BigEndian(entry, compressed.Length + 1);
                    entry[4] = 2;
                    compressed.CopyTo(entry, 5);
                }
                timestamp = now;
            }
            else
            {
                entry = original?.RawEntry(i);
                if (entry is null) continue;
                timestamp = original!._timestamps[i];
            }

            int sectors = (entry.Length + 4095) / 4096;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(i * 4), (sector << 8) | sectors);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4096 + i * 4), timestamp);
            body.Write(entry);
            body.Write(new byte[sectors * 4096 - entry.Length]);
            sector += sectors;
            count++;
        }

        if (count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) { fs.Write(header); body.Position = 0; body.CopyTo(fs); }
        File.Move(tmp, path, overwrite: true);
    }

    private static byte[] Compress(NbtCompound root)
    {
        var raw = new NbtFile(root).SaveToBuffer(NbtCompression.None);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }
}
