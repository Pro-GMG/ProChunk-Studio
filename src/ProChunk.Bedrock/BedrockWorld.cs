using System.Buffers.Binary;
using System.Text;
using fNbt;
using ProChunk.Core;
using ProChunk.Core.Items;
using ProChunk.Core.Nbt;

namespace ProChunk.Bedrock;

/// <summary>Bir Bedrock Edition dünya klasörü (level.dat + db/). Düzenlemeler bellekte tutulur, Save() ile yazılır.</summary>
public sealed class BedrockWorld : IWorld
{
    private const byte TagSubChunk = 47, TagVersion = 44, TagVersionOld = 118, TagBlockEntity = 49;
    private static readonly byte[] DigpPrefix = "digp"u8.ToArray();
    private static readonly byte[] ActorPrefix = "actorprefix"u8.ToArray();

    private readonly LevelDbReader _db;
    private readonly object _lock = new();
    private readonly Dictionary<byte[], byte[]?> _overlay = new(LevelDbReader.ByteArrayComparer.Instance);

    // (boyut, x, z) → mevcut subchunk Y'leri ve chunk'a ait tüm anahtarlar
    private readonly Dictionary<(int Dim, int X, int Z), SortedSet<sbyte>> _chunks = new();
    private readonly Dictionary<(int Dim, int X, int Z), HashSet<byte[]>> _chunkKeys = new();

    private readonly List<byte[]> _playerKeys = new();
    private NbtCompound? _pendingLevel;

    public string RootPath { get; }
    public string Name { get; private set; }
    public string Edition => "Bedrock";
    public IItemCodec Items => BedrockItemCodec.Instance;
    public bool IsDirty { get { lock (_lock) return _overlay.Count > 0 || _pendingLevel is not null; } }

    public static bool IsBedrockWorld(string folder) =>
        File.Exists(Path.Combine(folder, "level.dat")) && Directory.Exists(Path.Combine(folder, "db"));

    private BedrockWorld(string root)
    {
        RootPath = root;
        Name = ReadName(root);
        _db = new LevelDbReader(Path.Combine(root, "db"));

        foreach (var key in _db.Keys)
        {
            if (key.AsSpan().StartsWith("~local_player"u8) || key.AsSpan().StartsWith("player_server_"u8))
            {
                _playerKeys.Add(key);
                continue;
            }
            if (!TryParseChunkKey(key, out var dim, out var x, out var z, out var tag, out var y)) continue;
            KeysOf(dim, x, z).Add(key);
            if (tag is not (TagSubChunk or TagVersion or TagVersionOld)) continue;
            var set = YsOf(dim, x, z);
            if (tag == TagSubChunk) set.Add(y);
        }
    }

    public static BedrockWorld Open(string folder)
    {
        if (!IsBedrockWorld(folder)) throw new DirectoryNotFoundException("Bu klasör bir Bedrock dünyası değil (level.dat veya db/ yok).");
        return new BedrockWorld(folder);
    }

    private static string ReadName(string root)
    {
        var txt = Path.Combine(root, "levelname.txt");
        if (File.Exists(txt)) { var n = File.ReadAllText(txt).Trim(); if (n.Length > 0) return n; }
        try
        {
            var d = File.ReadAllBytes(Path.Combine(root, "level.dat"));
            return LittleNbt.ReadTopLevelString(d, 8, "LevelName") ?? Path.GetFileName(root);
        }
        catch { return Path.GetFileName(root); }
    }

    private SortedSet<sbyte> YsOf(int dim, int x, int z)
    {
        if (!_chunks.TryGetValue((dim, x, z), out var s)) _chunks[(dim, x, z)] = s = new SortedSet<sbyte>();
        return s;
    }

    private HashSet<byte[]> KeysOf(int dim, int x, int z)
    {
        if (!_chunkKeys.TryGetValue((dim, x, z), out var s))
            _chunkKeys[(dim, x, z)] = s = new HashSet<byte[]>(LevelDbReader.ByteArrayComparer.Instance);
        return s;
    }

    private byte[]? Get(byte[] key)
    {
        lock (_lock)
            if (_overlay.TryGetValue(key, out var v)) return v;
        return _db.Get(key);
    }

    private void Put(byte[] key, byte[]? value)
    {
        lock (_lock) _overlay[key] = value;
    }

    /// <summary>Anahtar: x(int32) z(int32) [boyut(int32)] tag(byte) [subchunk y(sbyte)].</summary>
    public static bool TryParseChunkKey(byte[] k, out int dim, out int x, out int z, out byte tag, out sbyte y)
    {
        dim = 0; x = z = 0; tag = 0; y = 0;
        if (k.Length is not (9 or 10 or 13 or 14)) return false;
        x = BinaryPrimitives.ReadInt32LittleEndian(k);
        z = BinaryPrimitives.ReadInt32LittleEndian(k.AsSpan(4));
        int p = 8;
        if (k.Length >= 13) { dim = BinaryPrimitives.ReadInt32LittleEndian(k.AsSpan(8)); p = 12; if (dim is < 1 or > 2) return false; }
        tag = k[p];
        if (k.Length == p + 2) { if (tag != TagSubChunk) return false; y = (sbyte)k[p + 1]; }
        return tag is >= 43 and <= 128;
    }

    public static byte[] ChunkKey(int dim, int x, int z, byte tag, sbyte? y = null)
    {
        var k = new byte[(dim == 0 ? 9 : 13) + (y.HasValue ? 1 : 0)];
        BinaryPrimitives.WriteInt32LittleEndian(k, x);
        BinaryPrimitives.WriteInt32LittleEndian(k.AsSpan(4), z);
        int p = 8;
        if (dim != 0) { BinaryPrimitives.WriteInt32LittleEndian(k.AsSpan(8), dim); p = 12; }
        k[p] = tag;
        if (y.HasValue) k[p + 1] = (byte)y.Value;
        return k;
    }

    private static byte[] DigpKey(int dim, int x, int z)
    {
        var k = new byte[dim == 0 ? 12 : 16];
        DigpPrefix.CopyTo(k, 0);
        BinaryPrimitives.WriteInt32LittleEndian(k.AsSpan(4), x);
        BinaryPrimitives.WriteInt32LittleEndian(k.AsSpan(8), z);
        if (dim != 0) BinaryPrimitives.WriteInt32LittleEndian(k.AsSpan(12), dim);
        return k;
    }

    /// <summary>Chunk'a ait tüm anahtarlar: chunk verileri + varlık listesi (digp) + varlıklar.</summary>
    private List<byte[]> AllKeys(int dim, int x, int z)
    {
        var keys = _chunkKeys.TryGetValue((dim, x, z), out var s) ? s.ToList() : new List<byte[]>();
        var digp = DigpKey(dim, x, z);
        if (Get(digp) is { } ids)
        {
            keys.Add(digp);
            for (int i = 0; i + 8 <= ids.Length; i += 8)
                keys.Add(ActorPrefix.Concat(ids.AsSpan(i, 8).ToArray()).ToArray());
        }
        return keys;
    }

    public int ChunkCount { get { lock (_lock) return _chunks.Count; } }

    private (int Min, int Max) SectionRange(int dim, int x, int z, SortedSet<sbyte> ys)
    {
        if (dim == 1) return (0, 7);
        if (dim == 2) return (0, 15);
        if (ys.Count > 0 && ys.Min < 0) return (-4, 19);
        var ver = Get(ChunkKey(dim, x, z, TagVersion)) ?? Get(ChunkKey(dim, x, z, TagVersionOld));
        return ver is { Length: > 0 } && ver[0] >= 40 ? (-4, 19) : (0, 15);
    }

    // ---------- IWorld ----------
    public IReadOnlySet<(int X, int Z)> GetRegions(int dimension)
    {
        lock (_lock) return _chunks.Keys.Where(c => c.Dim == dimension).Select(c => (c.X >> 5, c.Z >> 5)).ToHashSet();
    }

    public bool HasChunk(int dim, int cx, int cz) { lock (_lock) return _chunks.ContainsKey((dim, cx, cz)); }

    public ChunkVolume? ReadChunk(int dim, int cx, int cz)
    {
        SortedSet<sbyte> ys;
        lock (_lock)
        {
            if (!_chunks.TryGetValue((dim, cx, cz), out var s)) return null;
            ys = new SortedSet<sbyte>(s);
        }
        var (min, max) = SectionRange(dim, cx, cz, ys);
        var v = new ChunkVolume(cx, cz, Math.Min(min, ys.Count > 0 ? ys.Min : min), Math.Max(max, ys.Count > 0 ? ys.Max : max));

        foreach (var y in ys)
        {
            byte[]? data;
            List<SubChunk.Layer> layers;
            try
            {
                data = Get(ChunkKey(dim, cx, cz, TagSubChunk, y));
                if (data is null) continue;
                layers = SubChunk.Decode(data);
            }
            catch { continue; }
            if (layers.Count == 0) continue;

            var l0 = layers[0];
            var blocks = new ushort[4096];
            for (int by = 0; by < 16; by++)
            for (int bz = 0; bz < 16; bz++)
            for (int bx = 0; bx < 16; bx++)
                blocks[ChunkSection.Index(bx, by, bz)] = l0.Indices[SubChunk.XzyIndex(bx, by, bz)];
            v.Sections[y] = new ChunkSection(y, l0.Palette.ToList(), blocks, layers.Skip(1).ToList());
        }
        return v;
    }

    public void WriteChunk(int dim, ChunkVolume volume, bool clearBlockEntities = false)
    {
        EnsureChunkExists(dim, volume.X, volume.Z);
        var writes = new List<(byte[] Key, byte[] Value, sbyte Y)>();
        foreach (var (y, sec) in volume.Sections)
        {
            sec.Compact();
            var idx = new ushort[4096];
            for (int by = 0; by < 16; by++)
            for (int bz = 0; bz < 16; bz++)
            for (int bx = 0; bx < 16; bx++)
                idx[SubChunk.XzyIndex(bx, by, bz)] = sec.Blocks[ChunkSection.Index(bx, by, bz)];
            var layers = new List<SubChunk.Layer> { new(sec.Palette.ToArray(), idx) };
            if (sec.Native is List<SubChunk.Layer> extra) layers.AddRange(extra);
            writes.Add((ChunkKey(dim, volume.X, volume.Z, TagSubChunk, (sbyte)y), SubChunk.Encode((sbyte)y, layers), (sbyte)y));
        }

        lock (_lock)
        {
            var ys = YsOf(dim, volume.X, volume.Z);
            var keys = KeysOf(dim, volume.X, volume.Z);
            foreach (var (k, val, y) in writes) { _overlay[k] = val; ys.Add(y); keys.Add(k); }
            if (clearBlockEntities)
            {
                var be = ChunkKey(dim, volume.X, volume.Z, TagBlockEntity);
                _overlay[be] = null; keys.Remove(be);
            }
        }
    }

    public void DeleteChunk(int dim, int cx, int cz)
    {
        var keys = AllKeys(dim, cx, cz);
        lock (_lock)
        {
            foreach (var k in keys) _overlay[k] = null;
            _chunks.Remove((dim, cx, cz));
            _chunkKeys.Remove((dim, cx, cz));
        }
    }

    public int ImportChunks(string sourceRoot, int dim, IReadOnlyCollection<(int X, int Z)> chunks)
    {
        var src = Open(sourceRoot);
        int n = 0;
        foreach (var (x, z) in chunks)
        {
            if (!src.HasChunk(dim, x, z)) continue;
            var values = src.AllKeys(dim, x, z).Select(k => (k, src.Get(k))).Where(t => t.Item2 is not null).ToList();
            DeleteChunk(dim, x, z);
            lock (_lock)
            {
                var keys = KeysOf(dim, x, z);
                var ys = YsOf(dim, x, z);
                foreach (var (k, v) in values)
                {
                    _overlay[k] = v;
                    if (TryParseChunkKey(k, out _, out _, out _, out var tag, out var y))
                    {
                        keys.Add(k);
                        if (tag == TagSubChunk) ys.Add(y);
                    }
                }
            }
            n++;
        }
        return n;
    }

    private sealed record ChunkSnap(int Dim, int X, int Z, List<(byte[] Key, byte[]? Value)> Values, SortedSet<sbyte>? Ys);
    private sealed record Snap(List<ChunkSnap> Chunks);

    public object Snapshot(int dim, IEnumerable<(int X, int Z)> chunks)
    {
        var list = new List<ChunkSnap>();
        foreach (var (x, z) in chunks.Distinct())
        {
            var values = AllKeys(dim, x, z).Select(k => (k, Get(k))).ToList();
            SortedSet<sbyte>? ys;
            lock (_lock) ys = _chunks.TryGetValue((dim, x, z), out var s) ? new SortedSet<sbyte>(s) : null;
            list.Add(new ChunkSnap(dim, x, z, values, ys));
        }
        return new Snap(list);
    }

    public void Restore(object snapshot)
    {
        foreach (var c in ((Snap)snapshot).Chunks)
        {
            var current = AllKeys(c.Dim, c.X, c.Z);
            lock (_lock)
            {
                var keep = new HashSet<byte[]>(c.Values.Select(v => v.Key), LevelDbReader.ByteArrayComparer.Instance);
                foreach (var k in current) if (!keep.Contains(k)) _overlay[k] = null;
                var keys = new HashSet<byte[]>(LevelDbReader.ByteArrayComparer.Instance);
                foreach (var (k, v) in c.Values)
                {
                    _overlay[k] = v;
                    if (v is not null && TryParseChunkKey(k, out _, out _, out _, out _, out _)) keys.Add(k);
                }
                if (c.Ys is null) { _chunks.Remove((c.Dim, c.X, c.Z)); _chunkKeys.Remove((c.Dim, c.X, c.Z)); }
                else { _chunks[(c.Dim, c.X, c.Z)] = new SortedSet<sbyte>(c.Ys); _chunkKeys[(c.Dim, c.X, c.Z)] = keys; }
            }
        }
    }

    public void Save()
    {
        List<(byte[], byte[]?)> changes;
        lock (_lock) changes = _overlay.Select(kv => (kv.Key, kv.Value)).ToList();
        _db.Write(changes);
        lock (_lock) _overlay.Clear();

        if (_pendingLevel is { } level)
        {
            var path = Path.Combine(RootPath, "level.dat");
            var old = File.ReadAllBytes(path);
            File.WriteAllBytes(Path.Combine(RootPath, "level.dat_old"), old);
            var body = NbtIO.WriteBedrock(level);
            var file = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(file, BinaryPrimitives.ReadInt32LittleEndian(old)); // depolama sürümü
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), body.Length);
            body.CopyTo(file, 8);
            File.WriteAllBytes(path, file);
            var name = level.Get<NbtString>("LevelName")?.Value;
            if (!string.IsNullOrEmpty(name)) { File.WriteAllText(Path.Combine(RootPath, "levelname.txt"), name); Name = name; }
            _pendingLevel = null;
        }
    }

    // ---------- Yeni chunk ----------
    /// <summary>Aynı boyuttaki rastgele bir var olan chunk: yeni chunk'lar onun biyom/sürüm verisini kopyalar.</summary>
    private (int X, int Z)? TemplateChunk(int dim)
    {
        lock (_lock)
            foreach (var (k, ys) in _chunks)
                if (k.Dim == dim && ys.Count > 0 && Get(ChunkKey(dim, k.X, k.Z, 43)) is not null) return (k.X, k.Z);
        return null;
    }

    public ChunkVolume NewChunk(int dim, int cx, int cz)
    {
        var t = TemplateChunk(dim) ?? throw new InvalidOperationException("Bu boyutta şablon alınacak hiç chunk yok.");
        var tv = ReadChunk(dim, t.X, t.Z)!;
        return new ChunkVolume(cx, cz, tv.MinSectionY, tv.MaxSectionY);
    }

    /// <summary>Var olmayan chunk için gerekli Version / Data3D / FinalizedState anahtarlarını şablondan kopyalar.</summary>
    private void EnsureChunkExists(int dim, int cx, int cz)
    {
        if (HasChunk(dim, cx, cz)) return;
        var t = TemplateChunk(dim) ?? throw new InvalidOperationException("Bu boyutta şablon alınacak hiç chunk yok.");
        foreach (byte tag in new byte[] { 43, TagVersion, TagVersionOld, 54 })
        {
            if (Get(ChunkKey(dim, t.X, t.Z, tag)) is not { } val) continue;
            var key = ChunkKey(dim, cx, cz, tag);
            lock (_lock) { _overlay[key] = val; KeysOf(dim, cx, cz).Add(key); }
        }
        lock (_lock) YsOf(dim, cx, cz);
    }

    // ---------- Blok varlıkları ----------
    public List<NbtCompound> ReadBlockEntities(int dim, int cx, int cz)
    {
        var data = Get(ChunkKey(dim, cx, cz, TagBlockEntity));
        if (data is null || data.Length == 0) return new();
        try { return NbtIO.ReadBedrockMany(data); } catch { return new(); }
    }

    public void WriteBlockEntities(int dim, int cx, int cz, List<NbtCompound> entities)
    {
        var key = ChunkKey(dim, cx, cz, TagBlockEntity);
        lock (_lock)
        {
            if (entities.Count == 0) { _overlay[key] = null; KeysOf(dim, cx, cz).Remove(key); }
            else { _overlay[key] = NbtIO.WriteBedrockMany(entities); KeysOf(dim, cx, cz).Add(key); }
        }
    }

    public NbtCompound NewContainer(string blockState, int x, int y, int z)
    {
        var name = BlockState.BaseName(blockState).Replace("minecraft:", "");
        string id = name switch
        {
            _ when name.EndsWith("shulker_box") => "ShulkerBox",
            "barrel" => "Barrel", "hopper" => "Hopper", "dispenser" => "Dispenser", "dropper" => "Dropper",
            "furnace" => "Furnace", "blast_furnace" => "BlastFurnace", "smoker" => "Smoker",
            _ => "Chest",
        };
        return new NbtCompound("")
        {
            new NbtString("id", id), new NbtInt("x", x), new NbtInt("y", y), new NbtInt("z", z),
            new NbtByte("isMovable", 1), new NbtByte("Findable", 0), new NbtList("Items", NbtTagType.Compound),
        };
    }

    // ---------- Oyuncular ----------
    public IReadOnlyList<PlayerInfo> GetPlayers() => _playerKeys.Select(k =>
    {
        var s = Encoding.UTF8.GetString(k);
        return s == "~local_player"
            ? new PlayerInfo(s, "Yerel oyuncu", "Dünyayı oluşturan / tek oyunculu")
            : new PlayerInfo(s, "Oyuncu " + s["player_server_".Length..][..Math.Min(8, s.Length - 14)], s);
    }).ToList();

    public NbtCompound? ReadPlayer(string id) =>
        Get(Encoding.UTF8.GetBytes(id)) is { } d ? NbtIO.ReadBedrock(d) : null;

    public void WritePlayer(string id, NbtCompound data) => Put(Encoding.UTF8.GetBytes(id), NbtIO.WriteBedrock(data));

    public PlayerLayout PlayerLayout(NbtCompound p)
    {
        var c = BedrockItemCodec.Instance;
        return new PlayerLayout
        {
            Inventory = SlotContainer.Slotted(p, "Inventory", "Envanter", 36, 0, c),
            Armor = SlotContainer.Indexed(p, "Armor", "Zırh", 4, c, 1, i => new[] { "Kask", "Göğüslük", "Pantolon", "Bot" }[i]),
            Offhand = SlotContainer.Indexed(p, "Offhand", "Sol el", 1, c, 1, _ => "Sol el"),
            EnderChest = SlotContainer.Slotted(p, "EnderChestInventory", "Ender Chest", 27, 0, c),
            XpLevelKey = "PlayerLevel",
            GameModeKey = "PlayerGameMode",
            GameModes = new[] { ("0", "Hayatta Kalma"), ("1", "Yaratıcı"), ("2", "Macera"), ("6", "İzleyici"), ("5", "Dünya varsayılanı") },
        };
    }

    // ---------- level.dat ----------
    public NbtCompound ReadLevelData()
    {
        if (_pendingLevel is not null) return (NbtCompound)_pendingLevel.Clone();
        return NbtIO.ReadBedrock(File.ReadAllBytes(Path.Combine(RootPath, "level.dat")), 8);
    }

    public void WriteLevelData(NbtCompound data) => _pendingLevel = (NbtCompound)data.Clone();

    public string DescribeChunk(int dim, int cx, int cz)
    {
        SortedSet<sbyte> ys;
        lock (_lock)
        {
            if (!_chunks.TryGetValue((dim, cx, cz), out var s)) return $"Chunk {cx}, {cz}\n\nOluşturulmamış";
            ys = new SortedSet<sbyte>(s);
        }
        var ver = Get(ChunkKey(dim, cx, cz, TagVersion)) ?? Get(ChunkKey(dim, cx, cz, TagVersionOld));
        var be = Get(ChunkKey(dim, cx, cz, TagBlockEntity));
        var digp = Get(DigpKey(dim, cx, cz));
        var sb = new StringBuilder();
        sb.AppendLine($"Chunk     {cx}, {cz}");
        sb.AppendLine($"Region    {cx >> 5}, {cz >> 5}");
        sb.AppendLine();
        sb.AppendLine($"Sürüm     {(ver is { Length: > 0 } ? ver[0].ToString() : "?")}");
        sb.AppendLine($"SubChunk  {ys.Count}" + (ys.Count > 0 ? $"  (Y {ys.Min * 16} … {ys.Max * 16 + 15})" : ""));
        sb.AppendLine($"TileEnt.  {(be is null ? "yok" : $"{be.Length:N0} bayt")}");
        sb.AppendLine($"Varlık    {(digp?.Length ?? 0) / 8}");
        lock (_lock)
            if (_chunkKeys.TryGetValue((dim, cx, cz), out var keys) && keys.Any(_overlay.ContainsKey))
                sb.AppendLine("\n● Kaydedilmemiş değişiklik");
        return sb.ToString();
    }
}
