using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProChunk.Core.Generation;

namespace ProChunk.App;

/// <summary>Seçili chunk'lar için şablonlu arazi üretimi ayarları.</summary>
public sealed class GenerateDialog : Window
{
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "🎮 Minecraft motoru — gerçek arazi (resmi sunucu)", "Düz dünya (katmanlı)", "Boş (void)", "Okyanus", "Tepeler", "Dağlar" }, SelectedIndex = 0 };
    private readonly TextBox _layers = new() { Text = "minecraft:bedrock*1, minecraft:dirt*2, minecraft:grass_block*1", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _base = new() { Text = "64" }, _sea = new() { Text = "62" }, _amp = new() { Text = "18" };
    private readonly TextBox _seed = new() { Text = Random.Shared.Next().ToString() };
    private readonly CheckBox _trees = new() { Content = "Ağaçlar", IsChecked = true };
    private readonly CheckBox _overwrite = new() { Content = "Var olan chunk'ların üzerine yaz (dikkat: içerikleri silinir)" };
    private readonly StackPanel _flatPanel, _terrainPanel;

    public TerrainOptions Options { get; private set; } = new();
    public bool UseEngine => _kind.SelectedIndex == 0;
    private readonly TextBlock _engineInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    public bool Overwrite => _overwrite.IsChecked == true;

    public GenerateDialog(int selected, int existing, string edition)
    {
        _engineInfo.Text = edition == "Bedrock"
            ? "Bedrock Dedicated Server indirilir ve dünyanın bir kopyasında arka planda çalıştırılır. Seçili chunk'lar dünyanın kendi seed'iyle, oyunun üreteceği şekilde (yapılar, mağaralar, biyomlar dahil) üretilip geri alınır. İlk kullanımda ~100 MB indirilir."
            : "Dünyanın sürümüne uygun resmi server.jar Mojang'dan indirilir ve dünyanın bir kopyasında arka planda çalıştırılır. Seçili chunk'lar dünyanın kendi seed'iyle, oyunun üreteceği şekilde (yapılar, mağaralar, biyomlar dahil) üretilip geri alınır. Java 21+ gerekir.";
        _engineInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xC8, 0xFF));
        Title = "Chunk Üret";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");

        TextBlock H(string t) => new() { Text = t, Style = (Style)Application.Current.FindResource("Header"), Margin = new Thickness(0, 12, 0, 4) };
        _flatPanel = new StackPanel
        {
            Children =
            {
                H("KATMANLAR (alttan üste, blok*kalınlık)"), _layers,
                new TextBlock { Text = "Örn: minecraft:bedrock*1, minecraft:stone*60, minecraft:dirt*3, minecraft:grass_block*1", FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
            },
        };
        UIElement Row(params (string, TextBox)[] f)
        {
            var g = new UniformGridLike();
            foreach (var (l, tb) in f) g.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 10, 0), Children = { H(l), tb } });
            return g;
        }
        _terrainPanel = new StackPanel { Children = { Row(("TABAN YÜKSEKLİĞİ", _base), ("DENİZ SEVİYESİ", _sea), ("TEPE YÜKSEKLİĞİ", _amp)), H("SEED (GÜRÜLTÜ)"), _seed, new Border { Height = 10 }, _trees } };
        _kind.SelectionChanged += (_, _) => UpdatePanels();

        var ok = new Button { Content = "Üret", Style = (Style)Application.Current.FindResource("Primary"), IsDefault = true, Padding = new Thickness(20, 7, 20, 7) };
        var cancel = new Button { Content = "İptal", IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0) };
        ok.Click += (_, _) => { if (Build()) DialogResult = true; };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = $"{selected:N0} seçili chunk  •  {existing:N0} tanesi zaten var", Foreground = (Brush)Application.Current.FindResource("Muted") },
                H("ARAZİ TÜRÜ"), _kind, _engineInfo, _flatPanel, _terrainPanel,
                new Border { Height = 14 }, _overwrite,
                new TextBlock
                {
                    Text = "Şablon türlerinde yeni chunk'lar biyom ve sürüm bilgisini dünyadaki var olan bir chunk'tan kopyalar; arazi şablona göre üretilir.",
                    FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16),
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        _engineInfo.Visibility = _kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        _flatPanel.Visibility = _kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _terrainPanel.Visibility = _kind.SelectedIndex >= 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Build()
    {
        if (UseEngine) return true;
        var o = new TerrainOptions { Kind = (TerrainKind)(_kind.SelectedIndex - 1), Trees = _trees.IsChecked == true };
        if (o.Kind == TerrainKind.Flat)
        {
            o.Layers = new();
            foreach (var part in _layers.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = part.Split('*');
                int n = p.Length > 1 && int.TryParse(p[1], out var c) ? c : 1;
                o.Layers.Add((p[0].Trim(), Math.Max(1, n)));
            }
            if (o.Layers.Count == 0) { MessageBox.Show(this, "En az bir katman gir.", Title); return false; }
        }
        if (int.TryParse(_base.Text, out var b)) o.BaseHeight = b;
        if (int.TryParse(_sea.Text, out var s)) o.SeaLevel = s;
        if (int.TryParse(_amp.Text, out var a)) o.Amplitude = a;
        if (long.TryParse(_seed.Text, out var seed)) o.Seed = seed;
        Options = o;
        return true;
    }

    private sealed class UniformGridLike : System.Windows.Controls.Primitives.UniformGrid { public UniformGridLike() { Rows = 1; } }
}
