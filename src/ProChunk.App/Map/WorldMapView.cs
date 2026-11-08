using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProChunk.Core;

namespace ProChunk.App.Map;

/// <summary>Kuşbakışı dünya haritası: kaydırma, yakınlaştırma, chunk seçimi.</summary>
public sealed class WorldMapView : FrameworkElement
{
    private sealed class Tile { public BitmapSource? Bitmap; public BitmapSource[]? Levels; public bool Loading; }

    private IWorld? _world;
    private int _dimension;
    private IReadOnlySet<(int X, int Z)> _regions = new HashSet<(int, int)>();
    private readonly Dictionary<(int X, int Z), Tile> _tiles = new();
    private readonly SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount - 1));
    private int _generation;

    // Görünüm: ekran merkezindeki dünya koordinatı ve blok başına piksel
    private double _centerX, _centerZ, _scale = 1;

    private Point? _panStart; private double _panCX, _panCZ;
    private (int X, int Z)? _dragStart, _dragEnd, _hover;

    public HashSet<(int X, int Z)> Selection { get; } = new();

    public event Action<int, int>? HoverChanged;          // blok X, Z
    public event Action? SelectionChanged;
    public event Action<int, int>? ChunkClicked;           // chunk X, Z
    public event Action<int, int>? RegionsLoading;         // yüklenen, toplam

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)));
    private static readonly Brush Placeholder = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C)));
    private static readonly Brush SelFill = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x3B, 0x8E, 0xFF)));
    private static readonly Pen SelPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x6C, 0xAE, 0xFF)), 1));
    private static readonly Pen ChunkGrid = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1));
    private static readonly Pen RegionGrid = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 1.5));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public WorldMapView()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public double Scale => _scale;
    public (int X, int Z) CenterChunk => ((int)Math.Floor(_centerX / 16), (int)Math.Floor(_centerZ / 16));
    public int Dimension => _dimension;

    public void SetWorld(IWorld world, int dimension)
    {
        _generation++;
        _world = world;
        _dimension = dimension;
        _regions = world.GetRegions(dimension);
        _tiles.Clear();
        Selection.Clear();
        SelectionChanged?.Invoke();

        if (_regions.Count > 0)
        {
            // Başlangıçta 0,0'a en yakın region'a odaklan
            var r = _regions.OrderBy(k => k.X * k.X + k.Z * k.Z).First();
            _centerX = r.X * 512 + 256; _centerZ = r.Z * 512 + 256;
        }
        else { _centerX = _centerZ = 0; }
        _scale = 1;
        InvalidateVisual();
    }

    /// <summary>Düzenlenen chunk'ların region görüntülerini yeniden çizer.</summary>
    public void RefreshChunks(IEnumerable<(int X, int Z)> chunks)
    {
        if (_world is null) return;
        _regions = _world.GetRegions(_dimension);
        foreach (var r in chunks.Select(c => (c.X >> 5, c.Z >> 5)).Distinct())
            _tiles.Remove(r);
        InvalidateVisual();
    }

    /// <summary>Tüm haritayı yeniden çizer (geri alma vb. sonrası).</summary>
    public void RefreshAll()
    {
        if (_world is null) return;
        _generation++;
        _regions = _world.GetRegions(_dimension);
        _tiles.Clear();
        InvalidateVisual();
    }

    public event Action<int, int>? ChunkDoubleClicked;

    public void ClearSelection() { Selection.Clear(); SelectionChanged?.Invoke(); InvalidateVisual(); }

    public void ZoomBy(double factor) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);

    // ---------- Koordinat dönüşümleri ----------
    private Point ToScreen(double bx, double bz) => new((bx - _centerX) * _scale + ActualWidth / 2, (bz - _centerZ) * _scale + ActualHeight / 2);
    private (double X, double Z) ToWorld(Point p) => ((p.X - ActualWidth / 2) / _scale + _centerX, (p.Y - ActualHeight / 2) / _scale + _centerZ);
    private (int X, int Z) ChunkAt(Point p) { var (x, z) = ToWorld(p); return ((int)Math.Floor(x / 16), (int)Math.Floor(z / 16)); }

    private Rect ChunkRect(int cx, int cz)
    {
        var a = ToScreen(cx * 16, cz * 16);
        return new Rect(a.X, a.Y, 16 * _scale, 16 * _scale);
    }

    // ---------- Çizim ----------
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_world is null)
        {
            DrawCenteredText(dc, "Bir .mcworld / .zip dosyasını buraya sürükle veya \"Dünya Aç\"a tıkla");
            return;
        }
        if (_regions.Count == 0) { DrawCenteredText(dc, "Bu boyutta oluşturulmuş chunk yok"); return; }

        var (minX, minZ) = ToWorld(new Point(0, 0));
        var (maxX, maxZ) = ToWorld(new Point(ActualWidth, ActualHeight));
        int rx0 = (int)Math.Floor(minX / 512), rx1 = (int)Math.Floor(maxX / 512);
        int rz0 = (int)Math.Floor(minZ / 512), rz1 = (int)Math.Floor(maxZ / 512);

        for (int rz = rz0; rz <= rz1; rz++)
        for (int rx = rx0; rx <= rx1; rx++)
        {
            if (!_regions.Contains((rx, rz))) continue;
            var tl = ToScreen(rx * 512, rz * 512);
            var rect = new Rect(tl.X, tl.Y, 512 * _scale, 512 * _scale);
            var tile = GetTile(rx, rz);
            if (tile.Bitmap is not null) dc.DrawImage(LevelFor(tile), rect);
            else dc.DrawRectangle(Placeholder, null, rect);
        }

        // Chunk ızgarası (yeterince yakınken), aksi halde sadece region sınırları
        if (_scale * 16 >= 10)
        {
            for (int cx = (int)Math.Floor(minX / 16); cx <= Math.Ceiling(maxX / 16); cx++)
            {
                double x = ToScreen(cx * 16, 0).X;
                dc.DrawLine(cx % 32 == 0 ? RegionGrid : ChunkGrid, new Point(x, 0), new Point(x, ActualHeight));
            }
            for (int cz = (int)Math.Floor(minZ / 16); cz <= Math.Ceiling(maxZ / 16); cz++)
            {
                double y = ToScreen(0, cz * 16).Y;
                dc.DrawLine(cz % 32 == 0 ? RegionGrid : ChunkGrid, new Point(0, y), new Point(ActualWidth, y));
            }
        }
        else
        {
            for (int rx = rx0; rx <= rx1 + 1; rx++) { double x = ToScreen(rx * 512, 0).X; dc.DrawLine(RegionGrid, new Point(x, 0), new Point(x, ActualHeight)); }
            for (int rz = rz0; rz <= rz1 + 1; rz++) { double y = ToScreen(0, rz * 512).Y; dc.DrawLine(RegionGrid, new Point(0, y), new Point(ActualWidth, y)); }
        }

        // Seçim
        int vx0 = (int)Math.Floor(minX / 16) - 1, vx1 = (int)Math.Ceiling(maxX / 16) + 1;
        int vz0 = (int)Math.Floor(minZ / 16) - 1, vz1 = (int)Math.Ceiling(maxZ / 16) + 1;
        foreach (var (cx, cz) in Selection)
            if (cx >= vx0 && cx <= vx1 && cz >= vz0 && cz <= vz1)
                dc.DrawRectangle(SelFill, null, ChunkRect(cx, cz));

        // Sürükleme dikdörtgeni / fare altındaki chunk
        if (_dragStart is { } s && _dragEnd is { } e)
        {
            var a = ChunkRect(Math.Min(s.X, e.X), Math.Min(s.Z, e.Z));
            var b = ChunkRect(Math.Max(s.X, e.X), Math.Max(s.Z, e.Z));
            dc.DrawRectangle(SelFill, SelPen, Rect.Union(a, b));
        }
        else if (_hover is { } h && _scale * 16 >= 4)
        {
            dc.DrawRectangle(null, HoverPen, ChunkRect(h.X, h.Z));
        }
    }

    private void DrawCenteredText(DrawingContext dc, string text)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 16, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
    }

    // ---------- Region yükleme (arka planda, paralel) ----------
    private Tile GetTile(int rx, int rz)
    {
        if (_tiles.TryGetValue((rx, rz), out var t)) return t;
        t = new Tile { Loading = true };
        _tiles[(rx, rz)] = t;
        int gen = _generation;
        var world = _world!;
        int dim = _dimension;
        ReportLoading();

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            BitmapSource? bmp = null;
            BitmapSource[]? levels = null;
            try
            {
                if (gen != _generation) return;
                var pixels = world.RenderRegion(dim, rx, rz);
                levels = BuildLevels(pixels);
                bmp = levels[0];
            }
            catch { /* bozuk region: yer tutucu kalır */ }
            finally
            {
                _gate.Release();
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _generation) return;
                    t.Bitmap = bmp; t.Levels = levels; t.Loading = false;
                    ReportLoading();
                    InvalidateVisual();
                });
            }
        });
        return t;
    }

    /// <summary>Uzaklaştırınca kullanılan küçültülmüş kopyalar: 512, 256, 128, 64, 32 piksel.</summary>
    private static BitmapSource[] BuildLevels(uint[] pixels)
    {
        var levels = new List<BitmapSource>();
        int size = 512;
        while (true)
        {
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bmp.Freeze();
            levels.Add(bmp);
            if (size == 32) return levels.ToArray();
            pixels = HalveImage(pixels, size);
            size /= 2;
        }
    }

    /// <summary>Saydam (üretilmemiş) pikselleri saymadan 2x2 ortalama alır.</summary>
    private static uint[] HalveImage(uint[] src, int size)
    {
        int half = size / 2;
        var dst = new uint[half * half];
        for (int z = 0; z < half; z++)
        for (int x = 0; x < half; x++)
        {
            uint r = 0, g = 0, b = 0, n = 0;
            for (int dz = 0; dz < 2; dz++)
            for (int dx = 0; dx < 2; dx++)
            {
                uint c = src[(z * 2 + dz) * size + x * 2 + dx];
                if (c >> 24 == 0) continue;
                r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; n++;
            }
            if (n > 0) dst[z * half + x] = 0xFF000000 | (r / n) << 16 | (g / n) << 8 | (b / n);
        }
        return dst;
    }

    private BitmapSource LevelFor(Tile tile)
    {
        if (tile.Levels is not { } levels) return tile.Bitmap!;
        double wanted = 512 * _scale * VisualTreeHelper.GetDpi(this).DpiScaleX;
        int i = 0;
        while (i < levels.Length - 1 && levels[i + 1].PixelWidth >= wanted) i++;
        return levels[i];
    }

    private void ReportLoading() =>
        RegionsLoading?.Invoke(_tiles.Values.Count(x => !x.Loading), _tiles.Count);

    // ---------- Fare ----------
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    private void ZoomAt(Point p, double factor)
    {
        var before = ToWorld(p);
        _scale = Math.Clamp(_scale * factor, 1.0 / 16, 32);
        var after = ToWorld(p);
        _centerX += before.X - after.X;
        _centerZ += before.Z - after.Z;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = p; _panCX = _centerX; _panCZ = _centerZ;
            CaptureMouse();
        }
        else if (e.ChangedButton == MouseButton.Left && _world is not null && e.ClickCount == 2)
        {
            var c = ChunkAt(p);
            _dragStart = _dragEnd = null;
            ChunkDoubleClicked?.Invoke(c.X, c.Z);
        }
        else if (e.ChangedButton == MouseButton.Left && _world is not null)
        {
            _dragStart = _dragEnd = ChunkAt(p);
            CaptureMouse();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var (wx, wz) = ToWorld(p);
        HoverChanged?.Invoke((int)Math.Floor(wx), (int)Math.Floor(wz));

        if (_panStart is { } ps)
        {
            _centerX = _panCX - (p.X - ps.X) / _scale;
            _centerZ = _panCZ - (p.Y - ps.Y) / _scale;
        }
        else if (_dragStart is not null) _dragEnd = ChunkAt(p);
        _hover = ChunkAt(p);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panStart is not null && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = null; ReleaseMouseCapture(); return;
        }
        if (e.ChangedButton != MouseButton.Left || _dragStart is not { } s || _dragEnd is not { } end) return;

        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool remove = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (s == end && !add && !remove)
        {
            Selection.Clear();
            Selection.Add(s);
            ChunkClicked?.Invoke(s.X, s.Z);
        }
        else
        {
            if (!add && !remove) Selection.Clear();
            for (int x = Math.Min(s.X, end.X); x <= Math.Max(s.X, end.X); x++)
            for (int z = Math.Min(s.Z, end.Z); z <= Math.Max(s.Z, end.Z); z++)
                if (remove) Selection.Remove((x, z)); else Selection.Add((x, z));
        }

        _dragStart = _dragEnd = null;
        ReleaseMouseCapture();
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }
}
