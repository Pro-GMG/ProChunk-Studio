using System.IO.Compression;
using ProChunk.Bedrock;
using Xunit.Abstractions;

namespace ProChunk.Tests;

/// <summary>PROCHUNK_SAMPLE_MCWORLD ortam değişkeni bir .mcworld dosyasını gösteriyorsa çalışır.</summary>
public class SampleWorldTests(ITestOutputHelper output)
{
    [Fact]
    public void BedrockSample_LoadsAndRenders()
    {
        var sample = Environment.GetEnvironmentVariable("PROCHUNK_SAMPLE_MCWORLD");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;

        var dir = Path.Combine(Path.GetTempPath(), "prochunk_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(sample, dir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var world = BedrockWorld.Open(dir);
            output.WriteLine($"Ad: {world.Name}, chunk: {world.ChunkCount}, indeks: {sw.ElapsedMilliseconds} ms");
            Assert.True(world.ChunkCount > 0);

            Core.IWorld iw = world;
            var regions = iw.GetRegions(0);
            var r = regions.OrderBy(k => k.X * k.X + k.Z * k.Z).First();
            sw.Restart();
            var px = iw.RenderRegion(0, r.X, r.Z);
            int filled = px.Count(p => p != 0), unknown = px.Count(p => p == Core.MapPainter.UnknownChunk);
            output.WriteLine($"Region {r}: {filled} piksel dolu, {unknown} bilinmeyen, {sw.ElapsedMilliseconds} ms, toplam region {regions.Count}");
            Assert.True(filled > unknown);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void BedrockSample_EditSaveReopen_PersistsChanges()
    {
        var sample = Environment.GetEnvironmentVariable("PROCHUNK_SAMPLE_MCWORLD");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;

        var dir = Path.Combine(Path.GetTempPath(), "prochunk_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(sample, dir);
            Core.IWorld world = BedrockWorld.Open(dir);
            var regions = world.GetRegions(0);
            var r = regions.OrderBy(k => k.X * k.X + k.Z * k.Z).First();

            // Region içinde iki var olan chunk bul: biri düzenlenecek, biri silinecek
            var chunks = Enumerable.Range(0, 1024).Select(i => (X: r.X * 32 + i % 32, Z: r.Z * 32 + i / 32))
                .Where(c => world.HasChunk(0, c.X, c.Z)).Take(2).ToList();
            Assert.Equal(2, chunks.Count);
            var (ex, ez) = chunks[0];
            var (dx, dz) = chunks[1];

            var v = world.ReadChunk(0, ex, ez)!;
            int y = v.MaxY - 1;
            v.SetBlock(3, y, 5, "minecraft:diamond_block");
            v.SetBlock(4, y, 5, "minecraft:gold_block");
            int replaced = v.Replace(s => Core.BlockState.BaseName(s) == "minecraft:stone" ? "minecraft:glass" : null);
            world.WriteChunk(0, v);
            world.DeleteChunk(0, dx, dz);
            Assert.True(world.IsDirty);
            world.Save();
            Assert.False(world.IsDirty);

            // Yeniden aç: değişiklikler yeni .log dosyasından okunmalı
            Core.IWorld again = BedrockWorld.Open(dir);
            var v2 = again.ReadChunk(0, ex, ez)!;
            Assert.Equal("minecraft:diamond_block", v2.GetBlock(3, y, 5));
            Assert.Equal("minecraft:gold_block", v2.GetBlock(4, y, 5));
            Assert.DoesNotContain(v2.CountBlocks().Keys, k => Core.BlockState.BaseName(k) == "minecraft:stone");
            Assert.False(again.HasChunk(0, dx, dz));
            output.WriteLine($"Düzenlenen {ex},{ez} Y={y}, {replaced} taş → cam, silinen {dx},{dz}: tamam");

            // Geri al mantığı: snapshot → değiştir → restore
            var snap = again.Snapshot(0, new[] { (ex, ez) });
            again.DeleteChunk(0, ex, ez);
            Assert.False(again.HasChunk(0, ex, ez));
            again.Restore(snap);
            Assert.Equal("minecraft:diamond_block", again.ReadChunk(0, ex, ez)!.GetBlock(3, y, 5));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
