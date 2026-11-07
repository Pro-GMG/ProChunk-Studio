using fNbt;
using ProChunk.Core.Items;

namespace ProChunk.Core;

public sealed record PlayerInfo(string Id, string DisplayName, string Detail);

/// <summary>
/// Java ve Bedrock dünyaları için ortak arayüz. Boyut: 0=Overworld, 1=Nether, 2=End.
/// Değişiklikler bellekte tutulur; <see cref="Save"/> çağrılana kadar diske yazılmaz.
/// </summary>
public interface IWorld
{
    string Name { get; }
    string RootPath { get; }
    string Edition { get; }
    IItemCodec Items { get; }

    /// <summary>Kaydedilmemiş değişiklik var mı?</summary>
    bool IsDirty { get; }

    // ---------- Chunk'lar ----------
    IReadOnlySet<(int X, int Z)> GetRegions(int dimension);
    bool HasChunk(int dimension, int cx, int cz);
    ChunkVolume? ReadChunk(int dimension, int cx, int cz);

    /// <summary>Var olmayan bir chunk için boş (tamamen hava) bir volume. Yazılınca chunk oluşturulur.</summary>
    ChunkVolume NewChunk(int dimension, int cx, int cz);

    /// <summary>Blok verisini yazar. clearBlockEntities: sandık vb. verilerini temizle (yapıştırmada).</summary>
    void WriteChunk(int dimension, ChunkVolume volume, bool clearBlockEntities = false);
    void DeleteChunk(int dimension, int cx, int cz);

    /// <summary>
    /// Aynı sürümden başka bir dünya klasöründeki chunk'ları (bloklar, biyomlar, blok varlıkları) bu dünyaya kopyalar.
    /// Resmi sunucuyla üretilen chunk'ları geri almak için kullanılır. Dönüş: alınan chunk sayısı.
    /// </summary>
    int ImportChunks(string sourceRoot, int dimension, IReadOnlyCollection<(int X, int Z)> chunks);

    // ---------- Blok varlıkları (sandık, shulker, tabela...) ----------
    List<NbtCompound> ReadBlockEntities(int dimension, int cx, int cz);
    void WriteBlockEntities(int dimension, int cx, int cz, List<NbtCompound> entities);

    /// <summary>Yeni konteyner blok varlığı (dünya koordinatı).</summary>
    NbtCompound NewContainer(string blockState, int x, int y, int z);

    // ---------- Oyuncular ----------
    IReadOnlyList<PlayerInfo> GetPlayers();
    NbtCompound? ReadPlayer(string id);
    void WritePlayer(string id, NbtCompound data);
    PlayerLayout PlayerLayout(NbtCompound player);

    // ---------- Dünya ayarları (level.dat) ----------
    NbtCompound ReadLevelData();
    void WriteLevelData(NbtCompound data);

    // ---------- Geri alma ----------
    object Snapshot(int dimension, IEnumerable<(int X, int Z)> chunks);
    void Restore(object snapshot);

    string DescribeChunk(int dimension, int cx, int cz);
    void Save();

    /// <summary>Region'un 512x512 kuşbakışı görüntüsü (0xAARRGGBB, 0 = boş).</summary>
    uint[] RenderRegion(int dimension, int rx, int rz) =>
        MapPainter.RenderRegion(rx, rz, (cx, cz) => ReadChunk(dimension, cx, cz));
}

/// <summary>Oyuncu NBT'sindeki envanter bölümleri ve temel alanların adları.</summary>
public sealed class PlayerLayout
{
    public required SlotContainer Inventory { get; init; }      // 36 yuva: 0-8 hotbar, 9-35 ana
    public required SlotContainer Armor { get; init; }          // 4 yuva: kask, göğüslük, pantolon, bot
    public required SlotContainer Offhand { get; init; }        // 1 yuva
    public required SlotContainer EnderChest { get; init; }     // 27 yuva
    public required string XpLevelKey { get; init; }
    public required string GameModeKey { get; init; }
    public required (string Value, string Label)[] GameModes { get; init; }
}
