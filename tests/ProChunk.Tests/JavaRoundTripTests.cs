using fNbt;
using ProChunk.Core;
using ProChunk.Java;

namespace ProChunk.Tests;

/// <summary>Sentetik bir Java dünyasında yazma → yeniden okuma testleri.</summary>
public class JavaRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "prochunk_java_" + Guid.NewGuid().ToString("N"));

    public JavaRoundTripTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "region"));
        var level = new NbtCompound("") { new NbtCompound("Data") { new NbtString("LevelName", "Test") } };
        new NbtFile(level).SaveToFile(Path.Combine(_dir, "level.dat"), NbtCompression.GZip);

        // r.0.0.mca içinde (0,0) ve (1,0) chunk'ları: alt yarı taş
        var changes = new Dictionary<int, NbtCompound?>();
        foreach (var cx in new[] { 0, 1 })
        {
            var v = new ChunkVolume(cx, 0, -4, 19);
            for (int y = -64; y < 0; y++)
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++) v.SetBlock(x, y, z, "minecraft:stone");
            changes[cx] = ChunkReader.FromVolume(v, false);
        }
        RegionFile.Write(Path.Combine(_dir, "region", "r.0.0.mca"), null, changes);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Pack_Unpack_RoundTrip()
    {
        var values = Enumerable.Range(0, 4096).Select(i => (ushort)(i % 37)).ToArray();
        var packed = ChunkReader.Pack(values, 37);
        var back = ChunkReader.Unpack(packed, 37);
        Assert.Equal(values.Select(v => (int)v), back);
    }

    [Fact]
    public void EditSaveReopen_PersistsBlocksAndProperties()
    {
        IWorld w = JavaWorld.Open(_dir);
        Assert.True(w.HasChunk(0, 0, 0));
        var v = w.ReadChunk(0, 0, 0)!;
        Assert.Equal("minecraft:stone", v.GetBlock(5, -10, 5));

        v.SetBlock(5, -10, 5, "minecraft:oak_stairs[facing=east,half=bottom]");
        v.SetBlock(6, 70, 6, "minecraft:diamond_block");
        w.WriteChunk(0, v);
        w.DeleteChunk(0, 1, 0);
        w.Save();

        IWorld again = JavaWorld.Open(_dir);
        var v2 = again.ReadChunk(0, 0, 0)!;
        Assert.Equal("minecraft:oak_stairs[facing=east,half=bottom]", v2.GetBlock(5, -10, 5));
        Assert.Equal("minecraft:diamond_block", v2.GetBlock(6, 70, 6));
        Assert.Equal("minecraft:stone", v2.GetBlock(0, -64, 0));
        Assert.False(again.HasChunk(0, 1, 0));
    }

    [Fact]
    public void ReplaceAndUndoSnapshot()
    {
        IWorld w = JavaWorld.Open(_dir);
        var snap = w.Snapshot(0, new[] { (0, 0) });
        var v = w.ReadChunk(0, 0, 0)!;
        Assert.Equal(16 * 16 * 64, v.Replace(s => s == "minecraft:stone" ? "minecraft:dirt" : null));
        w.WriteChunk(0, v);
        Assert.Equal("minecraft:dirt", w.ReadChunk(0, 0, 0)!.GetBlock(1, -1, 1));
        w.Restore(snap);
        Assert.Equal("minecraft:stone", w.ReadChunk(0, 0, 0)!.GetBlock(1, -1, 1));
    }
}
