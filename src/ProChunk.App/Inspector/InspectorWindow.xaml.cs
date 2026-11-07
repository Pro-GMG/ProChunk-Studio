using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProChunk.Core;

namespace ProChunk.App.Inspector;

/// <summary>Ana pencerenin denetçiye sunduğu düzenleme hizmetleri.</summary>
public interface IEditorHost
{
    IWorld? World { get; }
    int Dimension { get; }
    /// <summary>Geri alma kaydı oluşturur ve chunk'ı dünyaya yazar.</summary>
    void ApplyChunkEdit(ChunkVolume volume, string description);
    event Action<IReadOnlyCollection<(int X, int Z)>>? ChunksChanged;
    void Undo();
    void Redo();
    void SaveWorld();
    /// <summary>Geri alma kaydıyla blok varlıklarını (sandık içerikleri vb.) yazar.</summary>
    void ApplyBlockEntities(int cx, int cz, List<fNbt.NbtCompound> entities, string description);
    /// <summary>Oyuncu / ayar gibi chunk dışı bir değişiklik yapıldı.</summary>
    void MarkChanged(string status);
}

public partial class InspectorWindow : Window
{
    private enum ViewMode { Top, SideZ, SideX }

    public sealed record StatItem(string State, int Count, Brush Swatch)
    {
        public string Display => State.StartsWith("minecraft:") ? State[10..] : State;
        public string CountText => Count.ToString("N0");
    }

    private readonly IEditorHost _host;
    private int _cx, _cz;
    private ChunkVolume? _volume;      // düzenlenen çalışma kopyası
    private bool _strokeDirty;
    private ViewMode _mode = ViewMode.Top;
    private List<StatItem> _stats = new();
    private bool _loading;

    public InspectorWindow(IEditorHost host, int cx, int cz)
    {
        InitializeComponent();
        _host = host;
        _host.ChunksChanged += OnChunksChanged;
        Closed += (_, _) => _host.ChunksChanged -= OnChunksChanged;

        Slice.CellHover += OnHover;
        Slice.CellPressed += OnPressed;
        Slice.StrokeEnded += OnStrokeEnded;

        BrushBox.ItemsSource = CommonBlocks;
        LoadChunk(cx, cz, keepSlice: false);
    }

    private static readonly string[] CommonBlocks =
    {
        "minecraft:air", "minecraft:stone", "minecraft:dirt", "minecraft:grass_block", "minecraft:sand", "minecraft:gravel",
        "minecraft:cobblestone", "minecraft:oak_planks", "minecraft:oak_log", "minecraft:glass", "minecraft:water",
        "minecraft:lava", "minecraft:bedrock", "minecraft:deepslate", "minecraft:diamond_block", "minecraft:gold_block",
        "minecraft:iron_block", "minecraft:emerald_block", "minecraft:obsidian", "minecraft:glowstone", "minecraft:tnt",
        "minecraft:white_wool", "minecraft:red_wool", "minecraft:stone_bricks", "minecraft:bricks", "minecraft:sandstone",
    };

    // ---------- Yükleme ----------
    private void LoadChunk(int cx, int cz, bool keepSlice)
    {
        if (_host.World is not { } world) { Close(); return; }
        var v = world.ReadChunk(_host.Dimension, cx, cz);
        if (v is null)
        {
            MessageBox.Show(this, $"Chunk {cx}, {cz} oluşturulmamış.", "Chunk Denetçisi", MessageBoxButton.OK, MessageBoxImage.Information);
            if (_volume is null) Close();
            return;
        }
        _cx = cx; _cz = cz; _volume = v;
        TitleText.Text = $"Chunk {cx}, {cz}";
        SubtitleText.Text = $"{world.Edition}  •  {world.Name}  •  Blok {cx * 16}…{cx * 16 + 15}, {cz * 16}…{cz * 16 + 15}  •  Y {v.MinY}…{v.MaxY}";
        Title = $"Chunk Denetçisi — {cx}, {cz}";
        RefreshStats();
        RefreshContainers();
        ConfigureSlider(keepSlice);
        Redraw();
    }

    private void OnChunksChanged(IReadOnlyCollection<(int X, int Z)> chunks)
    {
        if (chunks.Contains((_cx, _cz))) LoadChunk(_cx, _cz, keepSlice: true);
    }

    private void RefreshStats()
    {
        if (_volume is null) return;
        var counts = _volume.CountBlocks();
        _stats = counts.OrderByDescending(kv => kv.Value).Select(kv =>
        {
            var c = BlockColors.Get(kv.Key);
            var b = new SolidColorBrush(BlockColors.IsAir(kv.Key) ? Color.FromRgb(0x1A, 0x1A, 0x1F) : Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            b.Freeze();
            return new StatItem(kv.Key, kv.Value, b);
        }).ToList();
        int solid = counts.Where(kv => !BlockColors.IsAir(kv.Key)).Sum(kv => kv.Value);
        StatsSummary.Text = $"{counts.Count} farklı durum  •  {solid:N0} katı blok";
        ApplyStatsFilter();
        BrushBox.ItemsSource = CommonBlocks.Concat(_stats.Select(s => s.State)).Distinct().ToList();
    }

    private void ApplyStatsFilter()
    {
        var f = StatsFilter.Text.Trim();
        StatsList.ItemsSource = f.Length == 0 ? _stats : _stats.Where(s => s.State.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ---------- Görünüm ----------
    private void ConfigureSlider(bool keep)
    {
        if (_volume is null) return;
        _loading = true;
        double old = SliceSlider.Value;
        if (_mode == ViewMode.Top)
        {
            SliceLabel.Text = "Katman Y";
            SliceSlider.Minimum = _volume.MinY; SliceSlider.Maximum = _volume.MaxY;
            SliceSlider.Value = keep ? Math.Clamp(old, _volume.MinY, _volume.MaxY) : SurfaceY();
        }
        else
        {
            SliceLabel.Text = _mode == ViewMode.SideZ ? "Kesit Z (yerel)" : "Kesit X (yerel)";
            SliceSlider.Minimum = 0; SliceSlider.Maximum = 15;
            SliceSlider.Value = keep ? Math.Clamp(old, 0, 15) : 8;
        }
        _loading = false;
        UpdateSliceLabel();
    }

    private int SurfaceY()
    {
        if (_volume is null) return 64;
        for (int y = _volume.MaxY; y >= _volume.MinY; y--)
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                if (!BlockColors.IsAir(_volume.GetBlock(x, y, z))) return y;
        return _volume.MinY;
    }

    private int SliceIndex => (int)SliceSlider.Value;

    private void UpdateSliceLabel()
    {
        SliceValue.Text = _mode == ViewMode.Top ? $"{SliceIndex}"
            : _mode == ViewMode.SideZ ? $"{SliceIndex}  (dünya Z {_cz * 16 + SliceIndex})"
            : $"{SliceIndex}  (dünya X {_cx * 16 + SliceIndex})";
    }

    /// <summary>Ekran hücresi (u, v) → chunk içi (x, y, z).</summary>
    private (int X, int Y, int Z) ToBlock(int u, int v)
    {
        var vol = _volume!;
        return _mode switch
        {
            ViewMode.Top => (u, SliceIndex, v),
            ViewMode.SideZ => (u, vol.MaxY - v, SliceIndex),
            _ => (SliceIndex, vol.MaxY - v, u),
        };
    }

    private void Redraw()
    {
        if (_volume is null) return;
        var vol = _volume;
        if (_mode == ViewMode.Top)
        {
            Slice.RowLabel = null;
            Slice.SetContent(16, 16, 34, (u, v) => { var (x, y, z) = ToBlock(u, v); return vol.GetBlock(x, y, z); });
        }
        else
        {
            int rows = vol.MaxY - vol.MinY + 1;
            Slice.RowLabel = v => (vol.MaxY - v).ToString();
            Slice.SetContent(16, rows, 14, (u, v) => { var (x, y, z) = ToBlock(u, v); return vol.GetBlock(x, y, z); });
        }
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _volume is null) return;
        _mode = ViewTop.IsChecked == true ? ViewMode.Top : ViewZ.IsChecked == true ? ViewMode.SideZ : ViewMode.SideX;
        ConfigureSlider(keep: false);
        Redraw();
        if (_mode != ViewMode.Top)
        {
            // Yüzeyin görüneceği yere kaydır
            double rowY = (_volume.MaxY - SurfaceY()) * 14;
            SliceScroll.ScrollToVerticalOffset(Math.Max(0, rowY - 200));
        }
    }

    private void Slice_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized) return;
        UpdateSliceLabel();
        Redraw();
    }

    private void SliceUp_Click(object sender, RoutedEventArgs e) => SliceSlider.Value = Math.Min(SliceSlider.Maximum, SliceSlider.Value + 1);
    private void SliceDown_Click(object sender, RoutedEventArgs e) => SliceSlider.Value = Math.Max(SliceSlider.Minimum, SliceSlider.Value - 1);
    private void SliceSurface_Click(object sender, RoutedEventArgs e) { if (_mode == ViewMode.Top) SliceSlider.Value = SurfaceY(); }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key is Key.PageUp or Key.Add or Key.OemPlus) { SliceUp_Click(this, e); e.Handled = true; }
        else if (e.Key is Key.PageDown or Key.Subtract or Key.OemMinus) { SliceDown_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { _host.Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { _host.Redo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { _host.SaveWorld(); e.Handled = true; }
    }

    // ---------- Fare / araçlar ----------
    private void OnHover(int u, int v)
    {
        if (_volume is null) return;
        var (x, y, z) = ToBlock(u, v);
        var state = _volume.GetBlock(x, y, z);
        HoverText.Text = $"Yerel {x,2}, {y,4}, {z,2}   •   Dünya {_cx * 16 + x}, {y}, {_cz * 16 + z}   •   {state}"
                         + (Items.ContainerAccess.IsContainerBlock(state) ? "   •   📦 Bak aracıyla tıkla: içeriği aç" : "");
    }

    private string Brush => BlockState.Normalize(BrushBox.Text);

    private void OnPressed(int u, int v)
    {
        if (_volume is null) return;
        var (x, y, z) = ToBlock(u, v);
        if (ToolLook.IsChecked == true)
        {
            if (Items.ContainerAccess.IsContainerBlock(_volume.GetBlock(x, y, z)))
                Items.ContainerAccess.Open(this, _host, _cx * 16 + x, y, _cz * 16 + z);
            return;
        }
        if (ToolPick.IsChecked == true)
        {
            BrushBox.Text = _volume.GetBlock(x, y, z);
            ToolPencil.IsChecked = true;
            return;
        }
        string? state = ToolPencil.IsChecked == true ? Brush : ToolErase.IsChecked == true ? BlockState.Air : null;
        if (state is null) return;
        if (_volume.GetBlock(x, y, z) == state) return;
        if (_volume.SetBlock(x, y, z, state)) { _strokeDirty = true; Slice.InvalidateVisual(); }
    }

    private void OnStrokeEnded()
    {
        if (!_strokeDirty || _volume is null) return;
        _strokeDirty = false;
        Commit(ToolErase.IsChecked == true ? "Silgi" : "Kalem");
    }

    private void Commit(string description)
    {
        if (_volume is null) return;
        _host.ApplyChunkEdit(_volume, $"{description} — chunk {_cx}, {_cz}");
        // ApplyChunkEdit → ChunksChanged → LoadChunk ile taze kopya yüklenir
    }

    private IEnumerable<(int X, int Y, int Z)> SliceBlocks()
    {
        int rows = _mode == ViewMode.Top ? 16 : _volume!.MaxY - _volume.MinY + 1;
        for (int v = 0; v < rows; v++)
            for (int u = 0; u < 16; u++)
                yield return ToBlock(u, v);
    }

    private void ModifySlice(Func<string, string?> map, string description)
    {
        if (_volume is null) return;
        int changed = 0;
        foreach (var (x, y, z) in SliceBlocks())
        {
            var cur = _volume.GetBlock(x, y, z);
            var to = map(cur);
            if (to is not null && to != cur && _volume.SetBlock(x, y, z, to)) changed++;
        }
        if (changed > 0) Commit($"{description} ({changed} blok)");
        HoverText.Text = changed > 0 ? $"{changed:N0} blok değiştirildi" : "Değişecek blok yok";
    }

    private void FillSlice_Click(object sender, RoutedEventArgs e) { var b = Brush; ModifySlice(_ => b, "Kesit doldur"); }
    private void ClearSlice_Click(object sender, RoutedEventArgs e) => ModifySlice(_ => BlockState.Air, "Kesit temizle");

    private void ReplaceInSlice_Click(object sender, RoutedEventArgs e)
    {
        if (Slice.Highlight is not { } h)
        {
            HoverText.Text = "Önce sağdaki listeden değiştirilecek bloğu seç";
            return;
        }
        var b = Brush;
        ModifySlice(s => s == h ? b : null, "Kesitte değiştir");
    }

    // ---------- Konteynerler ----------
    public sealed record ContainerItem(int X, int Y, int Z, string Id, int Items)
    {
        public string Display => $"{Id.Replace("minecraft:", "")}  ({X}, {Y}, {Z})";
        public string CountText => $"{Items} eşya";
    }

    private void RefreshContainers()
    {
        if (_host.World is not { } w) return;
        var list = w.ReadBlockEntities(_host.Dimension, _cx, _cz)
            .Select(e => (e, p: Items.ContainerAccess.Position(e)))
            .Where(t => t.p is not null && t.e.Contains("Items"))
            .Select(t => new ContainerItem(t.p!.Value.X, t.p.Value.Y, t.p.Value.Z, Items.ContainerAccess.Id(t.e),
                (t.e.Get<fNbt.NbtList>("Items")?.OfType<fNbt.NbtCompound>().Count(i => !w.Items.IsEmpty(i))) ?? 0))
            .ToList();
        ContainersList.ItemsSource = list;
        ContainersHeader.Text = list.Count == 0 ? "KONTEYNER YOK" : $"KONTEYNERLER ({list.Count})";
    }

    private void ContainersList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ContainersList.SelectedItem is ContainerItem c) Items.ContainerAccess.Open(this, _host, c.X, c.Y, c.Z);
    }

    // ---------- İstatistik listesi ----------
    private void StatsFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyStatsFilter();

    private void StatsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Slice.Highlight = (StatsList.SelectedItem as StatItem)?.State;
        Slice.InvalidateVisual();
    }

    private void StatsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StatsList.SelectedItem is StatItem s) { BrushBox.Text = s.State; ToolPencil.IsChecked = true; }
    }

    // ---------- Komşu chunk'lar ----------
    private void West_Click(object sender, RoutedEventArgs e) => LoadChunk(_cx - 1, _cz, true);
    private void East_Click(object sender, RoutedEventArgs e) => LoadChunk(_cx + 1, _cz, true);
    private void North_Click(object sender, RoutedEventArgs e) => LoadChunk(_cx, _cz - 1, true);
    private void South_Click(object sender, RoutedEventArgs e) => LoadChunk(_cx, _cz + 1, true);
}
