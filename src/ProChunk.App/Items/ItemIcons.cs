using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProChunk.Core;

namespace ProChunk.App.Items;

/// <summary>
/// Eşya ikonları. Kullanıcının kurulu Minecraft Java .jar dosyasındaki dokular kullanılır
/// (uygulamaya gömülmez). Jar yoksa renkli harf rozeti üretilir.
/// </summary>
public static class ItemIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();
    private static readonly Lazy<ZipArchive?> Jar = new(OpenJar);
    private static readonly object JarLock = new();

    public static bool HasGameTextures => Jar.Value is not null;

    private static ZipArchive? OpenJar()
    {
        try
        {
            var versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions");
            if (!Directory.Exists(versions)) return null;
            var jar = Directory.EnumerateDirectories(versions)
                .Select(d => Path.Combine(d, Path.GetFileName(d) + ".jar"))
                .Where(File.Exists)
                .Where(f => !Path.GetFileName(f).Contains("forge", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            return jar is null ? null : ZipFile.OpenRead(jar);
        }
        catch { return null; }
    }

    /// <summary>Jar içinden bir dosyanın baytları; yoksa null.</summary>
    internal static byte[]? ReadEntry(string path)
    {
        if (Jar.Value is not { } jar) return null;
        lock (JarLock)
        {
            var e = jar.GetEntry(path);
            if (e is null) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    public static ImageSource Get(string id)
    {
        var name = BlockState.BaseName(id);
        name = name.StartsWith("minecraft:") ? name[10..] : name;
        return Cache.GetOrAdd(name, Load);
    }

    private static ImageSource Load(string name)
    {
        // Blok eşyaları: modelden çözülen, renklendirilmiş doku (merdiven, yaprak, çim...)
        if (ReadEntry($"assets/minecraft/textures/item/{name}.png") is null && BlockTextures.Top("minecraft:" + name) is { } blockTexture)
            return blockTexture;

        if (Jar.Value is { } jar)
        {
            foreach (var path in Candidates(name))
            {
                ZipArchiveEntry? e;
                lock (JarLock) e = jar.GetEntry(path);
                if (e is null) continue;
                try
                {
                    byte[] bytes;
                    lock (JarLock) { using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = new MemoryStream(bytes);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    // Animasyonlu dokular dikey şerittir: ilk kareyi al
                    if (img.PixelHeight > img.PixelWidth)
                    {
                        var crop = new CroppedBitmap(img, new Int32Rect(0, 0, img.PixelWidth, img.PixelWidth));
                        crop.Freeze();
                        return crop;
                    }
                    return img;
                }
                catch { }
            }
        }
        return Badge(name);
    }

    private static IEnumerable<string> Candidates(string n)
    {
        yield return $"assets/minecraft/textures/item/{n}.png";
        yield return $"assets/minecraft/textures/block/{n}.png";
        yield return $"assets/minecraft/textures/block/{n}_top.png";
        yield return $"assets/minecraft/textures/block/{n}_front.png";
        yield return $"assets/minecraft/textures/block/{n}_side.png";
        if (n.EndsWith("_block")) yield return $"assets/minecraft/textures/block/{n[..^6]}.png";
        if (n.EndsWith("_slab") || n.EndsWith("_stairs") || n.EndsWith("_wall"))
        {
            var b = n[..n.LastIndexOf('_')];
            yield return $"assets/minecraft/textures/block/{b}.png";
            yield return $"assets/minecraft/textures/block/{b}s.png";
            yield return $"assets/minecraft/textures/block/{b}_planks.png";
        }
        if (n.EndsWith("_log") || n.EndsWith("_wood")) yield return $"assets/minecraft/textures/block/{n[..^4]}_log.png";
        yield return $"assets/minecraft/textures/item/{n}_00.png";   // pusula, saat
    }

    /// <summary>Doku yoksa: blok rengiyle iki harfli rozet.</summary>
    private static ImageSource Badge(string name)
    {
        uint c = BlockColors.Get("minecraft:" + name);
        var color = Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        var letters = string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), new Rect(1, 1, 30, 30), 5, 5);
            var lum = color.R * 0.3 + color.G * 0.59 + color.B * 0.11;
            var ft = new FormattedText(letters, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 13,
                lum > 140 ? Brushes.Black : Brushes.White, 1.0);
            dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
