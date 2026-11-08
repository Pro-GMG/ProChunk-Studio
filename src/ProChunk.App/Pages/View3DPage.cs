using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using ProChunk.App.Inspector;
using ProChunk.App.Items;
using ProChunk.Core;

namespace ProChunk.App.Pages;

/// <summary>Seçili bölgenin 3D blok görünümü: gezinme, blok kırma/koyma, konteyner açma.</summary>
public sealed class View3DPage : UserControl
{
    private readonly IEditorHost _host;
    private readonly HelixViewport3D _view = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x9C, 0xC4, 0xF0)),
        ShowCoordinateSystem = true, ShowViewCube = true, ZoomExtentsWhenLoaded = false,
        IsHeadLightEnabled = false, ShowFrameRate = false, RotateAroundMouseDownPoint = true,
    };
    private readonly ModelVisual3D _scene = new();
    private readonly ComboBox _radius = new() { Width = 90, ItemsSource = new[] { "1×1", "3×3", "5×5", "7×7" }, SelectedIndex = 1 };
    private readonly TextBox _minY = new() { Width = 60 }, _maxY = new() { Width = 60 };
    private readonly CheckBox _water = new() { Content = "Su", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _look = new() { Content = "🔍 Bak", IsChecked = true }, _break = new() { Content = "⛏ Kır" }, _place = new() { Content = "🧱 Koy" };
    private readonly ComboBox _brush = new() { IsEditable = true, Width = 210, Text = "minecraft:stone" };
    private readonly TextBlock _status = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(10, 6, 10, 6) };
    private readonly TextBlock _center = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };

    private (int X, int Z) _centerChunk;
    private int _originX, _originZ;
    private HashSet<(int X, int Z)> _shown = new();
    private int _buildId;
    private bool _dirty = true;

    public View3DPage(IEditorHost host)
    {
        _host = host;
        _host.ChunksChanged += c => { if (c.Any(_shown.Contains)) { _dirty = true; if (IsVisible) Rebuild(keepCamera: true); } };

        foreach (var r in new[] { _look, _break, _place }) { r.Style = (Style)Application.Current.FindResource("Toggle"); r.GroupName = "tool3d"; r.Margin = new Thickness(0, 0, 4, 0); }
        _brush.ItemsSource = ItemCatalog.All.Where(i => !i.Contains("sword") && !i.Contains("_helmet")).ToList();

        var rebuild = new Button { Content = "↻ Yeniden oluştur", Style = (Style)Application.Current.FindResource("Primary") };
        rebuild.Click += (_, _) => Rebuild(keepCamera: false);
        var fromMap = new Button { Content = "Haritadaki seçime git" };
        fromMap.Click += (_, _) => { if (_host is MainWindow mw && mw.FocusChunk is { } f) { _centerChunk = f; Rebuild(false); } };
        _radius.SelectionChanged += (_, _) => { if (IsLoaded) Rebuild(false); };
        _water.Click += (_, _) => Rebuild(true);

        TextBlock L(string t) => new() { Text = t, Foreground = (Brush)Application.Current.FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
        var bar = new WrapPanel
        {
            Margin = new Thickness(12, 10, 12, 10),
            Children =
            {
                _center, L("Alan"), _radius, L("Y aralığı"), _minY, new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center }, _maxY,
                new Border { Width = 10 }, _water, new Border { Width = 12 }, rebuild, fromMap,
                new Border { Width = 18 }, _look, _break, _place, new Border { Width = 6 }, _brush,
            },
        };

        _view.Children.Add(new SunLight());
        _view.Children.Add(new DefaultLights());
        _view.Children.Add(_scene);
        _view.MouseMove += OnMouseMove;
        _view.PreviewMouseLeftButtonDown += OnClick;

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x14)), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10),
            Child = _status, IsHitTestVisible = false,
        };
        var help = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x14)), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.Gainsboro, FontSize = 11, Margin = new Thickness(10, 6, 10, 6),
                Text = "Sağ tık sürükle: döndür  •  Shift+sağ tık / orta tık: kaydır  •  Tekerlek: yakınlaştır  •  Sol tık: araç",
            },
        };

        var grid = new Grid { Children = { _view, overlay, help } };
        var dock = new DockPanel();
        var top = new Border { Background = (Brush)Application.Current.FindResource("Panel"), BorderBrush = (Brush)Application.Current.FindResource("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(grid);
        Content = dock;

        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Rebuild(keepCamera: _shown.Count > 0); };
    }

    /// <summary>Haritadan çağrılır: bu chunk'ı merkeze al.</summary>
    public void Focus(int cx, int cz)
    {
        _centerChunk = (cx, cz);
        _minY.Text = ""; _maxY.Text = "";
        _dirty = true;
    }

    public void Reset() { _shown.Clear(); _scene.Content = null; _dirty = true; }

    private int Radius => _radius.SelectedIndex switch { 0 => 0, 2 => 2, 3 => 3, _ => 1 };

    private void Rebuild(bool keepCamera)
    {
        if (_host.World is not { } world) return;
        _dirty = false;
        int id = ++_buildId;
        int dim = _host.Dimension, r = Radius;
        var (ccx, ccz) = _centerChunk;
        _originX = ccx * 16; _originZ = ccz * 16;
        int ox = _originX, oz = _originZ;
        int? minY = int.TryParse(_minY.Text, out var a) ? a : null;
        int? maxY = int.TryParse(_maxY.Text, out var b) ? b : null;
        bool water = _water.IsChecked == true;
        _center.Text = $"Merkez chunk {ccx}, {ccz}";
        _status.Text = "3D model oluşturuluyor…";

        Task.Run(() =>
        {
            var vols = new Dictionary<(int, int), ChunkVolume>();
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (world.ReadChunk(dim, ccx + dx, ccz + dz) is { } v) vols[(ccx + dx, ccz + dz)] = v;
            if (vols.Count == 0) return (null, vols, 0, 0, 0);

            // Varsayılan Y aralığı: yüzeyin 40 blok altından en yüksek bloğa
            int top = vols.Values.Max(TopY), low = vols.Values.Min(SurfaceMin);
            int y0 = minY ?? Math.Max(vols.Values.Min(v => v.MinY), low - 40);
            int y1 = maxY ?? top;
            var (model, faces) = Mesher.Build(vols, y0, y1, ox, oz, water);
            return ((Model3D?)model, vols, faces, y0, y1);
        }).ContinueWith(t =>
        {
            if (id != _buildId) return;
            if (t.Exception is { } ex) { _status.Text = "Hata: " + ex.InnerException?.Message; return; }
            var (model, vols, faces, y0, y1) = t.Result;
            _shown = vols.Keys.ToHashSet();
            if (model is null) { _scene.Content = null; _status.Text = "Bu bölgede oluşturulmuş chunk yok."; return; }
            _scene.Content = model;
            if (_minY.Text.Length == 0) _minY.Text = y0.ToString();
            if (_maxY.Text.Length == 0) _maxY.Text = y1.ToString();
            _status.Text = $"{vols.Count} chunk  •  {faces:N0} yüz  •  Y {y0}…{y1}";
            if (!keepCamera)
            {
                double size = (2 * r + 1) * 16;
                _view.Camera.Position = new Point3D(size * 0.9, (y1 - 0) + size * 0.5, size * 1.1);
                _view.Camera.LookDirection = new Vector3D(8 - size * 0.9, -size * 0.5 - 8, 8 - size * 1.1);
                _view.Camera.UpDirection = new Vector3D(0, 1, 0);
                _view.ZoomExtents(300);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static int TopY(ChunkVolume v)
    {
        for (int y = v.MaxY; y >= v.MinY; y--)
            for (int z = 0; z < 16; z += 3)
                for (int x = 0; x < 16; x += 3)
                    if (!BlockColors.IsAir(v.GetBlock(x, y, z))) return Math.Min(v.MaxY, y + 1);
        return v.MinY + 64;
    }

    /// <summary>Kabaca en alçak yüzey yüksekliği (Y aralığını belirlemek için).</summary>
    private static int SurfaceMin(ChunkVolume v)
    {
        int min = int.MaxValue;
        for (int z = 0; z < 16; z += 5)
            for (int x = 0; x < 16; x += 5)
                for (int y = v.MaxY; y >= v.MinY; y--)
                    if (!BlockColors.IsAir(v.GetBlock(x, y, z))) { min = Math.Min(min, y); break; }
        return min == int.MaxValue ? v.MinY : min;
    }

    // ---------- Fare ----------
    private (int X, int Y, int Z, Vector3D Normal)? Pick(Point p)
    {
        RayMeshGeometry3DHitTestResult? hit = null;
        VisualTreeHelper.HitTest(_view.Viewport, null, r =>
        {
            if (r is RayMeshGeometry3DHitTestResult m) { hit = m; return HitTestResultBehavior.Stop; }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        if (hit is null) return null;
        var pos = hit.MeshHit.Positions;
        var n = Vector3D.CrossProduct(pos[hit.VertexIndex2] - pos[hit.VertexIndex1], pos[hit.VertexIndex3] - pos[hit.VertexIndex1]);
        n.Normalize();
        var inside = hit.PointHit - n * 0.5;
        return ((int)Math.Floor(inside.X) + _originX, (int)Math.Floor(inside.Y), (int)Math.Floor(inside.Z) + _originZ, n);
    }

    private DateTime _lastHover;
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed) return;
        if ((DateTime.Now - _lastHover).TotalMilliseconds < 60 || _host.World is not { } w) return;
        _lastHover = DateTime.Now;
        if (Pick(e.GetPosition(_view.Viewport)) is not { } b) return;
        var state = w.ReadChunk(_host.Dimension, b.X >> 4, b.Z >> 4)?.GetBlock(b.X & 15, b.Y, b.Z & 15) ?? "?";
        _status.Text = $"{b.X}, {b.Y}, {b.Z}   {state}" + (ContainerAccess.IsContainerBlock(state) ? "   📦 Bak ile tıkla: içeriği aç" : "");
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_host.World is not { } w) return;
        if (Pick(e.GetPosition(_view.Viewport)) is not { } b) return;
        e.Handled = true;
        int dim = _host.Dimension;

        if (_look.IsChecked == true)
        {
            var state = w.ReadChunk(dim, b.X >> 4, b.Z >> 4)?.GetBlock(b.X & 15, b.Y, b.Z & 15) ?? "";
            if (ContainerAccess.IsContainerBlock(state)) ContainerAccess.Open(Window.GetWindow(this)!, _host, b.X, b.Y, b.Z);
            return;
        }

        int x = b.X, y = b.Y, z = b.Z;
        string block = BlockState.Air;
        if (_place.IsChecked == true)
        {
            x += (int)Math.Round(b.Normal.X); y += (int)Math.Round(b.Normal.Y); z += (int)Math.Round(b.Normal.Z);
            block = BlockState.Normalize(_brush.Text);
        }
        if (w.ReadChunk(dim, x >> 4, z >> 4) is not { } v) return;
        if (!v.SetBlock(x & 15, y, z & 15, block)) return;
        _host.ApplyChunkEdit(v, $"3D: {(block == BlockState.Air ? "kır" : BlockState.BaseName(block))} ({x}, {y}, {z})");
    }
}

/// <summary>Chunk'lardan görünür yüzlerden oluşan, renge göre gruplanmış 3D model üretir.</summary>
internal static class Mesher
{
    private static readonly string[] Decor =
    {
        "short_grass", "tall_grass", "fern", "flower", "tulip", "poppy", "dandelion", "orchid", "allium", "bluet", "daisy",
        "cornflower", "lily_of_the_valley", "bush", "sapling", "torch", "vine", "seagrass", "kelp", "rail", "button",
        "pressure_plate", "lever", "sugar_cane", "wheat", "carrots", "potatoes", "beetroots", "dead_bush", "leaf_litter",
        "firefly", "wildflowers", "pink_petals", "redstone_wire", "tripwire", "cobweb", "sign", "banner", "lily_pad",
    };

    private enum Kind : byte { Air, Water, Glass, Solid, Decor }

    public static (Model3DGroup? Model, int Faces) Build(Dictionary<(int, int), ChunkVolume> vols, int y0, int y1, int ox, int oz, bool water)
    {
        var kindCache = new Dictionary<string, Kind>();
        Kind KindOf(string s)
        {
            if (kindCache.TryGetValue(s, out var k)) return k;
            var n = BlockState.BaseName(s);
            k = BlockColors.IsAir(n) ? Kind.Air
                : BlockColors.IsWater(n) ? Kind.Water
                : n.Contains("glass") || n.Contains("leaves") || n.Contains("ice") ? Kind.Glass
                : Decor.Any(n.Contains) && !n.EndsWith("_block") ? Kind.Decor
                : Kind.Solid;
            lock (kindCache) kindCache[s] = k;
            return k;
        }

        string Get(int wx, int y, int wz)
        {
            if (y < y0 || y > y1) return BlockState.Air;
            return vols.TryGetValue((wx >> 4, wz >> 4), out var v) ? v.GetBlock(wx & 15, y, wz & 15) : BlockState.Air;
        }

        var builders = new Dictionary<(uint Key, BitmapSource? Tex), Quads>();
        Quads B(uint key, BitmapSource? tex)
        {
            if (!builders.TryGetValue((key, tex), out var mb)) builders[(key, tex)] = mb = new Quads();
            return mb;
        }

        int faces = 0;
        (int dx, int dy, int dz, double shade)[] dirs =
        {
            (0, 1, 0, 1.0), (0, -1, 0, 0.55), (1, 0, 0, 0.82), (-1, 0, 0, 0.82), (0, 0, 1, 0.68), (0, 0, -1, 0.68),
        };

        foreach (var ((cx, cz), v) in vols)
        for (int y = Math.Max(y0, v.MinY); y <= Math.Min(y1, v.MaxY); y++)
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            var s = v.GetBlock(x, y, z);
            var k = KindOf(s);
            if (k is Kind.Air or Kind.Decor || (k == Kind.Water && !water)) continue;
            int wx = cx * 16 + x, wz = cz * 16 + z;
            uint color = k == Kind.Water ? 0x003F76E4u : BlockColors.Get(s) & 0x00FFFFFF;
            var tex = Items.BlockTextures.Get(k == Kind.Water ? "minecraft:water" : s);

            foreach (var (dx, dy, dz, shade) in dirs)
            {
                var nk = KindOf(Get(wx + dx, y + dy, wz + dz));
                bool visible = k == Kind.Water
                    ? nk is Kind.Air or Kind.Decor || (nk == Kind.Glass)
                    : nk is Kind.Air or Kind.Decor or Kind.Water || (nk == Kind.Glass && k != Kind.Glass);
                if (!visible) continue;

                uint key = (MapPainter.Shade(color | 0xFF000000, shade) & 0x00FFFFFF) | (k == Kind.Water ? 1u << 24 : k == Kind.Glass ? 2u << 24 : 0);
                var image = tex is null ? null : Items.BlockTextures.Shaded(dy > 0 ? tex.Top : dy < 0 ? tex.Bottom : tex.Side, shade);
                if (image is not null) key &= 0xFF000000;
                AddFace(B(key, image), wx - ox, y, wz - oz, dx, dy, dz);
                faces++;
            }
        }

        if (faces == 0) return (null, 0);
        var group = new Model3DGroup();
        // Saydam yüzler en sonda çizilir
        foreach (var ((key, tex), mb) in builders.OrderBy(kv => kv.Key.Key >> 24).Select(kv => (kv.Key, kv.Value)))
        {
            byte alpha = (key >> 24) switch { 1 => 150, 2 => 190, _ => 255 };
            Brush brush;
            if (tex is not null)
                brush = new ImageBrush(tex)
                {
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill,
                    Opacity = (key >> 24) == 1 ? 0.65 : 1,
                };
            else
                brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(key >> 16), (byte)(key >> 8), (byte)key));
            brush.Freeze();
            var mat = new DiffuseMaterial(brush);
            mat.Freeze();
            var model = new GeometryModel3D(mb.ToMesh(), mat) { BackMaterial = (key >> 24) != 0 ? mat : null };
            model.Freeze();
            group.Children.Add(model);
        }
        group.Freeze();
        return (group, faces);
    }

    /// <summary>Dörtgen listesinden MeshGeometry3D.</summary>
    private sealed class Quads
    {
        private readonly Point3DCollection _p = new();
        private readonly Int32Collection _i = new();
        private readonly PointCollection _uv = new();
        public void AddQuad(Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int n = _p.Count;
            _p.Add(a); _p.Add(b); _p.Add(c); _p.Add(d);
            _uv.Add(new Point(0, 1)); _uv.Add(new Point(1, 1)); _uv.Add(new Point(1, 0)); _uv.Add(new Point(0, 0));
            _i.Add(n); _i.Add(n + 1); _i.Add(n + 2); _i.Add(n); _i.Add(n + 2); _i.Add(n + 3);
        }
        public MeshGeometry3D ToMesh()
        {
            var m = new MeshGeometry3D { Positions = _p, TriangleIndices = _i, TextureCoordinates = _uv };
            m.Freeze();
            return m;
        }
    }

    private static void AddFace(Quads mb, int x, int y, int z, int dx, int dy, int dz)
    {
        Point3D P(double a, double b, double c) => new(x + a, y + b, z + c);
        if (dy == 1) mb.AddQuad(P(0, 1, 1), P(1, 1, 1), P(1, 1, 0), P(0, 1, 0));
        else if (dy == -1) mb.AddQuad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
        else if (dx == 1) mb.AddQuad(P(1, 0, 1), P(1, 0, 0), P(1, 1, 0), P(1, 1, 1));
        else if (dx == -1) mb.AddQuad(P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0));
        else if (dz == 1) mb.AddQuad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));
        else mb.AddQuad(P(1, 0, 0), P(0, 0, 0), P(0, 1, 0), P(1, 1, 0));
    }
}
