using System.IO;
using System.IO.Compression;
using ProChunk.Bedrock;
using ProChunk.Core;
using ProChunk.Java;

namespace ProChunk.App;

/// <summary>
/// .mcworld / .zip / level.dat / klasör → IWorld. Arşivler ve oyunun kendi Bedrock dünyaları
/// çalışma alanına kopyalanır; orijinale dokunulmaz (geri vermek için Dışa Aktar).
/// </summary>
public static class WorldLoader
{
    public static readonly string Workspace =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProChunkStudio", "Workspace");

    public static readonly string[] SupportedExtensions = { ".mcworld", ".zip", ".dat" };

    public sealed record FoundWorld(string Name, string Path, string Edition, DateTime Modified)
    {
        public string Detail => $"{Edition}  •  {Modified:dd.MM.yyyy HH:mm}";
    }

    public static IWorld Load(string path, IProgress<string>? progress = null)
    {
        string folder;
        if (Directory.Exists(path)) folder = path;
        else if (Path.GetFileName(path).Equals("level.dat", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(path)!;
        else if (Path.GetExtension(path).ToLowerInvariant() is ".mcworld" or ".zip")
        {
            progress?.Report($"Arşiv çıkarılıyor: {Path.GetFileName(path)}…");
            folder = NewWorkspaceFolder(Path.GetFileNameWithoutExtension(path));
            ZipFile.ExtractToDirectory(path, folder, overwriteFiles: true);
        }
        else throw new NotSupportedException("Desteklenen türler: .mcworld, .zip, level.dat");

        var root = FindWorldRoot(folder) ?? throw new InvalidDataException("Burada bir Minecraft dünyası bulunamadı (level.dat yok).");

        if (BedrockWorld.IsBedrockWorld(root))
        {
            if (!IsInWorkspace(root))
            {
                progress?.Report("Bedrock dünyası güvenli çalışma kopyasına alınıyor…");
                var copy = NewWorkspaceFolder(Path.GetFileName(root));
                CopyDirectory(root, copy);
                root = copy;
            }
            progress?.Report("Bedrock veritabanı indeksleniyor…");
            return BedrockWorld.Open(root);
        }
        if (JavaWorld.IsJavaWorld(root)) return JavaWorld.Open(root);
        throw new InvalidDataException("level.dat bulundu ama region/ (Java) veya db/ (Bedrock) klasörü yok.");
    }

    public static bool IsInWorkspace(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Workspace), StringComparison.OrdinalIgnoreCase);

    private static string NewWorkspaceFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Workspace, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}");
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>level.dat içeren ilk klasörü arar (zip içinde iç içe klasör olabilir).</summary>
    private static string? FindWorldRoot(string folder, int depth = 0)
    {
        if (BedrockWorld.IsBedrockWorld(folder) || JavaWorld.IsJavaWorld(folder)) return folder;
        if (depth >= 3) return null;
        foreach (var sub in Directory.EnumerateDirectories(folder))
            if (FindWorldRoot(sub, depth + 1) is { } r) return r;
        return null;
    }

    /// <summary>Bilgisayardaki Java (.minecraft/saves) ve Bedrock (oyunun dünya klasörleri) dünyaları.</summary>
    public static List<FoundWorld> FindInstalledWorlds()
    {
        var list = new List<FoundWorld>();
        try { list.AddRange(JavaWorld.FindSaves().Select(s => new FoundWorld(s.Name, s.Path, "Java", Directory.GetLastWriteTime(s.Path)))); } catch { }

        var roots = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var users = Path.Combine(appData, "Minecraft Bedrock", "Users");
            if (Directory.Exists(users))
                roots.AddRange(Directory.EnumerateDirectories(users).Select(u => Path.Combine(u, "games", "com.mojang", "minecraftWorlds")));
        }
        catch { }
        roots.Add(Path.Combine(local, "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe", "LocalState", "games", "com.mojang", "minecraftWorlds"));

        foreach (var r in roots.Where(Directory.Exists))
            foreach (var d in Directory.EnumerateDirectories(r))
            {
                if (!BedrockWorld.IsBedrockWorld(d)) continue;
                var txt = Path.Combine(d, "levelname.txt");
                var name = File.Exists(txt) ? File.ReadAllText(txt).Trim() : Path.GetFileName(d);
                list.Add(new FoundWorld(name, d, "Bedrock", Directory.GetLastWriteTime(d)));
            }
        return list.OrderByDescending(w => w.Modified).ToList();
    }
}
