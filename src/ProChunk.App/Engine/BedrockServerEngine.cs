using System.IO;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using ProChunk.Bedrock;

namespace ProChunk.App.Engine;

/// <summary>Bedrock Dedicated Server ile chunk üretimi: tickingarea ile chunk'ları yükletip kaydettirir.</summary>
public sealed class BedrockServerEngine : ServerEngine
{
    private static readonly string[] DimIds = { "overworld", "nether", "the_end" };

    public override async Task<string> GenerateAsync(string worldRoot, int dim, IReadOnlyCollection<(int X, int Z)> chunks,
        IProgress<string> progress, CancellationToken ct)
    {
        var serverDir = await EnsureServerAsync(progress, ct);

        // 1) Dünyanın kopyası: hedef chunk'lar silinir ki sunucu onları yeniden üretsin
        var name = $"prochunk_{DateTime.Now:yyyyMMdd_HHmmss}";
        var copy = Path.Combine(serverDir, "worlds", name);
        progress.Report("Dünya kopyalanıyor…");
        CopyDirectory(worldRoot, copy);
        var w = BedrockWorld.Open(copy);
        foreach (var (x, z) in chunks) if (w.HasChunk(dim, x, z)) w.DeleteChunk(dim, x, z);
        w.Save();

        SetProperties(Path.Combine(serverDir, "server.properties"), new()
        {
            ["level-name"] = name, ["server-port"] = FreePort(System.Net.Sockets.ProtocolType.Udp).ToString(),
            ["server-portv6"] = FreePort(System.Net.Sockets.ProtocolType.Udp).ToString(), ["online-mode"] = "true", // boş izin listesi: kimse bağlanamaz
            ["allow-list"] = "true", ["view-distance"] = "6", ["tick-distance"] = "4", ["max-players"] = "1",
            ["content-log-file-enabled"] = "false", ["emit-server-telemetry"] = "false", ["server-name"] = "ProChunk Studio",
        });

        // 2) Sunucuyu başlat
        progress.Report("Bedrock Dedicated Server başlatılıyor… (Windows güvenlik duvarı izin isteyebilir; yerel çalıştığı için reddedebilirsin)");
        using var server = new ServerProcess(Path.Combine(serverDir, "bedrock_server.exe"), "", serverDir, progress);
        await server.WaitForAsync(l => l.Contains("Server started"), 0, TimeSpan.FromMinutes(4), ct, "açılış");

        // 3) Tickingarea'larla üret: alan başına en fazla 100 chunk, aynı anda en fazla 10 alan
        var rects = Rectangles(chunks, 10);
        int done = 0, n = 0;
        foreach (var batch in rects.Chunk(10))
        {
            foreach (var (x0, z0, x1, z1) in batch)
                server.Send($"execute in {DimIds[dim]} run tickingarea add {x0 * 16} 0 {z0 * 16} {x1 * 16 + 15} 0 {z1 * 16 + 15} pc{n++} true");
            int count = batch.Sum(r => (r.MaxX - r.MinX + 1) * (r.MaxZ - r.MinZ + 1));
            // Üretimin tamamlanması için bekle (Bedrock'ta "üretildi" bildirimi yok)
            var wait = TimeSpan.FromSeconds(Math.Clamp(6 + count * 0.12, 8, 90));
            for (var t = TimeSpan.Zero; t < wait; t += TimeSpan.FromSeconds(1))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                progress.Report($"Üretiliyor… ~{done + (int)(count * Math.Min(1, t / wait))} / {chunks.Count} chunk");
            }
            done += count;
            server.Send($"execute in {DimIds[dim]} run tickingarea remove_all");
            await Task.Delay(500, ct);
        }

        // 4) Kapat (stop kaydeder)
        progress.Report("Sunucu kaydediyor ve kapanıyor…");
        server.Send("stop");
        await server.WaitExitAsync(TimeSpan.FromMinutes(2));
        return copy;
    }

    /// <summary>Kopya dünyayı sunucu klasöründen siler (içe aktarma sonrası).</summary>
    public static void Cleanup(string copy)
    {
        try { if (copy.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(copy, true); } catch { }
    }

    private static async Task<string> EnsureServerAsync(IProgress<string> progress, CancellationToken ct)
    {
        var baseDir = Path.Combine(Root, "bedrock");
        progress.Report("Bedrock Dedicated Server sürümü denetleniyor…");
        string? url = null;
        try
        {
            var links = await Http.GetFromJsonAsync<JsonElement>("https://net-secondary.web.minecraft-services.net/api/v1.0/download/links", ct);
            url = links.GetProperty("result").GetProperty("links").EnumerateArray()
                .FirstOrDefault(l => l.GetProperty("downloadType").GetString() == "serverBedrockWindows").GetProperty("downloadUrl").GetString();
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Çevrimdışı: daha önce indirilmiş en yeni sürümü kullan
            var existing = Directory.Exists(baseDir) ? Directory.EnumerateDirectories(baseDir).OrderByDescending(d => d).FirstOrDefault() : null;
            if (existing is not null && File.Exists(Path.Combine(existing, "bedrock_server.exe"))) return existing;
            throw;
        }
        if (url is null) throw new InvalidOperationException("Bedrock Dedicated Server indirme adresi alınamadı.");

        var version = Path.GetFileNameWithoutExtension(url).Replace("bedrock-server-", "");
        var dir = Path.Combine(baseDir, version);
        if (File.Exists(Path.Combine(dir, "bedrock_server.exe"))) return dir;

        var zip = Path.Combine(baseDir, $"bedrock-server-{version}.zip");
        progress.Report($"Bedrock Dedicated Server {version} indiriliyor…");
        await DownloadAsync(url, zip, progress, ct);
        progress.Report("Sunucu dosyaları çıkarılıyor…");
        ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        File.Delete(zip);
        return dir;
    }

    private static void SetProperties(string path, Dictionary<string, string> values)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        foreach (var (k, v) in values)
        {
            int i = lines.FindIndex(l => l.StartsWith(k + "=", StringComparison.Ordinal));
            if (i >= 0) lines[i] = $"{k}={v}"; else lines.Add($"{k}={v}");
        }
        File.WriteAllLines(path, lines);
    }
}
