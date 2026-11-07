using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using fNbt;
using ProChunk.Core.Nbt;

namespace ProChunk.App.Nbt;

/// <summary>Genel amaçlı NBT ağaç düzenleyici. Verilen compound yerinde düzenlenir; Tamam ile onaylanır.</summary>
public sealed class NbtEditorWindow : Window
{
    private readonly NbtCompound _root;
    private readonly TreeView _tree = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Gainsboro };
    private readonly TextBox _value = new() { AcceptsReturn = false, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _filter = new() { Margin = new Thickness(0, 0, 0, 8), ToolTip = "Alan adına göre süz" };
    private readonly ComboBox _newType = new() { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _newName = new() { Width = 120, Margin = new Thickness(0, 0, 6, 0) };

    private static readonly (string Label, NbtTagType Type)[] Types =
    {
        ("Byte", NbtTagType.Byte), ("Short", NbtTagType.Short), ("Int", NbtTagType.Int), ("Long", NbtTagType.Long),
        ("Float", NbtTagType.Float), ("Double", NbtTagType.Double), ("String", NbtTagType.String),
        ("Compound", NbtTagType.Compound), ("List", NbtTagType.List),
    };

    public NbtEditorWindow(NbtCompound root, string title)
    {
        _root = root;
        Title = $"NBT Düzenleyici — {title}";
        Width = 900; Height = 640; MinWidth = 640; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");

        _newType.ItemsSource = Types.Select(t => t.Label).ToList();
        _newType.SelectedIndex = 2;
        _filter.TextChanged += (_, _) => Rebuild();
        _tree.SelectedItemChanged += (_, _) => OnSelect();

        var apply = new Button { Content = "Değeri uygula", Style = (Style)Application.Current.FindResource("Primary") };
        apply.Click += (_, _) => ApplyValue();
        _value.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyValue(); };
        var add = new Button { Content = "＋ Ekle" };
        add.Click += (_, _) => AddTag();
        var del = new Button { Content = "🗑 Sil", Style = (Style)Application.Current.FindResource("DangerButton") };
        del.Click += (_, _) => DeleteTag();

        TextBlock H(string t) => new() { Text = t, Style = (Style)Application.Current.FindResource("Header") };
        var side = new StackPanel
        {
            Width = 300, Margin = new Thickness(16, 0, 0, 0),
            Children =
            {
                H("SEÇİLİ ALAN"), _path,
                H("DEĞER"), _value, apply,
                new Border { Height = 24 },
                H("YENİ ALAN EKLE (seçili compound/list içine)"),
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _newType, _newName, add } },
                new Border { Height = 24 },
                del,
                new TextBlock
                {
                    Margin = new Thickness(0, 20, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 11,
                    Foreground = (Brush)Application.Current.FindResource("Muted"),
                    Text = "Dikkat: yanlış türde veya eksik alanlar oyunun dünyayı/oyuncuyu bozuk okumasına yol açabilir. Değişiklikler Tamam'a basınca uygulanır.",
                },
            },
        };

        var ok = new Button { Content = "Tamam", Style = (Style)Application.Current.FindResource("Primary"), IsDefault = false, Padding = new Thickness(20, 7, 20, 7) };
        var cancel = new Button { Content = "İptal", IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0) };
        ok.Click += (_, _) => DialogResult = true;
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };

        var left = new DockPanel();
        DockPanel.SetDock(_filter, Dock.Top);
        left.Children.Add(_filter);
        left.Children.Add(new Border
        {
            Background = (Brush)Application.Current.FindResource("Card"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6),
            Child = _tree,
        });

        var root2 = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(side, Dock.Right);
        root2.Children.Add(bottom);
        root2.Children.Add(side);
        root2.Children.Add(left);
        Content = root2;
        Rebuild();
    }

    private void Rebuild()
    {
        _tree.Items.Clear();
        var node = Node(_root, _filter.Text.Trim());
        if (node is not null) { node.IsExpanded = true; _tree.Items.Add(node); }
    }

    private TreeViewItem? Node(NbtTag tag, string filter)
    {
        var item = new TreeViewItem { Header = Describe(tag), Tag = tag, Foreground = Brushes.Gainsboro, FontFamily = new FontFamily("Consolas") };
        bool selfMatch = filter.Length == 0 || (tag.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool childMatch = false;
        if (tag is NbtCompound c)
            foreach (var t in c.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                if (Node(t, selfMatch ? "" : filter) is { } n) { item.Items.Add(n); childMatch = true; }
        if (tag is NbtList l)
            foreach (var t in l)
                if (Node(t, selfMatch ? "" : filter) is { } n) { item.Items.Add(n); childMatch = true; }
        if (filter.Length > 0 && childMatch) item.IsExpanded = true;
        return selfMatch || childMatch ? item : null;
    }

    private static string Describe(NbtTag t)
    {
        string name = t.Name ?? (t.Parent is NbtList pl ? $"[{pl.IndexOf(t)}]" : "(kök)");
        return t switch
        {
            NbtCompound c => $"📁 {name}  ({c.Count})",
            NbtList l => $"📚 {name}  [{l.Count}]",
            NbtByteArray a => $"{name}: byte[{a.Value.Length}]",
            NbtIntArray a => $"{name}: int[{a.Value.Length}]",
            NbtLongArray a => $"{name}: long[{a.Value.Length}]",
            NbtString s => $"{name} = \"{s.Value}\"",
            NbtFloat f => $"{name} = {f.Value.ToString(CultureInfo.InvariantCulture)}f",
            NbtDouble d => $"{name} = {d.Value.ToString(CultureInfo.InvariantCulture)}d",
            NbtByte b => $"{name} = {(sbyte)b.Value}b",
            NbtShort s => $"{name} = {s.Value}s",
            NbtLong l => $"{name} = {l.Value}L",
            _ => $"{name} = {t.StringValue}",
        };
    }

    private NbtTag? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag as NbtTag;

    private void OnSelect()
    {
        var t = Selected;
        _path.Text = t is null ? "" : $"{t.TagType}  {t.Path}";
        bool editable = t is not (null or NbtCompound or NbtList or NbtByteArray or NbtIntArray or NbtLongArray);
        _value.IsEnabled = editable;
        _value.Text = !editable ? "" : t switch
        {
            NbtByte b => ((sbyte)b.Value).ToString(),
            NbtFloat f => f.Value.ToString(CultureInfo.InvariantCulture),
            NbtDouble d => d.Value.ToString(CultureInfo.InvariantCulture),
            _ => t!.StringValue,
        };
    }

    private void ApplyValue()
    {
        if (Selected is not { } t) return;
        if (t.Parent is NbtCompound parent && t.Name is { } name)
        {
            if (!NbtIO.TrySetValue(parent, name, _value.Text)) { MessageBox.Show(this, "Değer bu türe uymuyor.", Title); return; }
        }
        else if (t.Parent is NbtList list)
        {
            var temp = new NbtCompound { (NbtTag)t.Clone() };
            temp[0].Name = "v";
            if (!NbtIO.TrySetValue(temp, "v", _value.Text)) { MessageBox.Show(this, "Değer bu türe uymuyor.", Title); return; }
            int i = list.IndexOf(t);
            var nt = (NbtTag)temp["v"]!.Clone(); nt.Name = null;
            list.RemoveAt(i); list.Insert(i, nt);
        }
        else return;
        Rebuild();
    }

    private void AddTag()
    {
        var target = Selected switch { NbtCompound c => (NbtTag)c, NbtList l => l, var s => s?.Parent ?? _root };
        var type = Types[Math.Max(0, _newType.SelectedIndex)].Type;
        string name = _newName.Text.Trim();
        NbtTag tag = type switch
        {
            NbtTagType.Byte => new NbtByte(), NbtTagType.Short => new NbtShort(), NbtTagType.Int => new NbtInt(),
            NbtTagType.Long => new NbtLong(), NbtTagType.Float => new NbtFloat(), NbtTagType.Double => new NbtDouble(),
            NbtTagType.String => new NbtString(""), NbtTagType.Compound => new NbtCompound(), _ => new NbtList(NbtTagType.Compound),
        };
        try
        {
            if (target is NbtCompound c)
            {
                if (name.Length == 0 || c.Contains(name)) { MessageBox.Show(this, "Benzersiz bir alan adı gir.", Title); return; }
                tag.Name = name; c.Add(tag);
            }
            else if (target is NbtList l) l.Add(tag);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title); return; }
        Rebuild();
    }

    private void DeleteTag()
    {
        if (Selected is not { } t || t == _root) return;
        if (t.Parent is NbtCompound c) c.Remove(t);
        else if (t.Parent is NbtList l) l.Remove(t);
        Rebuild();
    }
}
