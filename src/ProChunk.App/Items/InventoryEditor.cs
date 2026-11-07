using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using fNbt;
using ProChunk.Core.Items;

namespace ProChunk.App.Items;

/// <summary>
/// Yuva ızgaraları + seçili eşya düzenleme paneli. Envanter, Ender Chest, sandık ve shulker için ortak.
/// Değişiklikler doğrudan verilen <see cref="SlotContainer"/>'lara yazılır; <see cref="Changed"/> tetiklenir.
/// </summary>
public sealed class InventoryEditor : UserControl
{
    public sealed record Section(string Header, SlotContainer Container, int[] Order, int Columns);

    private sealed class SlotView : Border
    {
        public required SlotContainer Container { get; init; }
        public required int Index { get; init; }
        public readonly Image Icon = new() { Width = 32, Height = 32, IsHitTestVisible = false };
        public readonly TextBlock Count = new()
        {
            FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Brushes.White, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 0, Opacity = 1, Color = Colors.Black },
        };
        public readonly TextBlock Label = new()
        {
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x7C)), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
        };
    }

    private static readonly Brush SlotBg = Frozen(Color.FromRgb(0x2B, 0x2B, 0x33));
    private static readonly Brush SlotBorder = Frozen(Color.FromRgb(0x3E, 0x3E, 0x4A));
    private static readonly Brush SlotHover = Frozen(Color.FromRgb(0x6C, 0xAE, 0xFF));
    private static readonly Brush SlotSelected = Frozen(Color.FromRgb(0xFF, 0xD2, 0x3B));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly StackPanel _sections = new();
    private readonly List<SlotView> _slots = new();
    private SlotView? _selected;
    private Point? _dragStart;
    private static NbtCompound? _clipboard;

    // Düzenleme paneli
    private readonly Image _bigIcon = new() { Width = 48, Height = 48, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _slotTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _id = new() { IsEditable = true, Margin = new Thickness(0, 4, 0, 10), IsTextSearchEnabled = false };
    private readonly TextBox _count = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _damage = new() { Margin = new Thickness(0, 4, 0, 14) };
    private readonly Button _apply = new() { Content = "Uygula", Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(0, 0, 6, 6) };
    private readonly Button _clear = new() { Content = "Boşalt", Margin = new Thickness(0, 0, 6, 6) };
    private readonly Button _nested = new() { Content = "📦 İçeriği aç", Margin = new Thickness(0, 0, 6, 6) };
    private readonly Button _nbt = new() { Content = "{ } Ham NBT", Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _editor = new();
    private readonly TextBlock _hint = new()
    {
        Text = "Bir yuva seç.\n\nSürükle-bırak: eşyaları taşı/değiştir\nSağ tık: kopyala, yapıştır, boşalt\nShift+tık: yuvayı boşalt\nÇift tık: Shulker içeriğini aç",
        Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 12,
    };

    public event Action? Changed;

    public InventoryEditor()
    {
        _id.ItemsSource = ItemCatalog.All;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(_bigIcon, Dock.Left);
        head.Children.Add(_bigIcon);
        head.Children.Add(_slotTitle);

        TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.FindResource("Header"), Margin = new Thickness(0) };
        var buttons = new WrapPanel();
        foreach (var b in new[] { _apply, _clear, _nested, _nbt }) buttons.Children.Add(b);

        _editor.Children.Add(head);
        _editor.Children.Add(L("EŞYA KİMLİĞİ"));
        _editor.Children.Add(_id);
        _editor.Children.Add(L("ADET"));
        _editor.Children.Add(_count);
        _editor.Children.Add(L("HASAR (DAYANIKLILIK KAYBI)"));
        _editor.Children.Add(_damage);
        _editor.Children.Add(buttons);
        _editor.Visibility = Visibility.Collapsed;

        _apply.Click += (_, _) => ApplyEditor();
        _clear.Click += (_, _) => { if (_selected is { } s) SetItem(s, null); };
        _nested.Click += (_, _) => { if (_selected is { } s) OpenNested(s); };
        _nbt.Click += (_, _) => { if (_selected is { } s) EditRawNbt(s); };
        _count.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyEditor(); };
        _damage.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyEditor(); };

        var side = new Border
        {
            Width = 280, Padding = new Thickness(16), CornerRadius = new CornerRadius(10), Margin = new Thickness(16, 0, 0, 0),
            Background = (Brush)Application.Current.FindResource("Card"), BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel { Children = { _editor, _hint } },
        };

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    public void SetSections(IEnumerable<Section> sections)
    {
        _sections.Children.Clear();
        _slots.Clear();
        Select(null);
        foreach (var sec in sections)
        {
            _sections.Children.Add(new TextBlock
            {
                Text = sec.Header.ToUpperInvariant(), Style = (Style)Application.Current.FindResource("Header"), Margin = new Thickness(0, 6, 0, 6),
            });
            var grid = new UniformGrid { Columns = sec.Columns, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var i in sec.Order) grid.Children.Add(CreateSlot(sec.Container, i));
            _sections.Children.Add(grid);
        }
    }

    private SlotView CreateSlot(SlotContainer c, int i)
    {
        var s = new SlotView
        {
            Container = c, Index = i, Width = 50, Height = 50, Margin = new Thickness(2), CornerRadius = new CornerRadius(5),
            Background = SlotBg, BorderBrush = SlotBorder, BorderThickness = new Thickness(2), AllowDrop = true, Cursor = Cursors.Hand,
        };
        s.Label.Text = c.SlotLabel?.Invoke(i) ?? "";
        s.Child = new Grid { Children = { s.Label, s.Icon, s.Count } };
        s.MouseEnter += (_, _) => { if (s != _selected) s.BorderBrush = SlotHover; };
        s.MouseLeave += (_, _) => { if (s != _selected) s.BorderBrush = SlotBorder; };
        s.MouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift) { SetItem(s, null); e.Handled = true; return; }
            if (e.ClickCount == 2) { OpenNested(s); e.Handled = true; return; }
            Select(s);
            _dragStart = e.GetPosition(this);
        };
        s.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } p0 || c.Get(i) is null) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - p0.X) + Math.Abs(p.Y - p0.Y) < 6) return;
            _dragStart = null;
            DragDrop.DoDragDrop(s, new DataObject(typeof(SlotView), s), DragDropEffects.Move);
        };
        s.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(SlotView)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        s.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(SlotView)) is not SlotView from || from == s) return;
            var a = from.Container.Get(from.Index) is { } fa ? (NbtCompound)fa.Clone() : null;
            var b = s.Container.Get(s.Index) is { } sb ? (NbtCompound)sb.Clone() : null;
            from.Container.Set(from.Index, b);
            s.Container.Set(s.Index, a);
            Refresh(from); Refresh(s);
            Select(s);
            Changed?.Invoke();
        };
        s.ContextMenu = BuildMenu(s);
        Refresh(s);
        _slots.Add(s);
        return s;
    }

    private ContextMenu BuildMenu(SlotView s)
    {
        var m = new ContextMenu();
        MenuItem I(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); return mi; }
        m.Items.Add(I("Kopyala", () => { if (s.Container.Get(s.Index) is { } it) _clipboard = (NbtCompound)it.Clone(); }));
        m.Items.Add(I("Yapıştır", () => { if (_clipboard is not null) SetItem(s, (NbtCompound)_clipboard.Clone()); }));
        m.Items.Add(I("64'e tamamla", () =>
        {
            if (s.Container.Get(s.Index) is not { } it) return;
            var copy = (NbtCompound)it.Clone();
            s.Container.Codec.SetCount(copy, 64);
            SetItem(s, copy);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(I("İçeriği aç (Shulker)", () => OpenNested(s)));
        m.Items.Add(I("Ham NBT düzenle", () => EditRawNbt(s)));
        m.Items.Add(new Separator());
        m.Items.Add(I("Boşalt", () => SetItem(s, null)));
        return m;
    }

    private void Refresh(SlotView s)
    {
        var it = s.Container.Get(s.Index);
        var codec = s.Container.Codec;
        if (it is null)
        {
            s.Icon.Source = null; s.Count.Text = ""; s.Label.Visibility = Visibility.Visible; s.ToolTip = s.Label.Text.Length > 0 ? s.Label.Text : "Boş";
            return;
        }
        var id = codec.GetId(it);
        int count = codec.GetCount(it);
        s.Icon.Source = ItemIcons.Get(id);
        s.Count.Text = count > 1 ? count.ToString() : "";
        s.Label.Visibility = Visibility.Collapsed;
        s.ToolTip = $"{id}\nAdet: {count}" + (codec.GetDamage(it) is > 0 and var d ? $"\nHasar: {d}" : "")
                    + (codec.Nested(Clone(it)) is not null ? "\nÇift tık: içeriği aç" : "");
    }

    private static NbtCompound Clone(NbtCompound c) => (NbtCompound)c.Clone();

    private void Select(SlotView? s)
    {
        if (_selected is not null) _selected.BorderBrush = SlotBorder;
        _selected = s;
        if (s is null) { _editor.Visibility = Visibility.Collapsed; _hint.Visibility = Visibility.Visible; return; }
        s.BorderBrush = SlotSelected;
        _editor.Visibility = Visibility.Visible; _hint.Visibility = Visibility.Collapsed;

        var it = s.Container.Get(s.Index);
        var codec = s.Container.Codec;
        string label = s.Container.SlotLabel?.Invoke(s.Index) ?? $"Yuva {s.Index}";
        _slotTitle.Text = $"{s.Container.Title} — {label}";
        _bigIcon.Source = it is null ? null : ItemIcons.Get(codec.GetId(it));
        _id.Text = it is null ? "" : codec.GetId(it);
        _count.Text = it is null ? "1" : codec.GetCount(it).ToString();
        _damage.Text = it is null ? "0" : codec.GetDamage(it).ToString();
        _nested.Visibility = it is not null && codec.Nested(Clone(it)) is not null ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled = _nbt.IsEnabled = it is not null;
    }

    private void ApplyEditor()
    {
        if (_selected is not { } s) return;
        var codec = s.Container.Codec;
        var id = _id.Text.Trim();
        if (id.Length == 0) { SetItem(s, null); return; }
        if (!id.Contains(':')) id = "minecraft:" + id;
        if (!int.TryParse(_count.Text, out var count) || count < 1) count = 1;
        int.TryParse(_damage.Text, out var damage);

        var it = s.Container.Get(s.Index) is { } existing ? Clone(existing) : codec.Create(id, count);
        codec.SetId(it, id);
        codec.SetCount(it, count);
        codec.SetDamage(it, damage);
        SetItem(s, it);
    }

    private void SetItem(SlotView s, NbtCompound? item)
    {
        s.Container.Set(s.Index, item);
        Refresh(s);
        if (s == _selected) Select(s);
        Changed?.Invoke();
    }

    private void OpenNested(SlotView s)
    {
        if (s.Container.Get(s.Index) is not { } it) return;
        var copy = Clone(it);
        var nested = s.Container.Codec.Nested(copy);
        if (nested is null) return;
        var win = new ContainerWindow($"{s.Container.Codec.GetId(it)} içeriği", nested) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) SetItem(s, copy);
    }

    private void EditRawNbt(SlotView s)
    {
        if (s.Container.Get(s.Index) is not { } it) return;
        var copy = Clone(it);
        var win = new Nbt.NbtEditorWindow(copy, "Eşya NBT") { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) SetItem(s, copy);
    }
}

/// <summary>Tek bir konteyneri (sandık, shulker...) düzenleyen pencere. Tamam'a basılana kadar değişiklikler verilen kopyada kalır.</summary>
public sealed class ContainerWindow : Window
{
    public ContainerWindow(string title, SlotContainer container)
    {
        Title = title;
        Width = 820; Height = 520; MinWidth = 640; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");

        var editor = new InventoryEditor();
        int cols = container.Size % 9 == 0 ? 9 : container.Size;
        editor.SetSections(new[] { new InventoryEditor.Section(container.Title, container, Enumerable.Range(0, container.Size).ToArray(), cols) });

        var ok = new Button { Content = "Tamam", Style = (Style)Application.Current.FindResource("Primary"), IsDefault = true, Padding = new Thickness(20, 7, 20, 7) };
        var cancel = new Button { Content = "İptal", IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0) };
        ok.Click += (_, _) => DialogResult = true;

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(editor);
        Content = root;
    }
}
