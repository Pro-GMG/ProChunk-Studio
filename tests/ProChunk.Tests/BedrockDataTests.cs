using System.IO.Compression;
using fNbt;
using ProChunk.Bedrock;
using ProChunk.Core;
using ProChunk.Core.Generation;
using ProChunk.Core.Items;
using Xunit.Abstractions;

namespace ProChunk.Tests;

/// <summary>Örnek .mcworld üzerinde oyuncu / level.dat / blok varlığı / üretim testleri.</summary>
public class BedrockDataTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "prochunk_bd_" + Guid.NewGuid().ToString("N"));
    private readonly bool _ok;

    public BedrockDataTests(ITestOutputHelper output)
    {
        this.output = output;
        var sample = Environment.GetEnvironmentVariable("PROCHUNK_SAMPLE_MCWORLD");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;
        ZipFile.ExtractToDirectory(sample, _dir);
        _ok = true;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void LevelDat_ReadWriteRoundTrip()
    {
        if (!_ok) return;
        IWorld w = BedrockWorld.Open(_dir);
        var level = w.ReadLevelData();
        output.WriteLine($"LevelName={level.Get<NbtString>("LevelName")?.Value}, GameType={level.Get("GameType")}, alan sayısı={level.Count}");
        Assert.NotNull(level.Get<NbtString>("LevelName"));

        level.Remove("LevelName"); level.Add(new NbtString("LevelName", "ProChunk Test"));
        level.Remove("keepinventory"); level.Add(new NbtByte("keepinventory", 1));
        w.WriteLevelData(level);
        w.Save();

        IWorld again = BedrockWorld.Open(_dir);
        var l2 = again.ReadLevelData();
        Assert.Equal("ProChunk Test", l2.Get<NbtString>("LevelName")!.Value);
        Assert.Equal(1, l2.Get<NbtByte>("keepinventory")!.Value);
        Assert.Equal(level.Count, l2.Count);
        Assert.Equal("ProChunk Test", again.Name);
    }

    [Fact]
    public void Player_InventoryAndEnderChest_RoundTrip()
    {
        if (!_ok) return;
        IWorld w = BedrockWorld.Open(_dir);
        var players = w.GetPlayers();
        output.WriteLine("Oyuncular: " + string.Join(", ", players.Select(p => p.Id)));
        Assert.NotEmpty(players);

        var id = players[0].Id;
        var p = w.ReadPlayer(id)!;
        var layout = w.PlayerLayout(p);
        int filled = Enumerable.Range(0, 36).Count(i => layout.Inventory.Get(i) is not null);
        output.WriteLine(string.Join(" | ", Enumerable.Range(0, 36).Select(i => layout.Inventory.Get(i) is { } it ? $"{i}:{w.Items.GetId(it)}x{w.Items.GetCount(it)}" : null).Where(s => s != null)));
        output.WriteLine($"Envanterde {filled} dolu yuva, XP={p.Get(layout.XpLevelKey)}");

        layout.Inventory.Set(0, w.Items.Create("minecraft:diamond_sword", 1));
        layout.EnderChest.Set(26, w.Items.Create("minecraft:emerald", 64));
        layout.Armor.Set(0, w.Items.Create("minecraft:netherite_helmet", 1));
        var shulker = w.Items.Create("minecraft:red_shulker_box", 1);
        w.Items.Nested(shulker)!.Set(3, w.Items.Create("minecraft:golden_apple", 16));
        layout.Inventory.Set(9, shulker);
        w.WritePlayer(id, p);
        w.Save();

        IWorld again = BedrockWorld.Open(_dir);
        var p2 = again.ReadPlayer(id)!;
        var l2 = again.PlayerLayout(p2);
        Assert.Equal("minecraft:diamond_sword", again.Items.GetId(l2.Inventory.Get(0)!));
        Assert.Equal(64, again.Items.GetCount(l2.EnderChest.Get(26)!));
        Assert.Equal("minecraft:netherite_helmet", again.Items.GetId(l2.Armor.Get(0)!));
        var nested = again.Items.Nested(l2.Inventory.Get(9)!)!;
        Assert.Equal("minecraft:golden_apple", again.Items.GetId(nested.Get(3)!));
        Assert.Equal(16, again.Items.GetCount(nested.Get(3)!));
        Assert.Equal(36, p2.Get<NbtList>("Inventory")!.Count);   // Bedrock: 36 girdi korunur
    }

    [Fact]
    public void Chest_PlaceAndFill_RoundTrip()
    {
        if (!_ok) return;
        IWorld w = BedrockWorld.Open(_dir);
        var c = w.GetRegions(0).SelectMany(r => Enumerable.Range(0, 1024).Select(i => (X: r.X * 32 + i % 32, Z: r.Z * 32 + i / 32)))
            .First(c => w.HasChunk(0, c.X, c.Z));
        var v = w.ReadChunk(0, c.X, c.Z)!;
        int y = 100;
        v.SetBlock(5, y, 5, "minecraft:chest");
        w.WriteChunk(0, v);

        var list = w.ReadBlockEntities(0, c.X, c.Z);
        int before = list.Count;
        var chest = w.NewContainer("minecraft:chest", c.X * 16 + 5, y, c.Z * 16 + 5);
        var items = SlotContainer.Slotted(chest, "Items", "Sandık", 27, 0, w.Items);
        items.Set(0, w.Items.Create("minecraft:diamond", 32));
        items.Set(26, w.Items.Create("minecraft:blue_shulker_box", 1));
        list.Add(chest);
        w.WriteBlockEntities(0, c.X, c.Z, list);
        w.Save();

        IWorld again = BedrockWorld.Open(_dir);
        var l2 = again.ReadBlockEntities(0, c.X, c.Z);
        Assert.Equal(before + 1, l2.Count);
        var ch = l2.Single(e => e.Get<NbtInt>("x")!.Value == c.X * 16 + 5 && e.Get<NbtInt>("y")!.Value == y);
        var it = SlotContainer.Slotted(ch, "Items", "", 27, 0, again.Items);
        Assert.Equal(32, again.Items.GetCount(it.Get(0)!));
        Assert.Equal("minecraft:chest", again.ReadChunk(0, c.X, c.Z)!.GetBlock(5, y, 5));
        output.WriteLine($"Sandık {c.X * 16 + 5},{y},{c.Z * 16 + 5}: tamam (önceki blok varlığı: {before})");
    }

    [Fact]
    public void Generate_NewChunks_Exist()
    {
        if (!_ok) return;
        IWorld w = BedrockWorld.Open(_dir);
        var target = (X: 5000, Z: 5000);
        Assert.False(w.HasChunk(0, target.X, target.Z));
        var v = w.NewChunk(0, target.X, target.Z);
        TerrainGenerator.Fill(v, new TerrainOptions { Kind = TerrainKind.Hills });
        w.WriteChunk(0, v);
        w.Save();

        IWorld again = BedrockWorld.Open(_dir);
        Assert.True(again.HasChunk(0, target.X, target.Z));
        var v2 = again.ReadChunk(0, target.X, target.Z)!;
        Assert.Equal("minecraft:bedrock", v2.GetBlock(0, v2.MinY, 0));
        Assert.Contains(v2.CountBlocks().Keys, k => k == "minecraft:stone");
    }
}
