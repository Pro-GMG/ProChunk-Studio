using ProChunk.Core;
using ProChunk.Java;

namespace ProChunk.Tests;

public class ChunkReaderTests
{
    [Fact]
    public void Unpack_FourBits_ReadsSequentialValues()
    {
        // 4 bit: her long 16 değer taşır. 0..15 sırayla.
        long packed = 0;
        for (int i = 0; i < 16; i++) packed |= (long)i << (i * 4);
        var data = Enumerable.Repeat(packed, 256).ToArray();

        var result = ChunkReader.Unpack(data, paletteCount: 16);

        Assert.Equal(4096, result.Length);
        for (int i = 0; i < 32; i++) Assert.Equal(i % 16, result[i]);
    }

    [Fact]
    public void Unpack_SinglePalette_IsAllZero() =>
        Assert.All(ChunkReader.Unpack(null, 1), v => Assert.Equal(0, v));

    [Theory]
    [InlineData("r.0.0.mca", true, 0, 0)]
    [InlineData("r.-3.12.mca", true, -3, 12)]
    [InlineData("c.1.2.mcc", false, 0, 0)]
    public void RegionName_IsParsed(string name, bool ok, int rx, int rz)
    {
        Assert.Equal(ok, RegionFile.TryParseName(name, out var x, out var z));
        if (ok) { Assert.Equal(rx, x); Assert.Equal(rz, z); }
    }

    [Fact]
    public void BlockColors_AreStable()
    {
        Assert.Equal(BlockColors.Get("minecraft:some_modded_block"), BlockColors.Get("minecraft:some_modded_block"));
        Assert.True(BlockColors.IsAir("minecraft:cave_air"));
    }
}
