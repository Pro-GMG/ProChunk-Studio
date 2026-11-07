using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using fNbt;
using ProChunk.Core.Nbt;
using ProChunk.Java;

namespace ProChunk.App.Engine;

/// <summary>Resmi Java sunucusuyla (server.jar) chunk üretimi: forceload + save-all.</summary>
public sealed class JavaServerEngine : ServerEngine
{
    private static readonly string[] DimIds = { "minecraft:overworld", "minecraft:the_nether", "minecraft:the_end" };

    public override async Task<string> GenerateAsync(string worldRoot, int dim, IReadOnlyCollection<(int X, int Z)> chunks,
        IProgress<string> progress, CancellationToken ct)
    {
        // 1) Sürüm ve server.jar
        var level = NbtIO.ReadJavaFile(Path.Combine(worldRoot, "level.dat")).Get<NbtCompound>("Data");
        var version = level?.Get<NbtCompound>("Version")?.Get<NbtString>("Name")?.Value
                      ?? throw new InvalidOperationException("level.dat içinde sürüm bilgisi yok (çok eski dünya).");
        progress.Report($"Java {version} sunucusu hazırlanıyor…");
        var (jar, javaMajor) = await EnsureServerAsync(version, progress, ct);
        var java = FindJava(javaMajor);

        // 2) Dünyanın kopyası: hedef chunk'lar silinir ki sunucu onları yeniden üretsin
        var run = Path.Combine(Root, "java", version, $"run_{DateTime.Now:yyyyMMdd_HHmmss}");
        var copy = Path.Combine(run, "world");
        progress.Report("Dünya kopyalanıyor…");
        CopyDirectory(worldRoot, copy, f => Path.GetFileName(f) == "session.lock");
        var w = JavaWorld.Open(copy);
        foreach (var (x, z) in chunks) if (((Core.IWorld)w).HasChunk(dim, x, z)) w.DeleteChunk(dim, x, z);
        w.Save();

        File.WriteAllText(Path.Combine(run, "eula.txt"), "eula=true\n");
        File.WriteAllText(Path.Combine(run, "server.properties"), string.Join("\n",
            "level-name=world", "online-mode=false", "white-list=true", "enforce-whitelist=true", $"server-port={FreePort()}",
            "spawn-protection=0", "max-tick-time=-1", "view-distance=3", "simulation-distance=3", "sync-chunk-writes=true",
            "enable-rcon=false", "enable-query=false", "motd=ProChunk Studio chunk uretimi", "max-players=1") + "\n");

        // 3) Sunucuyu başlat
        progress.Report("Sunucu başlatılıyor (ilk açılış biraz sürebilir)…");
        using var server = new ServerProcess(java, $"-Xms1G -Xmx3G -jar \"{jar}\" nogui", run, progress);
        await server.WaitForAsync(l => l.Contains("Done (") && l.Contains("For help"), 0, TimeSpan.FromMinutes(6), ct, "açılış");

        // 4) Toplu forceload → üret → kaydet → doğrula
        var remaining = chunks.ToHashSet();
        var rects = Rectangles(remaining, 16);       // forceload komutu başına en fazla 256 chunk
        int done = 0;
        foreach (var batch in rects.Chunk(4))
        {
            foreach (var (x0, z0, x1, z1) in batch)
                server.Send($"execute in {DimIds[dim]} run forceload add {x0 * 16} {z0 * 16} {x1 * 16 + 15} {z1 * 16 + 15}");

            var batchChunks = remaining.Where(c => batch.Any(r => c.X >= r.MinX && c.X <= r.MaxX && c.Z >= r.MinZ && c.Z <= r.MaxZ)).ToList();
            var deadline = DateTime.Now + TimeSpan.FromSeconds(60 + batchChunks.Count * 0.5);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                int mark = server.LineCount;
                server.Send("save-all flush");
                await server.WaitForAsync(l => l.Contains("Saved the game") || l.Contains("Oyun kaydedildi"), mark, TimeSpan.FromMinutes(3), ct, "kayıt");

                var check = JavaWorld.Open(copy);
                int ok = batchChunks.Count(c => check.ChunkStatus(dim, c.X, c.Z) is "minecraft:full" or "full");
                progress.Report($"Üretiliyor… {done + ok} / {chunks.Count} chunk");
                if (ok == batchChunks.Count || DateTime.Now > deadline) { done += ok; break; }
            }
            server.Send($"execute in {DimIds[dim]} run forceload remove all");
        }

        // 5) Kapat
        progress.Report("Sunucu kapatılıyor…");
        int stopMark = server.LineCount;
        server.Send("stop");
        await server.WaitExitAsync(TimeSpan.FromMinutes(2));
        return copy;
    }

    private static async Task<(string Jar, int JavaMajor)> EnsureServerAsync(string version, IProgress<string> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Root, "java", version);
        var jar = Path.Combine(dir, "server.jar");
        var info = Path.Combine(dir, "java-version.txt");
        if (File.Exists(jar) && File.Exists(info) && int.TryParse(File.ReadAllText(info), out var cached)) return (jar, cached);

        progress.Report("Mojang sürüm listesi alınıyor…");
        var manifest = await Http.GetFromJsonAsync<JsonElement>("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
        var entry = manifest.GetProperty("versions").EnumerateArray().FirstOrDefault(v => v.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Mojang sürüm listesinde '{version}' bulunamadı (modlu / snapshot olmayan bir dünya gerekir).");
        var meta = await Http.GetFromJsonAsync<JsonElement>(entry.GetProperty("url").GetString()!, ct);
        if (!meta.GetProperty("downloads").TryGetProperty("server", out var server))
            throw new InvalidOperationException($"{version} için resmi sunucu dosyası yok.");
        int major = meta.TryGetProperty("javaVersion", out var jv) ? jv.GetProperty("majorVersion").GetInt32() : 8;

        progress.Report($"server.jar ({version}) indiriliyor…");
        await DownloadAsync(server.GetProperty("url").GetString()!, jar, progress, ct);
        File.WriteAllText(info, major.ToString());
        return (jar, major);
    }

    /// <summary>Gerekli sürümü karşılayan bir java.exe bulur (PATH, JAVA_HOME, Minecraft başlatıcısının runtime'ı).</summary>
    private static string FindJava(int major)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home) candidates.Add(Path.Combine(home, "bin", "java.exe"));
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(p => Path.Combine(p.Trim(), "java.exe")));
        var rt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
        if (Directory.Exists(rt)) candidates.AddRange(Directory.EnumerateFiles(rt, "java.exe", SearchOption.AllDirectories));
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu", "BellSoft" })
        {
            var d = Path.Combine(pf, vendor);
            if (Directory.Exists(d)) candidates.AddRange(Directory.EnumerateDirectories(d).Select(j => Path.Combine(j, "bin", "java.exe")));
        }

        foreach (var c in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            if (JavaMajor(c) >= major) return c;
        throw new InvalidOperationException($"Bu sürüm için Java {major} veya üstü gerekli ama bulunamadı.\nhttps://adoptium.net adresinden kurabilirsin.");
    }

    private static int JavaMajor(string exe)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, "-version") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return 0;
            int a = int.Parse(m.Groups[1].Value);
            return a == 1 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
        }
        catch { return 0; }
    }
}
