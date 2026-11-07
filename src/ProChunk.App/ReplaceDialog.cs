using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProChunk.App;

/// <summary>Seçimde blok bul / değiştir penceresi.</summary>
public sealed class ReplaceDialog : Window
{
    private readonly ComboBox _from = new() { IsEditable = true, Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 12) };
    private readonly ComboBox _to = new() { IsEditable = true, Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 12), Text = "minecraft:air" };
    private readonly CheckBox _byName = new()
    {
        Content = "Özellikleri yok say (ör. tüm oak_stairs yönlerini eşleştir)",
        IsChecked = true, Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 0, 0, 16),
    };

    public string From => _from.Text.Trim();
    public string To => _to.Text.Trim();
    public bool MatchByName => _byName.IsChecked == true;

    public ReplaceDialog(IEnumerable<string> statesInSelection, int chunkCount)
    {
        Title = "Blok Bul / Değiştir";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28));

        var states = statesInSelection.ToList();
        _from.ItemsSource = states;
        _to.ItemsSource = states.Prepend("minecraft:air").Distinct().ToList();
        if (states.Count > 0) _from.Text = states[0];

        var ok = new Button { Content = "Değiştir", Padding = new Thickness(16, 6, 16, 6), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "İptal", Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (From.Length == 0 || To.Length == 0) { MessageBox.Show(this, "İki alanı da doldur.", Title); return; }
            DialogResult = true;
        };

        TextBlock Label(string t) => new() { Text = t, Foreground = Brushes.Gray, FontSize = 11, FontWeight = FontWeights.SemiBold };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = $"{chunkCount:N0} seçili chunk içinde", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 0, 0, 12) },
                Label("BUNU BUL"), _from,
                Label("BUNUNLA DEĞİŞTİR"), _to,
                _byName,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
    }
}
