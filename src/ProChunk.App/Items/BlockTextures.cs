using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProChunk.Core;

namespace ProChunk.App.Items;

/// <summary>
/// Blok dokuları: kullanıcının kurulu Minecraft Java .jar dosyasındaki blockstate/model/doku
/// bilgisinden çözülür (uygulamaya gömülmez). Gri saklanan çim, yaprak ve su dokuları renklendirilir.
/// </summary>
public static class BlockTextures
{
    public sealed record Faces(BitmapSource Top, BitmapSource Side, BitmapSource Bottom, uint Average);
    private sealed record Tex(BitmapSource Image, uint Average);

    private const int Size = 64;   // keskin pikseller için en yakın komşuyla büyütülür
    private const uint Grass = 0x7FB238, Foliage = 0x48B518;

    private static readonly ConcurrentDictionary<string, Faces?> States = new();
    private static readonly ConcurrentDictionary<string, Tex?> Textures = new();
    private static readonly ConcurrentDictionary<(BitmapSource, int), BitmapSource> ShadedCache = new();
    private static readonly ConcurrentDictionary<string, Brush?> Swatches = new();
    private static readonly ConcurrentDictionary<BitmapSource, bool> Opaque = new();

    public static Faces? Get(string state) => BlockColors.IsAir(state) ? null : States.GetOrAdd(state, Resolve);
    public static BitmapSource? Top(string state) => Get(state)?.Top;
    public static BitmapSource? Side(string state) => Get(state)?.Side;

    /// <summary>Haritada kullanılmak üzere dokunun ortalama rengi (0xAARRGGBB).</summary>
    public static uint? AverageColor(string state) => Get(state)?.Average;

    public static Brush? Swatch(string state) => Swatches.GetOrAdd(state, s =>
    {
        if (Top(s) is not { } t) return null;
        var b = new ImageBrush(t) { Stretch = Stretch.Fill };
        b.Freeze();
        return b;
    });

    /// <summary>Üst dokusunda saydam piksel yoksa true (çim, yaprak gibi bitkilerde false).</summary>
    public static bool IsOpaque(string state)
    {
        if (Top(state) is not { } t) return !BlockColors.IsAir(state);
        return Opaque.GetOrAdd(t, img =>
        {
            var px = new byte[Size * Size * 4];
            img.CopyPixels(px, Size * 4, 0);
            for (int i = 3; i < px.Length; i += 4)
                if (px[i] < 250) return false;
            return true;
        });
    }

    /// <summary>3B yüzler için karartılmış kopya.</summary>
    public static BitmapSource Shaded(BitmapSource src, double shade) =>
        ShadedCache.GetOrAdd((src, (int)(shade * 100)), _ =>
        {
            var px = new byte[Size * Size * 4];
            src.CopyPixels(px, Size * 4, 0);
            for (int i = 0; i < px.Length; i += 4)
                for (int c = 0; c < 3; c++) px[i + c] = (byte)(px[i + c] * shade);
            return Create(px);
        });

    private static Faces? Resolve(string state)
    {
        try
        {
            var (fullName, props) = BlockState.Parse(state);
            var name = fullName.StartsWith("minecraft:", StringComparison.Ordinal) ? fullName[10..] : fullName;
            var values = new Dictionary<string, string>();
            foreach (var (k, v) in props) values[k] = v;

            if (ReadJson($"assets/minecraft/blockstates/{name}.json") is not { } doc) return null;
            using (doc)
            {
                if (PickModel(doc.RootElement, values) is not { } model) return null;
                var (textures, elements) = LoadModel(model);
                using (elements)
                {
                    var el = elements?.RootElement;
                    var top = FaceTexture(el, "up", textures);
                    var side = FaceTexture(el, "north", textures);
                    var bottom = FaceTexture(el, "down", textures);
                    var fallback = Reference(textures, "top", "end", "all", "cross", "plant", "texture", "side", "particle");

                    var t = Load(top ?? fallback);
                    var s = Load(side ?? Reference(textures, "side", "all", "cross", "plant", "texture", "particle") ?? top);
                    var b = Load(bottom ?? Reference(textures, "bottom", "end", "all", "particle") ?? top);
                    var any = t ?? s ?? b;
                    if (any is null) return null;
                    return new Faces((t ?? any).Image, (s ?? any).Image, (b ?? any).Image, (t ?? any).Average);
                }
            }
        }
        catch { return null; }
    }

    private static string? PickModel(JsonElement root, Dictionary<string, string> props)
    {
        if (root.TryGetProperty("variants", out var variants))
        {
            JsonElement? chosen = null;
            foreach (var v in variants.EnumerateObject())
            {
                chosen ??= v.Value;
                var conditions = v.Name.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split('=')).Where(c => c.Length == 2);
                if (conditions.All(c => props.TryGetValue(c[0], out var value) && value == c[1])) { chosen = v.Value; break; }
            }
            return chosen is { } e ? ModelName(e) : null;
        }
        if (root.TryGetProperty("multipart", out var parts) && parts.GetArrayLength() > 0)
            return ModelName(parts[0].GetProperty("apply"));
        return null;
    }

    private static string? ModelName(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) e = e[0];
        return e.TryGetProperty("model", out var m) ? m.GetString() : null;
    }

    /// <summary>Üst modeller boyunca doku haritasını birleştirir; ilk "elements" dizisini döndürür.</summary>
    private static (Dictionary<string, string> Textures, JsonDocument? Elements) LoadModel(string model)
    {
        var textures = new Dictionary<string, string>();
        JsonDocument? elements = null;
        string? current = model;
        for (int depth = 0; depth < 12 && current is not null; depth++)
        {
            var id = Strip(current);
            if (ReadJson($"assets/minecraft/models/{id}.json") is not { } doc) break;
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("textures", out var map))
                    foreach (var t in map.EnumerateObject())
                        if (t.Value.ValueKind == JsonValueKind.String) textures.TryAdd(t.Name, t.Value.GetString()!);
                if (elements is null && root.TryGetProperty("elements", out var el))
                    elements = JsonDocument.Parse(el.GetRawText());
                current = root.TryGetProperty("parent", out var p) ? p.GetString() : null;
            }
        }
        return (textures, elements);
    }

    private static string? FaceTexture(JsonElement? elements, string face, Dictionary<string, string> textures)
    {
        if (elements is not { } list) return null;
        foreach (var e in list.EnumerateArray())
            if (e.TryGetProperty("faces", out var faces) && faces.TryGetProperty(face, out var f) && f.TryGetProperty("texture", out var t)
                && Follow(t.GetString(), textures) is { } id)
                return id;
        return null;
    }

    private static string? Reference(Dictionary<string, string> textures, params string[] keys)
    {
        foreach (var k in keys)
            if (Follow("#" + k, textures) is { } id) return id;
        return null;
    }

    private static string? Follow(string? value, Dictionary<string, string> textures)
    {
        for (int i = 0; i < 10 && value is not null; i++)
        {
            if (!value.StartsWith('#')) return Strip(value);
            textures.TryGetValue(value[1..], out value);
        }
        return null;
    }

    private static string Strip(string id) => id.StartsWith("minecraft:", StringComparison.Ordinal) ? id[10..] : id;

    private static JsonDocument? ReadJson(string path) => ItemIcons.ReadEntry(path) is { } bytes ? JsonDocument.Parse(bytes) : null;

    // ---------- Doku yükleme ----------
    private static Tex? Load(string? id) => id is null ? null : Textures.GetOrAdd(id, LoadCore);

    private static Tex? LoadCore(string id)
    {
        try
        {
            if (Frame(id) is not { } frame) return null;
            var (px, w) = frame;
            if (TintOf(id) is { } tint) Multiply(px, tint);

            // Çim yan yüzü: toprak üstüne renklendirilmiş kaplama
            if (id == "block/grass_block_side" && Frame("block/grass_block_side_overlay") is { } over && over.Width == w)
            {
                Multiply(over.Pixels, Grass);
                for (int i = 0; i < px.Length; i += 4)
                    if (over.Pixels[i + 3] > 0)
                        for (int c = 0; c < 4; c++) px[i + c] = over.Pixels[i + c];
            }

            var scaled = new byte[Size * Size * 4];
            long r = 0, g = 0, b = 0, n = 0;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int s = ((y * w / Size) * w + x * w / Size) * 4, d = (y * Size + x) * 4;
                Buffer.BlockCopy(px, s, scaled, d, 4);
                if (px[s + 3] > 16) { b += px[s]; g += px[s + 1]; r += px[s + 2]; n++; }
            }
            if (n == 0) return null;
            return new Tex(Create(scaled), 0xFF000000 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n));
        }
        catch { return null; }
    }

    /// <summary>Dokunun ilk karesi (animasyonlar dikey şerittir), BGRA.</summary>
    private static (byte[] Pixels, int Width)? Frame(string id)
    {
        if (ItemIcons.ReadEntry($"assets/minecraft/textures/{id}.png") is not { } bytes) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(bytes);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth;
        var px = new byte[w * w * 4];
        bgra.CopyPixels(new Int32Rect(0, 0, w, Math.Min(w, bgra.PixelHeight)), px, w * 4, 0);
        return (px, w);
    }

    private static uint? TintOf(string id)
    {
        var n = id.StartsWith("block/", StringComparison.Ordinal) ? id[6..] : id;
        if (n is "grass_block_top" or "short_grass" or "fern" or "sugar_cane" || n.StartsWith("tall_grass") || n.StartsWith("large_fern")) return Grass;
        if (n == "spruce_leaves") return 0x619961;
        if (n == "birch_leaves") return 0x80A755;
        if (n is "oak_leaves" or "jungle_leaves" or "acacia_leaves" or "dark_oak_leaves" or "mangrove_leaves" or "vine") return Foliage;
        if (n == "lily_pad") return 0x208030;
        if (n.StartsWith("water_")) return 0x3F76E4;
        return null;
    }

    private static void Multiply(byte[] px, uint rgb)
    {
        byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(px[i] * b / 255);
            px[i + 1] = (byte)(px[i + 1] * g / 255);
            px[i + 2] = (byte)(px[i + 2] * r / 255);
        }
    }

    private static BitmapSource Create(byte[] px)
    {
        var bmp = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, px, Size * 4);
        bmp.Freeze();
        return bmp;
    }
}
