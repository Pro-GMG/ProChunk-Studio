using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using fNbt;
using ProChunk.App.Inspector;
using ProChunk.App.Items;
using ProChunk.Core;
using ProChunk.Core.Nbt;

namespace ProChunk.App.Pages;

/// <summary>Oyuncular: envanter, zırh, sol el, Ender Chest, XP, oyun modu, konum.</summary>
public sealed class PlayersPage : UserControl
{
    private readonly IEditorHost _host;
    private readonly ListBox _list = new() { Width = 230 };
    private readonly InventoryEditor _inventory = new();
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.FindResource("PageTitle") };
    private readonly TextBlock _subtitle = new() { Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBox _xp = new() { Width = 70 };
    private readonly ComboBox _mode = new() { Width = 170 };
    private readonly TextBox _px = new() { Width = 90 }, _py = new() { Width = 70 }, _pz = new() { Width = 90 };
    private readonly Grid _content = new();
    private readonly TextBlock _empty = new()
    {
        Text = "Bu dünyada oyuncu verisi bulunamadı.", Foreground = (Brush)Application.Current.FindResource("Muted"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15,
    };

    private string? _playerId;
    private NbtCompound? _player;
    private PlayerLayout? _layout;
    private bool _loading;

    public PlayersPage(IEditorHost host)
    {
        _host = host;

        _list.ItemTemplate = new DataTemplate
        {
            VisualTree = ItemTemplateFactory(),
        };
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is PlayerInfo p) LoadPlayer(p.Id); };
        _inventory.Changed += Commit;

        foreach (var tb in new[] { _xp, _px, _py, _pz })
        {
            tb.LostFocus += (_, _) => ApplyFields();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyFields(); };
        }
        _mode.SelectionChanged += (_, _) => ApplyFields();

        var raw = new Button { Content = "{ } Ham NBT" };
        raw.Click += (_, _) => EditRaw();
        var clearInv = new Button { Content = "Envanteri temizle", Style = (Style)Application.Current.FindResource("DangerButton") };
        clearInv.Click += (_, _) => ClearInventory();

        TextBlock L(string t) => new() { Text = t, Foreground = (Brush)Application.Current.FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var fields = new WrapPanel
        {
            Margin = new Thickness(0, 14, 0, 14),
            Children =
            {
                L("XP seviyesi"), _xp, Spacer(), L("Oyun modu"), _mode, Spacer(),
                L("Konum X"), _px, L("Y"), _py, L("Z"), _pz, Spacer(), raw, clearInv,
            },
        };

        var header = new StackPanel { Children = { _title, _subtitle, fields } };
        DockPanel.SetDock(header, Dock.Top);
        var right = new DockPanel { Margin = new Thickness(20, 0, 0, 0), Children = { header, _inventory } };

        var leftHeader = new TextBlock { Text = "OYUNCULAR", Style = (Style)Application.Current.FindResource("Header") };
        DockPanel.SetDock(leftHeader, Dock.Top);
        var left = new Border
        {
            Style = (Style)Application.Current.FindResource("CardBorder"), Padding = new Thickness(10),
            Child = new DockPanel { Children = { leftHeader, _list } },
        };

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(right);
        _content.Children.Add(root);
        _content.Children.Add(_empty);
        Content = _content;
    }

    private static FrameworkElementFactory ItemTemplateFactory()
    {
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PlayerInfo.DisplayName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var det = new FrameworkElementFactory(typeof(TextBlock));
        det.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PlayerInfo.Detail)));
        det.SetValue(TextBlock.FontSizeProperty, 11.0);
        det.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x99)));
        det.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        sp.AppendChild(name);
        sp.AppendChild(det);
        return sp;
    }

    private static Border Spacer() => new() { Width = 18 };

    /// <summary>Dünya değişince veya sayfa açılınca çağrılır.</summary>
    public void Reload()
    {
        var w = _host.World;
        var players = w?.GetPlayers() ?? Array.Empty<PlayerInfo>();
        _list.ItemsSource = players;
        _empty.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ((DockPanel)_content.Children[0]).Visibility = players.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var keep = players.FirstOrDefault(p => p.Id == _playerId) ?? players.FirstOrDefault();
        _list.SelectedItem = keep;
        if (keep is not null) LoadPlayer(keep.Id);
    }

    private void LoadPlayer(string id)
    {
        if (_host.World is not { } w) return;
        _loading = true;
        try
        {
            _playerId = id;
            _player = w.ReadPlayer(id);
            if (_player is null) return;
            _layout = w.PlayerLayout(_player);
            var info = w.GetPlayers().First(p => p.Id == id);
            _title.Text = info.DisplayName;
            _subtitle.Text = info.Detail;

            _xp.Text = NbtIO.Int(_player.Get(_layout.XpLevelKey))?.ToString() ?? "0";
            _mode.ItemsSource = _layout.GameModes.Select(m => m.Label).ToList();
            var cur = NbtIO.Int(_player.Get(_layout.GameModeKey))?.ToString();
            _mode.SelectedIndex = Array.FindIndex(_layout.GameModes, m => m.Value == cur);

            var pos = _player.Get<NbtList>("Pos");
            string P(int i) => pos is { Count: > 2 } ? Convert.ToDouble(pos[i] is NbtFloat f ? f.Value : pos[i].DoubleValue).ToString("0.##", CultureInfo.InvariantCulture) : "";
            _px.Text = P(0); _py.Text = P(1); _pz.Text = P(2);

            _inventory.SetSections(new[]
            {
                new InventoryEditor.Section("Zırh", _layout.Armor, new[] { 0, 1, 2, 3 }, 4),
                new InventoryEditor.Section("Sol el", _layout.Offhand, new[] { 0 }, 1),
                new InventoryEditor.Section("Envanter", _layout.Inventory, Enumerable.Range(9, 27).ToArray(), 9),
                new InventoryEditor.Section("Hızlı erişim çubuğu", _layout.Inventory, Enumerable.Range(0, 9).ToArray(), 9),
                new InventoryEditor.Section("Ender Chest", _layout.EnderChest, Enumerable.Range(0, 27).ToArray(), 9),
            });
        }
        finally { _loading = false; }
    }

    private void Commit()
    {
        if (_loading || _host.World is not { } w || _player is null || _playerId is null) return;
        w.WritePlayer(_playerId, _player);
        _host.MarkChanged("Oyuncu güncellendi — diske yazmak için Kaydet (Ctrl+S)");
    }

    private void ApplyFields()
    {
        if (_loading || _player is null || _layout is null) return;
        if (int.TryParse(_xp.Text, out var xp))
        {
            if (!_player.Contains(_layout.XpLevelKey)) _player.Add(new NbtInt(_layout.XpLevelKey, 0));
            NbtIO.TrySetValue(_player, _layout.XpLevelKey, xp.ToString());
        }
        if (_mode.SelectedIndex >= 0)
        {
            if (!_player.Contains(_layout.GameModeKey)) _player.Add(new NbtInt(_layout.GameModeKey, 0));
            NbtIO.TrySetValue(_player, _layout.GameModeKey, _layout.GameModes[_mode.SelectedIndex].Value);
        }
        if (_player.Get<NbtList>("Pos") is { Count: > 2 } pos)
        {
            var texts = new[] { _px.Text, _py.Text, _pz.Text };
            for (int i = 0; i < 3; i++)
            {
                if (!double.TryParse(texts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
                NbtTag t = pos[i] is NbtFloat ? new NbtFloat((float)d) : new NbtDouble(d);
                pos.RemoveAt(i); pos.Insert(i, t);
            }
        }
        Commit();
    }

    private void ClearInventory()
    {
        if (_layout is null) return;
        if (MessageBox.Show(Window.GetWindow(this), "Envanter, zırh ve sol el boşaltılsın mı? (Ender Chest korunur)", "Envanteri temizle",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        for (int i = 0; i < 36; i++) _layout.Inventory.Set(i, null);
        for (int i = 0; i < 4; i++) _layout.Armor.Set(i, null);
        _layout.Offhand.Set(0, null);
        Commit();
        LoadPlayer(_playerId!);
    }

    private void EditRaw()
    {
        if (_player is null || _playerId is null) return;
        var copy = (NbtCompound)_player.Clone();
        var win = new Nbt.NbtEditorWindow(copy, _title.Text) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true) return;
        _host.World?.WritePlayer(_playerId, copy);
        _host.MarkChanged("Oyuncu NBT güncellendi");
        LoadPlayer(_playerId);
    }
}
