using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ProChunk.Core;

namespace ProChunk.App.Inspector;

/// <summary>Bir chunk kesitini hücre ızgarası olarak çizer. (u, v) = ekran sütun/satırı.</summary>
public sealed class SliceView : FrameworkElement
{
    private Func<int, int, string>? _cell;
    private int _cols = 16, _rows = 16;
    private double _cellSize = 32;
    private (int U, int V)? _hover;
    private bool _pressed;

    public string? Highlight { get; set; }
    public bool ShowGrid { get; set; } = true;
    /// <summary>Yandan görünümde satır etiketleri için (satır → Y değeri).</summary>
    public Func<int, string>? RowLabel { get; set; }

    public event Action<int, int>? CellHover;
    public event Action<int, int>? CellPressed;   // basıldı veya basılıyken üzerine gelindi
    public event Action? StrokeEnded;

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 2));
    private static readonly Pen HighlightPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3B)), 2));
    private static readonly Brush AirBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1F)));
    private static readonly Dictionary<uint, Brush> Brushes_ = new();
    private const double LabelWidth = 36;

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public SliceView() { RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); }

    public void SetContent(int cols, int rows, double cellSize, Func<int, int, string> cell)
    {
        _cols = cols; _rows = rows; _cellSize = cellSize; _cell = cell;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double Left => RowLabel is null ? 0 : LabelWidth;

    protected override Size MeasureOverride(Size availableSize) => new(Left + _cols * _cellSize, _rows * _cellSize);

    private static Brush BrushFor(string state)
    {
        if (BlockColors.IsAir(state)) return AirBrush;
        var c = BlockColors.Get(state);
        lock (Brushes_)
        {
            if (!Brushes_.TryGetValue(c, out var b))
                Brushes_[c] = b = Freeze(new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)));
            return b;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_cell is null) return;
        double l = Left, s = _cellSize;
        var typeface = new Typeface("Consolas");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int v = 0; v < _rows; v++)
        {
            for (int u = 0; u < _cols; u++)
            {
                var state = _cell(u, v);
                var r = new Rect(l + u * s, v * s, s, s);
                dc.DrawRectangle(BrushFor(state), ShowGrid && s >= 8 ? GridPen : null, r);
                if (Highlight is not null && state == Highlight)
                    dc.DrawRectangle(null, HighlightPen, new Rect(r.X + 1, r.Y + 1, s - 2, s - 2));
            }
            if (RowLabel is not null && (s >= 10 || v % 4 == 0))
            {
                var ft = new FormattedText(RowLabel(v), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, Math.Min(11, s * 0.8), Brushes.Gray, dip);
                dc.DrawText(ft, new Point(l - ft.Width - 4, v * s + (s - ft.Height) / 2));
            }
        }
        if (_hover is { } h) dc.DrawRectangle(null, HoverPen, new Rect(l + h.U * s, h.V * s, s, s));
    }

    private (int U, int V)? CellAt(Point p)
    {
        int u = (int)Math.Floor((p.X - Left) / _cellSize), v = (int)Math.Floor(p.Y / _cellSize);
        return u >= 0 && u < _cols && v >= 0 && v < _rows ? (u, v) : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var c = CellAt(e.GetPosition(this));
        if (c == _hover) return;
        _hover = c;
        if (c is { } h)
        {
            CellHover?.Invoke(h.U, h.V);
            if (_pressed) CellPressed?.Invoke(h.U, h.V);
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (CellAt(e.GetPosition(this)) is not { } c) return;
        _pressed = true;
        CaptureMouse();
        CellPressed?.Invoke(c.U, c.V);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
        StrokeEnded?.Invoke();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }
}
