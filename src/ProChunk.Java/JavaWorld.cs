using System.Collections.Concurrent;
using System.Text;
using fNbt;
using ProChunk.Core;
using ProChunk.Core.Items;
using ProChunk.Core.Nbt;

namespace ProChunk.Java;

public enum Dimension { Overworld = 0, Nether = 1, End = 2 }

/// <summary>Bir Java Edition dünya klasörü. Düzenlemeler bellekte tutulur, Save() ile yazılır.</summary>
public sealed class JavaWorld : IWorld
{
    private readonly ConcurrentDictionary<int, Dictionary<(int X, int Z), string>> _regions = new();
    private readonly ConcurrentDictionary<string, RegionFile> _regionCache = new();
    private readonly Dictionary<(int Dim, int X, int Z), NbtCompound?> _overlay = new();
    private readonly object _lock = new();

    private readonly Dictionary<string, NbtCompound> _pendingPlayers = new();
    private NbtCompound? _pendingLevel;
    private readonly int _dataVersion;

    public string RootPath { get; }
    public string Name { get; private set; }
    public string Edition => "Java";
    public IItemCodec Items { get; }
    public bool IsDirty { get { lock (_lock) return _overlay.Count > 0 || _pendingPlayers.Count > 0 || _pendingLevel is not null; } }

    private JavaWorld(string root, string name)
    {
        RootPath = root; Name = name;
        try { _dataVersion = NbtIO.ReadJavaFile(Path.Combine(root, "level.dat")).Get<NbtCompound>("Data")?.Get<NbtInt>("DataVersion")?.Value ?? 0; }
        catch { _dataVersion = 0; }
        Items = new JavaItemCodec(_dataVersion >= 3837);
    }

    public static bool IsJavaWorld(string folder) =>
        File.Exists(Path.Combine(folder, "level.dat")) &&
        (Directory.Exists(Path.Combine(folder, "region")) || Directory.Exists(Path.Combine(folder, "dimensions", "minecraft", "overworld", "region")));

    /// <summary>Yeni düzen (26.x+): boyutlar dimensions/minecraft/&lt;ad&gt;/ altında.</summary>
    private bool NewLayout => Directory.Exists(Path.Combine(RootPath, "dimensions", "minecraft", "overworld", "region"))
                              && !Directory.Exists(Path.Combine(RootPath, "region"));

    public static JavaWorld Open(string folder)
    {
        if (!IsJavaWorld(folder)) throw new DirectoryNotFoundException("Bu klasör bir Java dünyası değil (level.dat veya region/ yok).");
        return new JavaWorld(folder, ReadLevelName(folder) ?? Path.GetFileName(folder));
    }

    public static string? ReadLevelName(string folder)
    {
        try
        {
            var f = new NbtFile(Path.Combine(folder, "level.dat"));
            return f.RootTag.Get<NbtCompound>("Data")?.Get<NbtString>("LevelName")?.Value;
        }
        catch { return null; }
    }

    public string RegionFolder(Dimension dim) => NewLayout
        ? Path.Combine(RootPath, "dimensions", "minecraft", dim switch { Dimension.Nether => "the_nether", Dimension.End => "the_end", _ => "overworld" }, "region")
        : dim switch
    {
        Dimension.Nether => Path.Combine(RootPath, "DIM-1", "region"),
        Dimension.End => Path.Combine(RootPath, "DIM1", "region"),
        _ => Path.Combine(RootPath, "region"),
    };

    private string RegionPath(int dim, int rx, int rz) => Path.Combine(RegionFolder((Dimension)dim), $"r.{rx}.{rz}.mca");

    /// <summary>Boyuttaki tüm region dosyaları: (rx, rz) → yol.</summary>
    public Dictionary<(int X, int Z), string> ListRegions(Dimension dim) =>
        _regions.GetOrAdd((int)dim, _ =>
        {
            var result = new Dictionary<(int, int), string>();
            var dir = RegionFolder(dim);
            if (!Directory.Exists(dir)) return result;
            foreach (var f in Directory.EnumerateFiles(dir, "r.*.mca"))
                if (RegionFile.TryParseName(Path.GetFileName(f), out var rx, out var rz) && new FileInfo(f).Length >= 8192)
                    result[(rx, rz)] = f;
            return result;
        });

    private RegionFile? Region(int dim, int rx, int rz)
    {
        if (!ListRegions((Dimension)dim).TryGetValue((rx, rz), out var p)) return null;
        return _regionCache.GetOrAdd(p, RegionFile.Open);
    }

    private NbtCompound? ReadRoot(int dim, int cx, int cz)
    {
        lock (_lock)
            if (_overlay.TryGetValue((dim, cx, cz), out var o)) return o;
        return Region(dim, cx >> 5, cz >> 5)?.ReadChunk(cx & 31, cz & 31);
    }

    // ---------- IWorld ----------
    public IReadOnlySet<(int X, int Z)> GetRegions(int dimension)
    {
        var set = ListRegions((Dimension)dimension).Keys.ToHashSet();
        lock (_lock)
            foreach (var (k, v) in _overlay)
                if (k.Dim == dimension && v is not null) set.Add((k.X >> 5, k.Z >> 5));
        return set;
    }

    public bool HasChunk(int dim, int cx, int cz)
    {
        lock (_lock)
            if (_overlay.TryGetValue((dim, cx, cz), out var o)) return o is not null;
        return Region(dim, cx >> 5, cz >> 5)?.HasChunk(cx & 31, cz & 31) == true;
    }

    public ChunkVolume? ReadChunk(int dim, int cx, int cz) =>
        ReadRoot(dim, cx, cz) is { } root ? ChunkReader.ToVolume(root, cx, cz) : null;

    public void WriteChunk(int dim, ChunkVolume volume, bool clearBlockEntities = false)
    {
        var root = ChunkReader.FromVolume(volume, clearBlockEntities);
        lock (_lock) _overlay[(dim, volume.X, volume.Z)] = root;
    }

    public void DeleteChunk(int dim, int cx, int cz)
    {
        lock (_lock) _overlay[(dim, cx, cz)] = null;
    }

    public int ImportChunks(string sourceRoot, int dim, IReadOnlyCollection<(int X, int Z)> chunks)
    {
        var src = Open(sourceRoot);
        int n = 0;
        foreach (var (x, z) in chunks)
        {
            if (src.ReadRoot(dim, x, z) is not { } root) continue;
            lock (_lock) _overlay[(dim, x, z)] = root;
            n++;
        }
        return n;
    }

    /// <summary>Chunk'ın durumu (ör. "minecraft:full"); yoksa null.</summary>
    public string? ChunkStatus(int dim, int cx, int cz) => ReadRoot(dim, cx, cz)?.Get<NbtString>("Status")?.Value;

    private sealed record Snap(int Dim, List<((int X, int Z) Pos, NbtCompound? Root)> Items);

    public object Snapshot(int dim, IEnumerable<(int X, int Z)> chunks) =>
        new Snap(dim, chunks.Distinct().Select(c => (c, ReadRoot(dim, c.X, c.Z))).ToList());

    public void Restore(object snapshot)
    {
        var s = (Snap)snapshot;
        lock (_lock)
            foreach (var (pos, root) in s.Items) _overlay[(s.Dim, pos.X, pos.Z)] = root;
    }

    public void Save()
    {
        List<IGrouping<(int Dim, int RX, int RZ), KeyValuePair<(int Dim, int X, int Z), NbtCompound?>>> groups;
        lock (_lock) groups = _overlay.GroupBy(kv => (kv.Key.Dim, kv.Key.X >> 5, kv.Key.Z >> 5)).ToList();

        foreach (var g in groups)
        {
            var path = RegionPath(g.Key.Dim, g.Key.RX, g.Key.RZ);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var original = File.Exists(path) ? RegionFile.Open(path) : null;
            var changes = g.ToDictionary(kv => (kv.Key.X & 31) + (kv.Key.Z & 31) * 32, kv => kv.Value);
            RegionFile.Write(path, original, changes);
            _regionCache.TryRemove(path, out _);
        }
        lock (_lock) _overlay.Clear();
        _regions.Clear();

        foreach (var (path, data) in _pendingPlayers)
        {
            if (path == LevelPlayerId)
            {
                var level = _pendingLevel ?? ReadLevelData();
                var copy = (NbtCompound)data.Clone(); copy.Name = "Player";
                NbtIO.Set(level, copy);
                _pendingLevel = level;
            }
            else NbtIO.WriteJavaFile(path, data);
        }
        _pendingPlayers.Clear();

        if (_pendingLevel is { } lvl)
        {
            var path = Path.Combine(RootPath, "level.dat");
            File.Copy(path, Path.Combine(RootPath, "level.dat_old"), overwrite: true);
            var root = NbtIO.ReadJavaFile(path);
            var data = (NbtCompound)lvl.Clone(); data.Name = "Data";
            NbtIO.Set(root, data);
            NbtIO.WriteJavaFile(path, root);
            if (lvl.Get<NbtString>("LevelName")?.Value is { Length: > 0 } n) Name = n;
            _pendingLevel = null;
        }
    }

    // ---------- Yeni chunk ----------
    private NbtCompound? TemplateRoot(int dim)
    {
        foreach (var (pos, _) in ListRegions((Dimension)dim).OrderBy(r => Math.Abs(r.Key.X) + Math.Abs(r.Key.Z)))
        {
            var region = Region(dim, pos.X, pos.Z);
            if (region is null) continue;
            for (int i = 0; i < 1024; i++)
                if (region.HasChunk(i & 31, i >> 5) && region.ReadChunk(i & 31, i >> 5) is { } root
                    && root.Get<NbtString>("Status")?.Value is "minecraft:full" or "full")
                    return root;
        }
        return null;
    }

    public ChunkVolume NewChunk(int dim, int cx, int cz)
    {
        var t = TemplateRoot(dim) ?? throw new InvalidOperationException("Bu boyutta şablon alınacak tamamlanmış chunk yok.");
        var root = (NbtCompound)t.Clone();
        NbtIO.Set(root, new NbtInt("xPos", cx));
        NbtIO.Set(root, new NbtInt("zPos", cz));
        NbtIO.Set(root, new NbtLong("InhabitedTime", 0));
        foreach (var k in new[] { "block_entities", "block_ticks", "fluid_ticks", "entities" })
            NbtIO.Set(root, new NbtList(k, NbtTagType.Compound));
        root.Remove("structures");
        root.Add(new NbtCompound("structures") { new NbtCompound("References"), new NbtCompound("starts") });
        root.Remove("PostProcessing");
        root.Remove("Heightmaps");

        var v = ChunkReader.ToVolume(root, cx, cz);
        foreach (var s in v.Sections.Values) { Array.Clear(s.Blocks); s.Palette.Clear(); s.Palette.Add(BlockState.Air); }
        return v;
    }

    // ---------- Blok varlıkları ----------
    public List<NbtCompound> ReadBlockEntities(int dim, int cx, int cz) =>
        ReadRoot(dim, cx, cz)?.Get<NbtList>("block_entities")?.OfType<NbtCompound>().Select(c => (NbtCompound)c.Clone()).ToList() ?? new();

    public void WriteBlockEntities(int dim, int cx, int cz, List<NbtCompound> entities)
    {
        var root = ReadRoot(dim, cx, cz) ?? throw new InvalidOperationException("Chunk oluşturulmamış.");
        root = (NbtCompound)root.Clone();
        var list = new NbtList("block_entities", NbtTagType.Compound);
        foreach (var e in entities) { var c = (NbtCompound)e.Clone(); c.Name = null; list.Add(c); }
        NbtIO.Set(root, list);
        lock (_lock) _overlay[(dim, cx, cz)] = root;
    }

    public NbtCompound NewContainer(string blockState, int x, int y, int z) => new("")
    {
        new NbtString("id", BlockState.BaseName(blockState) switch
        {
            "minecraft:trapped_chest" => "minecraft:trapped_chest",
            var n when n.EndsWith("shulker_box") => "minecraft:shulker_box",
            var n => n,
        }),
        new NbtInt("x", x), new NbtInt("y", y), new NbtInt("z", z),
        new NbtByte("keepPacked", 0), new NbtList("Items", NbtTagType.Compound),
    };

    // ---------- Oyuncular ----------
    private const string LevelPlayerId = "level.dat/Player";
    private string PlayerDataDir => Directory.Exists(Path.Combine(RootPath, "players", "data")) && !Directory.Exists(Path.Combine(RootPath, "playerdata"))
        ? Path.Combine(RootPath, "players", "data") : Path.Combine(RootPath, "playerdata");

    public IReadOnlyList<PlayerInfo> GetPlayers()
    {
        var list = new List<PlayerInfo>();
        try
        {
            if (ReadLevelData().Get<NbtCompound>("Player") is not null)
                list.Add(new PlayerInfo(LevelPlayerId, "Tek oyunculu oyuncu", "level.dat içindeki oyuncu"));
        }
        catch { }
        if (Directory.Exists(PlayerDataDir))
            foreach (var f in Directory.EnumerateFiles(PlayerDataDir, "*.dat"))
                list.Add(new PlayerInfo(f, "Oyuncu " + Path.GetFileNameWithoutExtension(f)[..8], Path.GetFileNameWithoutExtension(f)));
        return list;
    }

    public NbtCompound? ReadPlayer(string id)
    {
        if (_pendingPlayers.TryGetValue(id, out var p)) return (NbtCompound)p.Clone();
        if (id == LevelPlayerId) return ReadLevelData().Get<NbtCompound>("Player") is { } lp ? (NbtCompound)lp.Clone() : null;
        return File.Exists(id) ? NbtIO.ReadJavaFile(id) : null;
    }

    public void WritePlayer(string id, NbtCompound data) => _pendingPlayers[id] = (NbtCompound)data.Clone();

    public PlayerLayout PlayerLayout(NbtCompound p)
    {
        var c = Items;
        string[] armorLabels = { "Kask", "Göğüslük", "Pantolon", "Bot" };
        bool equipment = p.Contains("equipment") || _dataVersion >= 4325; // 1.21.5+
        return new PlayerLayout
        {
            Inventory = SlotContainer.Slotted(p, "Inventory", "Envanter", 36, 0, c),
            Armor = equipment
                ? SlotContainer.Keyed(p, "equipment", "Zırh", new[] { "head", "chest", "legs", "feet" }, c, i => armorLabels[i])
                : new SlotContainer("Zırh", 4, 1,      // 103 kask … 100 bot
                    i => SlotContainer.Slotted(p, "Inventory", "", 1, 103 - i, c).Get(0),
                    (i, it) => SlotContainer.Slotted(p, "Inventory", "", 1, 103 - i, c).Set(0, it), c) { SlotLabel = i => armorLabels[i] },
            Offhand = equipment
                ? SlotContainer.Keyed(p, "equipment", "Sol el", new[] { "offhand" }, c, _ => "Sol el")
                : SlotContainer.Slotted(p, "Inventory", "Sol el", 1, -106, c, 1, _ => "Sol el"),
            EnderChest = SlotContainer.Slotted(p, "EnderItems", "Ender Chest", 27, 0, c),
            XpLevelKey = "XpLevel",
            GameModeKey = "playerGameType",
            GameModes = new[] { ("0", "Hayatta Kalma"), ("1", "Yaratıcı"), ("2", "Macera"), ("3", "İzleyici") },
        };
    }

    // ---------- level.dat ----------
    public NbtCompound ReadLevelData()
    {
        if (_pendingLevel is not null) return (NbtCompound)_pendingLevel.Clone();
        return NbtIO.ReadJavaFile(Path.Combine(RootPath, "level.dat")).Get<NbtCompound>("Data") is { } d
            ? (NbtCompound)d.Clone() : new NbtCompound("Data");
    }

    public void WriteLevelData(NbtCompound data) => _pendingLevel = (NbtCompound)data.Clone();

    public string DescribeChunk(int dim, int cx, int cz)
    {
        var root = ReadRoot(dim, cx, cz);
        if (root is null) return $"Chunk {cx}, {cz}\n\nOluşturulmamış";
        var sb = new StringBuilder();
        sb.AppendLine($"Chunk     {cx}, {cz}");
        sb.AppendLine($"Region    r.{cx >> 5}.{cz >> 5}.mca");
        sb.AppendLine();
        sb.AppendLine($"Durum     {root.Get<NbtString>("Status")?.Value ?? "?"}");
        sb.AppendLine($"DataVer.  {root.Get<NbtInt>("DataVersion")?.Value.ToString() ?? "?"}");
        var inhabited = root.Get<NbtLong>("InhabitedTime")?.Value ?? 0;
        sb.AppendLine($"Oyuncu    {TimeSpan.FromSeconds(inhabited / 20.0):hh\\:mm\\:ss}");
        sb.AppendLine($"Section   {root.Get<NbtList>("sections")?.Count ?? 0}");
        sb.AppendLine($"TileEnt.  {root.Get<NbtList>("block_entities")?.Count ?? 0}");
        lock (_lock) if (_overlay.ContainsKey((dim, cx, cz))) sb.AppendLine("\n● Kaydedilmemiş değişiklik");
        if (root.Get<NbtCompound>("Level") is not null) sb.AppendLine("\n⚠ Eski format (1.17 öncesi)");
        return sb.ToString();
    }

    /// <summary>.minecraft/saves altındaki Java dünyaları.</summary>
    public static IEnumerable<(string Name, string Path)> FindSaves()
    {
        var saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "saves");
        if (!Directory.Exists(saves)) yield break;
        foreach (var d in Directory.EnumerateDirectories(saves).OrderByDescending(Directory.GetLastWriteTime))
            if (IsJavaWorld(d))
                yield return (ReadLevelName(d) ?? Path.GetFileName(d), d);
    }
}
