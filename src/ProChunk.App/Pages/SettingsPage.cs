using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using fNbt;
using ProChunk.App.Inspector;
using ProChunk.Core.Nbt;

namespace ProChunk.App.Pages;

/// <summary>Dünya ayarları (level.dat): genel ayarlar + oyun kuralları + gelişmiş NBT.</summary>
public sealed class SettingsPage : UserControl
{
    private enum Kind { Text, Number, Bool, Choice, ReadOnly }
    private sealed record Field(string Key, string Label, Kind Kind, (string Value, string Label)[]? Options = null);

    private static readonly (string, string)[] GameModes = { ("0", "Hayatta Kalma"), ("1", "Yaratıcı"), ("2", "Macera"), ("3", "İzleyici") };
    private static readonly (string, string)[] Difficulties = { ("0", "Barışçıl"), ("1", "Kolay"), ("2", "Normal"), ("3", "Zor") };

    private static readonly Field[] BedrockFields =
    {
        new("LevelName", "Dünya adı", Kind.Text),
        new("GameType", "Varsayılan oyun modu", Kind.Choice, GameModes[..3]),
        new("Difficulty", "Zorluk", Kind.Choice, Difficulties),
        new("RandomSeed", "Seed", Kind.ReadOnly),
        new("commandsEnabled", "Hileler / komutlar açık", Kind.Bool),
        new("IsHardcore", "Hardcore", Kind.Bool),
        new("SpawnX", "Doğma noktası X", Kind.Number), new("SpawnY", "Doğma noktası Y", Kind.Number), new("SpawnZ", "Doğma noktası Z", Kind.Number),
        new("Time", "Dünya zamanı (tick)", Kind.Number),
        new("rainLevel", "Yağmur seviyesi (0–1)", Kind.Number),
        new("lightningLevel", "Fırtına seviyesi (0–1)", Kind.Number),
        new("spawnMobs", "Canavar doğsun", Kind.Bool),
    };

    private static readonly Field[] JavaFields =
    {
        new("LevelName", "Dünya adı", Kind.Text),
        new("GameType", "Varsayılan oyun modu", Kind.Choice, GameModes),
        new("Difficulty", "Zorluk", Kind.Choice, Difficulties),
        new("hardcore", "Hardcore", Kind.Bool),
        new("allowCommands", "Hileler / komutlar açık", Kind.Bool),
        new("SpawnX", "Doğma noktası X", Kind.Number), new("SpawnY", "Doğma noktası Y", Kind.Number), new("SpawnZ", "Doğma noktası Z", Kind.Number),
        new("DayTime", "Gün saati (tick)", Kind.Number),
        new("Time", "Toplam zaman (tick)", Kind.Number),
        new("raining", "Yağmur yağıyor", Kind.Bool),
        new("thundering", "Fırtına var", Kind.Bool),
        new("clearWeatherTime", "Açık hava süresi (tick)", Kind.Number),
    };

    /// <summary>Oyun kuralı Türkçe adları (anahtarlar küçük harfle).</summary>
    private static readonly Dictionary<string, string> RuleLabels = new()
    {
        ["keepinventory"] = "Ölünce envanter korunsun", ["dodaylightcycle"] = "Gün döngüsü", ["doweathercycle"] = "Hava döngüsü",
        ["domobspawning"] = "Yaratık doğması", ["mobgriefing"] = "Yaratıklar blok bozabilsin", ["pvp"] = "PvP",
        ["naturalregeneration"] = "Doğal can yenilenmesi", ["showcoordinates"] = "Koordinatları göster", ["dofiretick"] = "Ateş yayılsın",
        ["tntexplodes"] = "TNT patlasın", ["falldamage"] = "Düşme hasarı", ["firedamage"] = "Ateş hasarı", ["drowningdamage"] = "Boğulma hasarı",
        ["freezedamage"] = "Donma hasarı", ["doinsomnia"] = "Phantom doğsun", ["doimmediaterespawn"] = "Anında yeniden doğ",
        ["commandblockoutput"] = "Komut bloğu çıktısı", ["sendcommandfeedback"] = "Komut geri bildirimi", ["domobloot"] = "Yaratık ganimeti",
        ["dotiledrops"] = "Blok düşürme", ["doentitydrops"] = "Varlık düşürme", ["showdeathmessages"] = "Ölüm mesajları",
        ["randomtickspeed"] = "Rastgele tick hızı", ["spawnradius"] = "Doğma yarıçapı", ["playerssleepingpercentage"] = "Uyuyan oyuncu yüzdesi",
        ["respawnblocksexplode"] = "Yeniden doğma blokları patlasın", ["showtags"] = "Etiketleri göster", ["recipesunlock"] = "Tarif kilidi",
        ["dolimitedcrafting"] = "Sınırlı üretim", ["projectilescanbreakblocks"] = "Mermiler blok kırabilsin", ["showdaysplayed"] = "Oynanan günleri göster",
        ["functioncommandlimit"] = "Fonksiyon komut limiti", ["maxcommandchainlength"] = "Maks. komut zinciri", ["spectatorsgeneratechunks"] = "İzleyiciler chunk üretsin",
        ["doinsomnia "] = "Phantom doğsun", ["announceadvancements"] = "Başarımları duyur", ["disableraids"] = "Baskınları kapat",
        ["dopatrolspawning"] = "Devriye doğması", ["dotraderspawning"] = "Gezgin tüccar doğması", ["dowardenspawning"] = "Warden doğması",
        ["forgiveDeadPlayers".ToLowerInvariant()] = "Ölü oyuncuları affet", ["universalanger"] = "Evrensel öfke", ["locatorbar"] = "Konum çubuğu",
    };

    private static readonly HashSet<string> BedrockRuleKeys = RuleLabels.Keys.ToHashSet();

    private readonly IEditorHost _host;
    private readonly StackPanel _general = new();
    private readonly WrapPanel _rules = new();
    private readonly TextBlock _title = new() { Text = "Dünya Ayarları", Style = (Style)Application.Current.FindResource("PageTitle") };
    private NbtCompound? _data;
    private bool _loading;

    public SettingsPage(IEditorHost host)
    {
        _host = host;
        var advanced = new Button { Content = "{ } Tüm alanlar (gelişmiş NBT)", HorizontalAlignment = HorizontalAlignment.Left };
        advanced.Click += (_, _) => EditRaw();

        Border Card(string header, UIElement body) => new()
        {
            Style = (Style)Application.Current.FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 16),
            Child = new StackPanel { Children = { new TextBlock { Text = header, Style = (Style)Application.Current.FindResource("Header") }, body } },
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(24), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                _title,
                new TextBlock
                {
                    Text = "Değişiklikler hemen belleğe uygulanır; diske yazmak için Kaydet (Ctrl+S). Kaydederken eski level.dat, level.dat_old olarak saklanır.",
                    Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 4, 0, 18), TextWrapping = TextWrapping.Wrap,
                },
                Card("GENEL", _general),
                Card("OYUN KURALLARI", _rules),
                advanced,
            },
        };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void Reload()
    {
        if (_host.World is not { } w) return;
        _loading = true;
        try
        {
            _data = w.ReadLevelData();
            _title.Text = $"Dünya Ayarları — {w.Name}";
            BuildGeneral(w.Edition == "Bedrock" ? BedrockFields : JavaFields);
            BuildRules(w.Edition == "Bedrock");
        }
        finally { _loading = false; }
    }

    private void BuildGeneral(Field[] fields)
    {
        _general.Children.Clear();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        int row = 0;
        foreach (var f in fields)
        {
            var tag = _data!.Get(f.Key);
            if (tag is null && f.Key == "RandomSeed") tag = _data.Get<NbtCompound>("WorldGenSettings")?.Get("seed");
            if (tag is null) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = f.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 12, 5) };
            var editor = Editor(f, tag);
            Grid.SetRow(label, row); Grid.SetRow(editor, row); Grid.SetColumn(editor, 1);
            grid.Children.Add(label); grid.Children.Add(editor);
            row++;
        }
        _general.Children.Add(grid);
    }

    private FrameworkElement Editor(Field f, NbtTag tag)
    {
        string value = tag is NbtByte b ? ((sbyte)b.Value).ToString() : tag.StringValue;
        switch (f.Kind)
        {
            case Kind.Bool:
                var cb = new CheckBox { IsChecked = value is "1" or "true", Margin = new Thickness(0, 5, 0, 5) };
                cb.Click += (_, _) => Set(f.Key, cb.IsChecked == true ? "1" : "0");
                return cb;
            case Kind.Choice:
                var combo = new ComboBox { ItemsSource = f.Options!.Select(o => o.Label).ToList(), Margin = new Thickness(0, 3, 0, 3) };
                combo.SelectedIndex = Array.FindIndex(f.Options!, o => o.Value == value);
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) Set(f.Key, f.Options![combo.SelectedIndex].Value); };
                return combo;
            case Kind.ReadOnly:
                var ro = new TextBox { Text = value, IsReadOnly = true, Margin = new Thickness(0, 3, 0, 3), ToolTip = "Salt okunur" };
                return ro;
            default:
                var tb = new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 3) };
                tb.LostFocus += (_, _) => Set(f.Key, tb.Text);
                tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Set(f.Key, tb.Text); };
                return tb;
        }
    }

    private void BuildRules(bool bedrock)
    {
        _rules.Children.Clear();
        if (bedrock)
        {
            foreach (var tag in _data!.Where(t => t.Name is { } n && BedrockRuleKeys.Contains(n.ToLowerInvariant())).OrderBy(t => Label(t.Name!)))
                _rules.Children.Add(RuleEditor(tag.Name!, tag is NbtByte b ? ((sbyte)b.Value).ToString() : tag.StringValue, tag is NbtByte,
                    v => Set(tag.Name!, v)));
        }
        else if (_data!.Get<NbtCompound>("GameRules") is { } rules)
        {
            foreach (var tag in rules.OrderBy(t => Label(t.Name!)))
            {
                var v = tag.StringValue;
                bool isBool = v is "true" or "false";
                _rules.Children.Add(RuleEditor(tag.Name!, isBool ? (v == "true" ? "1" : "0") : v, isBool,
                    nv => { NbtIO.Set(rules, new NbtString(tag.Name!, isBool ? (nv == "1" ? "true" : "false") : nv)); Commit(); }));
            }
        }
        if (_rules.Children.Count == 0)
            _rules.Children.Add(new TextBlock { Text = "Oyun kuralı bulunamadı.", Foreground = (Brush)Application.Current.FindResource("Muted") });
    }

    private static string Label(string key) => RuleLabels.TryGetValue(key.ToLowerInvariant(), out var l) ? l : key;

    private FrameworkElement RuleEditor(string key, string value, bool isBool, Action<string> set)
    {
        var panel = new DockPanel { Width = 330, Margin = new Thickness(0, 4, 18, 4), ToolTip = key };
        if (isBool)
        {
            var cb = new CheckBox { Content = Label(key), IsChecked = value == "1" };
            cb.Click += (_, _) => set(cb.IsChecked == true ? "1" : "0");
            panel.Children.Add(cb);
        }
        else
        {
            var tb = new TextBox { Text = value, Width = 80 };
            tb.LostFocus += (_, _) => set(tb.Text);
            DockPanel.SetDock(tb, Dock.Right);
            panel.Children.Add(tb);
            panel.Children.Add(new TextBlock { Text = Label(key), VerticalAlignment = VerticalAlignment.Center });
        }
        return panel;
    }

    private void Set(string key, string value)
    {
        if (_loading || _data is null) return;
        if (!NbtIO.TrySetValue(_data, key, value))
        {
            _host.MarkChanged($"Geçersiz değer: {key} = {value}");
            return;
        }
        Commit();
    }

    private void Commit()
    {
        if (_loading || _data is null || _host.World is not { } w) return;
        w.WriteLevelData(_data);
        _host.MarkChanged("Dünya ayarları güncellendi — diske yazmak için Kaydet (Ctrl+S)");
    }

    private void EditRaw()
    {
        if (_data is null) return;
        var copy = (NbtCompound)_data.Clone();
        var win = new Nbt.NbtEditorWindow(copy, "level.dat") { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true) return;
        _data = copy;
        Commit();
        Reload();
    }
}
