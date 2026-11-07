using System.Buffers.Binary;
using System.IO.Compression;

namespace ProChunk.Bedrock;

/// <summary>
/// Salt-okunur, saf C# LevelDB okuyucu (Mojang varyantı: zlib / raw-deflate blok sıkıştırma).
/// Açılışta tüm anahtarları indeksler; değerler istendiğinde bloktan okunur.
/// </summary>
public sealed class LevelDbReader
{
    private readonly record struct Loc(int File, long BlockOffset, int BlockSize, int ValueOffset, int ValueLength, ulong Seq, bool Deleted, byte[]? Inline);

    private readonly List<string> _files = new();
    private readonly Dictionary<byte[], Loc> _index = new(ByteArrayComparer.Instance);
    private readonly Dictionary<(int, long), byte[]> _blockCache = new();
    private readonly Queue<(int, long)> _cacheOrder = new();
    private readonly object _lock = new();
    private const int CacheBlocks = 256;

    private readonly string _folder;
    private ulong _maxSeq;

    public LevelDbReader(string dbFolder)
    {
        _folder = dbFolder;
        foreach (var f in Directory.EnumerateFiles(dbFolder).Where(f => f.EndsWith(".ldb") || f.EndsWith(".sst")))
        {
            int id = _files.Count;
            _files.Add(f);
            try { IndexTable(id, f); } catch { /* bozuk tablo atlanır */ }
        }
        foreach (var f in Directory.EnumerateFiles(dbFolder, "*.log"))
            try { ReadLog(f); } catch { }
    }

    public int Count => _index.Count(kv => !kv.Value.Deleted);

    public IEnumerable<byte[]> Keys
    {
        get { foreach (var kv in _index) if (!kv.Value.Deleted) yield return kv.Key; }
    }

    public byte[]? Get(byte[] key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var loc) || loc.Deleted) return null;
            if (loc.Inline is not null) return loc.Inline;
            var block = GetBlock(loc.File, loc.BlockOffset, loc.BlockSize);
            return block.AsSpan(loc.ValueOffset, loc.ValueLength).ToArray();
        }
    }

    // ---------- SSTable ----------
    private void IndexTable(int fileId, string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < 48) return;
        var footer = new byte[48];
        fs.Seek(-48, SeekOrigin.End);
        fs.ReadExactly(footer);
        int p = 0;
        ReadVarint(footer, ref p); ReadVarint(footer, ref p); // metaindex
        long idxOff = (long)ReadVarint(footer, ref p);
        int idxSize = (int)ReadVarint(footer, ref p);

        var index = ReadBlock(fs, idxOff, idxSize);
        foreach (var (_, vOff, vLen) in IterateBlock(index))
        {
            int q = vOff;
            long off = (long)ReadVarint(index, ref q);
            int size = (int)ReadVarint(index, ref q);
            var data = ReadBlock(fs, off, size);
            foreach (var (ikey, valOff, valLen) in IterateBlock(data))
            {
                if (ikey.Length < 8) continue;
                ulong trailer = BinaryPrimitives.ReadUInt64LittleEndian(ikey.AsSpan(ikey.Length - 8));
                Put(ikey[..^8], new Loc(fileId, off, size, valOff, valLen, trailer >> 8, (trailer & 0xFF) == 0, null));
            }
        }
    }

    private byte[] GetBlock(int file, long offset, int size)
    {
        if (_blockCache.TryGetValue((file, offset), out var b)) return b;
        using (var fs = File.OpenRead(_files[file])) b = ReadBlock(fs, offset, size);
        _blockCache[(file, offset)] = b;
        _cacheOrder.Enqueue((file, offset));
        if (_cacheOrder.Count > CacheBlocks) _blockCache.Remove(_cacheOrder.Dequeue());
        return b;
    }

    private static byte[] ReadBlock(FileStream fs, long offset, int size)
    {
        var buf = new byte[size + 5];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(buf);
        byte type = buf[size];
        if (type == 0) return buf[..size];
        using var input = new MemoryStream(buf, 0, size);
        using Stream z = type switch
        {
            2 => new ZLibStream(input, CompressionMode.Decompress),
            4 => new DeflateStream(input, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"LevelDB sıkıştırma türü desteklenmiyor: {type}"),
        };
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Blok girdileri: (tam anahtar, değer ofseti, değer uzunluğu).</summary>
    private static IEnumerable<(byte[] Key, int ValueOffset, int ValueLength)> IterateBlock(byte[] block)
    {
        if (block.Length < 4) yield break;
        int restarts = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(block.Length - 4));
        int end = block.Length - 4 - restarts * 4;
        int p = 0;
        byte[] last = Array.Empty<byte>();
        while (p < end)
        {
            int shared = (int)ReadVarint(block, ref p);
            int nonShared = (int)ReadVarint(block, ref p);
            int valLen = (int)ReadVarint(block, ref p);
            var key = new byte[shared + nonShared];
            Buffer.BlockCopy(last, 0, key, 0, shared);
            Buffer.BlockCopy(block, p, key, shared, nonShared);
            p += nonShared;
            yield return (key, p, valLen);
            p += valLen;
            last = key;
        }
    }

    // ---------- Log (henüz tabloya yazılmamış son değişiklikler) ----------
    private void ReadLog(string path)
    {
        var data = File.ReadAllBytes(path);
        var record = new MemoryStream();
        for (int blockStart = 0; blockStart < data.Length; blockStart += 32768)
        {
            int p = blockStart, blockEnd = Math.Min(blockStart + 32768, data.Length);
            while (p + 7 <= blockEnd)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4));
                byte type = data[p + 6];
                p += 7;
                if (type == 0 || p + len > blockEnd) break;
                record.Write(data, p, len);
                p += len;
                if (type is 1 or 4) { ApplyBatch(record.ToArray()); record.SetLength(0); } // FULL / LAST
            }
        }
    }

    private void ApplyBatch(byte[] b)
    {
        if (b.Length < 12) return;
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(b);
        int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        int p = 12;
        for (int i = 0; i < count && p < b.Length; i++, seq++)
        {
            byte tag = b[p++];
            int kl = (int)ReadVarint(b, ref p);
            var key = b.AsSpan(p, kl).ToArray(); p += kl;
            if (tag == 1)
            {
                int vl = (int)ReadVarint(b, ref p);
                var val = b.AsSpan(p, vl).ToArray(); p += vl;
                Put(key, new Loc(-1, 0, 0, 0, vl, seq, false, val));
            }
            else Put(key, new Loc(-1, 0, 0, 0, 0, seq, true, null));
        }
    }

    // ---------- Yazma ----------
    /// <summary>
    /// Değişiklikleri yeni bir .log dosyasına (WriteBatch) yazar. LevelDB/Minecraft açılışta
    /// bu log'u okuyup veritabanına işler. value == null → silme.
    /// </summary>
    public void Write(IReadOnlyCollection<(byte[] Key, byte[]? Value)> changes)
    {
        if (changes.Count == 0) return;
        ulong seq = _maxSeq + 1;

        using var batch = new MemoryStream();
        Span<byte> tmp = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, seq);
        BinaryPrimitives.WriteInt32LittleEndian(tmp[8..], changes.Count);
        batch.Write(tmp);
        foreach (var (k, v) in changes)
        {
            batch.WriteByte(v is null ? (byte)0 : (byte)1);
            WriteVarint(batch, (ulong)k.Length); batch.Write(k);
            if (v is not null) { WriteVarint(batch, (ulong)v.Length); batch.Write(v); }
        }

        long number = Directory.EnumerateFiles(_folder)
            .Select(f => new string(Path.GetFileName(f).Where(char.IsDigit).ToArray()))
            .Where(s => s.Length > 0).Select(long.Parse).DefaultIfEmpty(0).Max() + 1;
        var path = Path.Combine(_folder, $"{number:D6}.log");
        File.WriteAllBytes(path, FrameLogRecords(batch.ToArray()));

        lock (_lock)
        {
            foreach (var (k, v) in changes)
            {
                _index[k] = new Loc(-1, 0, 0, 0, v?.Length ?? 0, seq, v is null, v);
                seq++;
            }
            _maxSeq = seq - 1;
        }
    }

    /// <summary>Kaydı 32 KB'lık log bloklarına böler (FULL/FIRST/MIDDLE/LAST).</summary>
    private static byte[] FrameLogRecords(byte[] payload)
    {
        const int Block = 32768, Header = 7;
        using var ms = new MemoryStream();
        int pos = 0, blockLeft = Block;
        bool first = true;
        do
        {
            if (blockLeft < Header) { ms.Write(new byte[blockLeft]); blockLeft = Block; }
            int avail = blockLeft - Header;
            int len = Math.Min(avail, payload.Length - pos);
            bool last = pos + len == payload.Length;
            byte type = first && last ? (byte)1 : first ? (byte)2 : last ? (byte)4 : (byte)3;

            uint crc = 0xFFFFFFFF;
            crc = System.Numerics.BitOperations.Crc32C(crc, type);
            for (int i = 0; i < len; i++) crc = System.Numerics.BitOperations.Crc32C(crc, payload[pos + i]);
            crc ^= 0xFFFFFFFF;
            uint masked = ((crc >> 15) | (crc << 17)) + 0xa282ead8;

            var h = new byte[Header];
            BinaryPrimitives.WriteUInt32LittleEndian(h, masked);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), (ushort)len);
            h[6] = type;
            ms.Write(h);
            ms.Write(payload, pos, len);
            pos += len; blockLeft -= Header + len; first = false;
        } while (pos < payload.Length);
        return ms.ToArray();
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private void Put(byte[] key, Loc loc)
    {
        if (loc.Seq > _maxSeq) _maxSeq = loc.Seq;
        if (!_index.TryGetValue(key, out var old) || loc.Seq >= old.Seq) _index[key] = loc;
    }

    private static ulong ReadVarint(byte[] b, ref int p)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte x = b[p++];
            result |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
        }
        return result;
    }

    public sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? a, byte[]? b) => a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] k) { var h = new HashCode(); h.AddBytes(k); return h.ToHashCode(); }
    }
}
