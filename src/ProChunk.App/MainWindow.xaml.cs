using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using fNbt;
using Microsoft.Win32;
using ProChunk.App.Inspector;
using ProChunk.App.Pages;
using ProChunk.Core;
using ProChunk.Core.Generation;

namespace ProChunk.App;

public partial class MainWindow : Window, IEditorHost
{
    private sealed record ClipItem(int DX, int DZ, ChunkVolume Volume);

    private IWorld? _world;
    private bool _busy;
    private bool _backedUp;
    private readonly EditHistory _history = new();
    private List<ClipItem>? _clipboard;
    private (int X, int Z)? _lastClicked;

    private readonly View3DPage _view3d;
    private readonly PlayersPage _players;
    private readonly SettingsPage _settings;

    public IWorld? World => _world;
    public int Dimension => Map.Dimension;
    public event Action<IReadOnlyCollection<(int X, int Z)>>? ChunksChanged;

    /// <summary>3D görünüm ve denetçi için odak chunk'ı: tek seçim, son tıklanan veya harita merkezi.</summary>
    public (int X, int Z)? FocusChunk =>
        Map.Selection.Count == 1 ? Map.Selection.First() : _lastClicked ?? (_world is null ? null : Map.CenterChunk);

    public MainWindow()
    {
        InitializeComponent();
        Core.BlockColors.Fallback = Items.BlockTextures.AverageColor;

        _view3d = new View3DPage(this);
        _players = new PlayersPage(this);
        _settings = new SettingsPage(this);
        View3DHost.Content = _view3d;
        PlayersHost.Content = _players;
        SettingsHost.Content = _settings;

        Map.HoverChanged += (x, z) =>
            CoordText.Text = $"Blok {x}, {z}   •   Chunk {x >> 4}, {z >> 4}   •   Region {x >> 9}, {z >> 9}   •   Zoom {Map.Scale:0.##}x";
        Map.SelectionChanged += UpdateUi;
        Map.RegionsLoading += (done, total) =>
        {
            if (!_busy && MapPage.Visibility == Visibility.Visible)
                StatusText.Text = done < total ? $"Harita çiziliyor… {done}/{total} region" : $"{total} region çizildi";
        };
        Map.ChunkClicked += (x, z) => { _lastClicked = (x, z); ShowChunkInfo(x, z); };
        Map.ChunkDoubleClicked += OpenInspector;

        Loaded += async (_, _) =>
        {
            LoadInstalled();
            UpdateUi();
            // "Birlikte aç" / komut satırı: ProChunk.App.exe "dünya.mcworld" [harita|3d|oyuncular|ayarlar]
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && (File.Exists(args[1]) || Directory.Exists(args[1])))
            {
                await LoadWorldAsync(args[1]);
                if (args.Length > 2)
                    (args[2] switch { "3d" => Nav3D, "oyuncular" => NavPlayers, "ayarlar" => NavSettings, _ => NavMap }).IsChecked = true;
            }
        };
        Closing += OnClosing;
    }

    // ---------- Başlık çubuğu ----------
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Ekranı kaplarken pencere kenarları ekran dışına taşar: iç boşlukla telafi et
        RootPanel.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Önceki boyut" : "Ekranı kapla";
    }

    // ---------- Sayfalar ----------
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || HomePage is null) return;
        HomePage.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MapPage.Visibility = NavMap.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        View3DHost.Visibility = Nav3D.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PlayersHost.Visibility = NavPlayers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavPlayers.IsChecked == true) _players.Reload();
        if (NavSettings.IsChecked == true) _settings.Reload();
    }

    private void LoadInstalled()
    {
        try
        {
            var worlds = WorldLoader.FindInstalledWorlds();
            InstalledList.ItemsSource = worlds;
            if (worlds.Count == 0) InstalledHint.Text = "Bu bilgisayarda Minecraft dünyası bulunamadı. Bir .mcworld / .zip dosyası açabilirsin.";
        }
        catch { /* listesiz devam */ }
    }

    private void RefreshInstalled_Click(object sender, RoutedEventArgs e) => LoadInstalled();

    private async void InstalledList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (InstalledList.SelectedItem is WorldLoader.FoundWorld w) await LoadWorldAsync(w.Path);
    }

    // ---------- Durum ----------
    private void UpdateUi()
    {
        int n = Map.Selection.Count;
        bool has = _world is not null && !_busy;
        SelectionText.Text = n == 0 ? "Seçili chunk yok" : $"{n:N0} chunk seçili";
        InspectBtn.IsEnabled = Show3DBtn.IsEnabled = has && (n == 1 || _lastClicked is not null);
        DeleteBtn.IsEnabled = CopyBtn.IsEnabled = ReplaceBtn.IsEnabled = GenerateBtn.IsEnabled = has && n > 0;
        PasteBtn.IsEnabled = has && n > 0 && _clipboard is not null;
        UndoBtn.IsEnabled = has && _history.CanUndo;
        RedoBtn.IsEnabled = has && _history.CanRedo;
        UndoBtn.ToolTip = _history.UndoDescription is { } u ? $"Geri al: {u} (Ctrl+Z)" : "Geri al (Ctrl+Z)";
        RedoBtn.ToolTip = _history.RedoDescription is { } r ? $"Yinele: {r} (Ctrl+Y)" : "Yinele (Ctrl+Y)";
        SaveBtn.IsEnabled = has && _world!.IsDirty;
        ExportBtn.IsEnabled = has;
        NavMap.IsEnabled = Nav3D.IsEnabled = NavPlayers.IsEnabled = NavSettings.IsEnabled = _world is not null;
        ClipboardText.Text = _clipboard is null ? "" : $"Panoda {_clipboard.Count} chunk var";
        bool dirty = _world?.IsDirty == true;
        DirtyMark.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        if (_world is not null) Title = $"ProChunk Studio — {_world.Name}{(dirty ? " ●" : "")}";
    }

    private void SetStatus(string s) => StatusText.Text = s;

    private void NotifyChanged(IReadOnlyCollection<(int X, int Z)> chunks)
    {
        Map.RefreshChunks(chunks);
        ChunksChanged?.Invoke(chunks);
        if (_lastClicked is { } c && chunks.Contains(c)) ShowChunkInfo(c.X, c.Z);
        UpdateUi();
    }

    public void MarkChanged(string status) { SetStatus(status); UpdateUi(); }

    private async Task RunBusy(string message, Func<Task> action)
    {
        _busy = true;
        LoadingText.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
        UpdateUi();
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _busy = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            UpdateUi();
        }
    }

    // ---------- Dünya yükleme ----------
    private async Task LoadWorldAsync(string path)
    {
        if (_busy || !ConfirmDiscard()) return;
        var progress = new Progress<string>(s => { LoadingText.Text = s; SetStatus(s); });
        IWorld? loaded = null;
        await RunBusy("Yükleniyor…", async () =>
        {
            loaded = await Task.Run(() => WorldLoader.Load(path, progress));
        });
        if (loaded is null) { SetStatus("Dünya açılamadı"); return; }

        foreach (var w in OwnedWindows.OfType<Window>().ToList()) w.Close();
        _world = loaded;
        _history.Clear();
        _backedUp = false;
        _lastClicked = null;
        _clipboard = null;
        WorldName.Text = loaded.Name;
        WorldTitle.Text = loaded.RootPath;
        EditionBadge.Text = loaded.Edition.ToUpperInvariant();
        EditionBorder.Background = new SolidColorBrush(loaded.Edition == "Bedrock" ? Color.FromRgb(0x2E, 0x7D, 0x4F) : Color.FromRgb(0x2F, 0x6F, 0xD6));
        DimensionBox.IsEnabled = true;
        DimensionBox.SelectedIndex = 0;
        Map.SetWorld(loaded, 0);
        _view3d.Reset();
        _view3d.Focus(Map.CenterChunk.X, Map.CenterChunk.Z);
        ChunkInfo.Text = "Bilgi için haritada bir chunk'a tıkla\nÇift tık: Chunk Denetçisi";
        UpdateUi();
        NavMap.IsChecked = true;
        SetStatus($"{loaded.Edition} dünyası açıldı: {loaded.Name}");
    }

    private async void OpenWorld_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Minecraft dünyası aç",
            Filter = "Minecraft dünyası (*.mcworld;*.zip;level.dat)|*.mcworld;*.zip;level.dat|Bedrock dünyası (*.mcworld)|*.mcworld|Zip arşivi (*.zip)|*.zip|Tüm dosyalar|*.*",
        };
        if (dlg.ShowDialog(this) == true) await LoadWorldAsync(dlg.FileName);
    }

    // ---------- Sürükle-bırak ----------
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f
                  && (Directory.Exists(f[0]) || WorldLoader.SupportedExtensions.Contains(Path.GetExtension(f[0]).ToLowerInvariant()));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await LoadWorldAsync(files[0]);
    }

    // ---------- Harita araç çubuğu ----------
    private void Dimension_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_world is null || !IsLoaded) return;
        foreach (var w in OwnedWindows.OfType<InspectorWindow>().ToList()) w.Close();
        Map.SetWorld(_world, DimensionBox.SelectedIndex);
        _lastClicked = null;
        _view3d.Reset();
        _view3d.Focus(Map.CenterChunk.X, Map.CenterChunk.Z);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(1.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(1 / 1.5);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Map.ClearSelection();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_busy || Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        bool onMap = MapPage.Visibility == Visibility.Visible;
        Action? a = (ctrl, e.Key) switch
        {
            (true, Key.Z) => Undo,
            (true, Key.Y) => Redo,
            (true, Key.S) => SaveWorld,
            (true, Key.E) => () => Export_Click(this, e),
            (true, Key.O) => () => OpenWorld_Click(this, e),
            (true, Key.C) when onMap => () => Copy_Click(this, e),
            (true, Key.V) when onMap => () => Paste_Click(this, e),
            (true, Key.H) when onMap => () => Replace_Click(this, e),
            (false, Key.Delete) when onMap => () => Delete_Click(this, e),
            (false, Key.Escape) when onMap => Map.ClearSelection,
            _ => null,
        };
        if (a is not null) { a(); e.Handled = true; }
    }

    // ---------- IEditorHost ----------
    public void ApplyChunkEdit(ChunkVolume volume, string description)
    {
        if (_world is null) return;
        _history.Record(_world, Dimension, new[] { (volume.X, volume.Z) }, description);
        _world.WriteChunk(Dimension, volume);
        SetStatus(description);
        NotifyChanged(new[] { (volume.X, volume.Z) });
    }

    public void ApplyBlockEntities(int cx, int cz, List<NbtCompound> entities, string description)
    {
        if (_world is null) return;
        _history.Record(_world, Dimension, new[] { (cx, cz) }, description);
        _world.WriteBlockEntities(Dimension, cx, cz, entities);
        SetStatus(description + " — kaydetmek için Ctrl+S");
        NotifyChanged(new[] { (cx, cz) });
    }

    public void Undo()
    {
        if (_world is null || _busy) return;
        if (_history.Undo(_world) is { } r) { SetStatus($"Geri alındı: {r.Description}"); AfterHistory(r.Dimension, r.Chunks); }
    }

    public void Redo()
    {
        if (_world is null || _busy) return;
        if (_history.Redo(_world) is { } r) { SetStatus($"Yinelendi: {r.Description}"); AfterHistory(r.Dimension, r.Chunks); }
    }

    private void AfterHistory(int dim, List<(int X, int Z)> chunks)
    {
        if (dim != Dimension) { DimensionBox.SelectedIndex = dim; return; }
        NotifyChanged(chunks);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    // ---------- Chunk işlemleri ----------
    private void Inspect_Click(object sender, RoutedEventArgs e)
    {
        var c = Map.Selection.Count == 1 ? Map.Selection.First() : _lastClicked;
        if (c is { } p) OpenInspector(p.X, p.Z);
    }

    private void OpenInspector(int cx, int cz)
    {
        if (_world is null) return;
        if (!_world.HasChunk(Dimension, cx, cz)) { SetStatus($"Chunk {cx}, {cz} oluşturulmamış — Chunk üret ile oluşturabilirsin"); return; }
        OwnedWindows.OfType<InspectorWindow>().FirstOrDefault()?.Close();
        new InspectorWindow(this, cx, cz) { Owner = this }.Show();
    }

    private void Show3D_Click(object sender, RoutedEventArgs e)
    {
        if (FocusChunk is not { } c) return;
        _view3d.Focus(c.X, c.Z);
        Nav3D.IsChecked = true;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_world is null || Map.Selection.Count == 0 || _busy) return;
        var world = _world; int dim = Dimension;
        var sel = Map.Selection.ToList();
        int existing = sel.Count(c => world.HasChunk(dim, c.X, c.Z));
        var dlg = new GenerateDialog(sel.Count, existing, world.Edition) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var options = dlg.Options; bool overwrite = dlg.Overwrite;
        var targets = sel.Where(c => overwrite || !world.HasChunk(dim, c.X, c.Z)).ToList();
        if (targets.Count == 0) { SetStatus("Üretilecek chunk yok (hepsi zaten var; üzerine yazmayı işaretle)"); return; }
        if (dlg.UseEngine) { await GenerateWithEngineAsync(world, dim, targets); return; }

        await RunBusy($"{targets.Count:N0} chunk üretiliyor…", () => Task.Run(() =>
        {
            Dispatcher.Invoke(() => _history.Record(world, dim, targets, $"{targets.Count} chunk üret ({options.Kind})"));
            foreach (var (x, z) in targets)
            {
                var v = world.HasChunk(dim, x, z) ? world.ReadChunk(dim, x, z)! : world.NewChunk(dim, x, z);
                TerrainGenerator.Fill(v, options);
                world.WriteChunk(dim, v, clearBlockEntities: true);
            }
        }));
        SetStatus($"{targets.Count:N0} chunk üretildi — kaydetmek için Ctrl+S");
        NotifyChanged(targets);
    }

    /// <summary>Resmi sunucuyla (BDS / server.jar) gerçek Minecraft arazisi üretir ve dünyaya alır.</summary>
    private async Task GenerateWithEngineAsync(IWorld world, int dim, List<(int X, int Z)> targets)
    {
        if (!Engine.ServerEngine.EulaAccepted)
        {
            var r = MessageBox.Show(this,
                "Gerçek arazi üretimi için resmi Minecraft sunucusu Mojang'dan indirilip bilgisayarında çalıştırılacak.\n\n" +
                "Bunun için Minecraft Son Kullanıcı Lisans Sözleşmesi'ni (EULA) kabul etmen gerekiyor:\nhttps://aka.ms/MinecraftEULA\n\n" +
                "EULA'yı okudum ve kabul ediyorum?", "Minecraft EULA", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            Engine.ServerEngine.AcceptEula();
        }
        if (targets.Count > 4096 && MessageBox.Show(this, $"{targets.Count:N0} chunk üretmek uzun sürebilir. Devam edilsin mi?", "Chunk üret",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // Sunucu diskteki dünyayı kullanır: önce kaydedilmemiş değişiklikleri yaz
        if (world.IsDirty)
        {
            if (MessageBox.Show(this, "Üretimden önce kaydedilmemiş değişiklikler kaydedilecek. Devam edilsin mi?", "Chunk üret",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            if (!await SaveAsync()) return;
        }

        Engine.ServerEngine engine = world.Edition == "Bedrock" ? new Engine.BedrockServerEngine() : new Engine.JavaServerEngine();
        var progress = new Progress<string>(s => { LoadingText.Text = s; SetStatus(s); });
        int imported = 0;
        string? copy = null;
        await RunBusy("Minecraft sunucusu hazırlanıyor…", async () =>
        {
            copy = await engine.GenerateAsync(world.RootPath, dim, targets, progress, CancellationToken.None);
            ((IProgress<string>)progress).Report("Üretilen chunk'lar dünyaya alınıyor…");
            _history.Record(world, dim, targets, $"{targets.Count} chunk üret (Minecraft motoru)");
            imported = await Task.Run(() => world.ImportChunks(copy, dim, targets));
        });
        if (copy is not null)
        {
            var dir = world.Edition == "Bedrock" ? copy : Path.GetDirectoryName(copy)!;
            _ = Task.Run(() => { try { Directory.Delete(dir, true); } catch { } });
        }
        if (copy is null) return;
        SetStatus(imported == targets.Count
            ? $"{imported:N0} chunk Minecraft motoruyla üretildi — kaydetmek için Ctrl+S"
            : $"{imported:N0} / {targets.Count:N0} chunk üretildi (eksikler için tekrar dene) — kaydetmek için Ctrl+S");
        NotifyChanged(targets);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_world is null || Map.Selection.Count == 0 || _busy) return;
        var world = _world; int dim = Dimension;
        var chunks = Map.Selection.Where(c => world.HasChunk(dim, c.X, c.Z)).ToList();
        if (chunks.Count == 0) { SetStatus("Seçimde oluşturulmuş chunk yok"); return; }
        if (MessageBox.Show(this, $"{chunks.Count:N0} chunk silinecek. Oyun bu alanları bir sonraki ziyarette yeniden oluşturur.\n\nGeri almak için Ctrl+Z kullanabilirsin. Devam edilsin mi?",
                "Chunk'ları Sil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await RunBusy($"{chunks.Count:N0} chunk siliniyor…", () => Task.Run(() =>
        {
            Dispatcher.Invoke(() => _history.Record(world, dim, chunks, $"{chunks.Count} chunk sil"));
            foreach (var (x, z) in chunks) world.DeleteChunk(dim, x, z);
        }));
        SetStatus($"{chunks.Count:N0} chunk silindi (kaydetmek için Ctrl+S)");
        NotifyChanged(chunks);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_world is null || Map.Selection.Count == 0 || _busy) return;
        var world = _world; int dim = Dimension;
        var sel = Map.Selection.ToList();
        int minX = sel.Min(c => c.X), minZ = sel.Min(c => c.Z);
        List<ClipItem> items = new();
        await RunBusy("Kopyalanıyor…", () => Task.Run(() =>
        {
            foreach (var (x, z) in sel)
                if (world.ReadChunk(dim, x, z) is { } v) items.Add(new ClipItem(x - minX, z - minZ, v.Clone()));
        }));
        _clipboard = items.Count > 0 ? items : null;
        SetStatus(items.Count > 0 ? $"{items.Count} chunk kopyalandı — hedefi seçip Ctrl+V" : "Seçimde kopyalanacak chunk yok");
        UpdateUi();
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_world is null || _clipboard is null || Map.Selection.Count == 0 || _busy) return;
        var world = _world; int dim = Dimension; var clip = _clipboard;
        int ax = Map.Selection.Min(c => c.X), az = Map.Selection.Min(c => c.Z);
        var targets = clip.Select(c => (X: ax + c.DX, Z: az + c.DZ)).ToList();
        int missing = targets.Count(t => !world.HasChunk(dim, t.X, t.Z));

        await RunBusy($"{targets.Count} chunk'a yapıştırılıyor…", () => Task.Run(() =>
        {
            Dispatcher.Invoke(() => _history.Record(world, dim, targets, $"{targets.Count} chunk yapıştır"));
            foreach (var item in clip)
            {
                int tx = ax + item.DX, tz = az + item.DZ;
                var target = world.ReadChunk(dim, tx, tz) ?? world.NewChunk(dim, tx, tz);
                target.CopyBlocksFrom(item.Volume);
                world.WriteChunk(dim, target, clearBlockEntities: true);
            }
        }));
        SetStatus($"{targets.Count} chunk'a yapıştırıldı" + (missing > 0 ? $" ({missing} yeni chunk oluşturuldu)" : "") + " — sandık içerikleri kopyalanmaz");
        Map.Selection.Clear();
        foreach (var t in targets) Map.Selection.Add(t);
        NotifyChanged(targets);
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_world is null || Map.Selection.Count == 0 || _busy) return;
        var world = _world; int dim = Dimension;
        var chunks = Map.Selection.Where(c => world.HasChunk(dim, c.X, c.Z)).ToList();
        if (chunks.Count == 0) { SetStatus("Seçimde oluşturulmuş chunk yok"); return; }

        var counts = new Dictionary<string, int>();
        await RunBusy("Bloklar taranıyor…", () => Task.Run(() =>
        {
            foreach (var (x, z) in chunks.Take(64))
                if (world.ReadChunk(dim, x, z) is { } v)
                    foreach (var (k, n) in v.CountBlocks()) counts[k] = counts.GetValueOrDefault(k) + n;
        }));
        var suggestions = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Where(s => !BlockColors.IsAir(s)).ToList();

        var dlg = new ReplaceDialog(suggestions, chunks.Count) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var from = BlockState.Normalize(dlg.From);
        var to = BlockState.Normalize(dlg.To);
        bool byName = dlg.MatchByName;
        var fromName = BlockState.BaseName(from);
        Func<string, string?> map = s => (byName ? BlockState.BaseName(s) == fromName : s == from) ? to : null;

        int total = 0, touched = 0;
        await RunBusy("Bloklar değiştiriliyor…", () => Task.Run(() =>
        {
            Dispatcher.Invoke(() => _history.Record(world, dim, chunks, $"{BlockState.BaseName(from)} → {BlockState.BaseName(to)}"));
            foreach (var (x, z) in chunks)
            {
                if (world.ReadChunk(dim, x, z) is not { } v) continue;
                int n = v.Replace(map);
                if (n == 0) continue;
                world.WriteChunk(dim, v);
                total += n; touched++;
            }
        }));
        SetStatus(total > 0 ? $"{total:N0} blok değiştirildi ({touched} chunk)" : "Eşleşen blok bulunamadı");
        NotifyChanged(chunks);
    }

    // ---------- Kaydet / dışa aktar ----------
    private void Save_Click(object sender, RoutedEventArgs e) => SaveWorld();

    public void SaveWorld() => _ = SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_world is null || !_world.IsDirty || _busy) return true;
        var world = _world;
        bool ok = false;
        await RunBusy("Kaydediliyor…", () => Task.Run(() =>
        {
            // Çalışma alanı dışındaki (ör. .minecraft/saves) dünyaları ilk kayıttan önce yedekle
            if (!_backedUp && !WorldLoader.IsInWorkspace(world.RootPath))
            {
                var dir = Path.Combine(Path.GetDirectoryName(WorldLoader.Workspace)!, "Backups");
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, $"{Path.GetFileName(world.RootPath)}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                ZipFile.CreateFromDirectory(world.RootPath, zip);
                _backedUp = true;
            }
            world.Save();
            ok = true;
        }));
        if (ok) { SetStatus("Kaydedildi ✓"); WorldName.Text = world.Name; Map.RefreshAll(); }
        UpdateUi();
        return ok;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_world is null || _busy) return;
        bool bedrock = _world.Edition == "Bedrock";
        var dlg = new SaveFileDialog
        {
            Title = "Dünyayı dışa aktar",
            FileName = _world.Name,
            Filter = bedrock ? "Bedrock dünyası (*.mcworld)|*.mcworld" : "Zip arşivi (*.zip)|*.zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (_world.IsDirty && !await SaveAsync()) return;

        var root = _world.RootPath; var target = dlg.FileName;
        await RunBusy("Dışa aktarılıyor…", () => Task.Run(() =>
        {
            if (File.Exists(target)) File.Delete(target);
            ZipFile.CreateFromDirectory(root, target, CompressionLevel.Optimal, includeBaseDirectory: !bedrock);
        }));
        SetStatus($"Dışa aktarıldı: {target}" + (bedrock ? " — oyuna almak için dosyayı çift tıkla" : ""));
    }

    private bool ConfirmDiscard()
    {
        if (_world is not { IsDirty: true }) return true;
        var r = MessageBox.Show(this, "Kaydedilmemiş değişiklikler var. Kaydedilsin mi?", "ProChunk Studio",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) _world.Save();
        return true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try { if (!ConfirmDiscard()) e.Cancel = true; }
        catch (Exception ex)
        {
            e.Cancel = MessageBox.Show(this, $"Kaydedilemedi: {ex.Message}\n\nYine de çıkılsın mı?", "Hata",
                MessageBoxButton.YesNo, MessageBoxImage.Error) != MessageBoxResult.Yes;
        }
    }

    private async void ShowChunkInfo(int cx, int cz)
    {
        if (_world is null) return;
        var world = _world; int dim = Map.Dimension;
        ChunkInfo.Text = $"Chunk {cx}, {cz}\nOkunuyor…";
        ChunkInfo.Text = await Task.Run(() =>
        {
            try
            {
                var text = world.DescribeChunk(dim, cx, cz);
                int containers = world.HasChunk(dim, cx, cz) ? world.ReadBlockEntities(dim, cx, cz).Count(b => b.Contains("Items")) : 0;
                return text + (containers > 0 ? $"\nKonteyner {containers}" : "") + "\n\nÇift tık: Chunk Denetçisi";
            }
            catch (Exception ex) { return $"Chunk {cx}, {cz}\n\nOkuma hatası:\n{ex.Message}"; }
        });
        UpdateUi();
    }
}
