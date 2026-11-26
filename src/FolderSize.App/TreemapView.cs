using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FolderSize.Core;

namespace FolderSize.App;

public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush),
        typeof(TreemapView), new FrameworkPropertyMetadata(Brushes.WhiteSmoke, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush),
        typeof(TreemapView), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(nameof(SelectionBrush), typeof(Brush),
        typeof(TreemapView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(nameof(Node), typeof(ScanNode),
        typeof(TreemapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Reset));
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<MapItem>),
        typeof(TreemapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Reset));
    public IReadOnlyList<MapItem>? Items { get => (IReadOnlyList<MapItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(nameof(SelectedNode),
        typeof(ScanNode), typeof(TreemapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ScanNode? Node { get => (ScanNode?)GetValue(NodeProperty); set => SetValue(NodeProperty, value); }
    public ScanNode? SelectedNode { get => (ScanNode?)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public event Action<ScanNode>? NodeSelected;
    public event Action<ScanNode>? NodeOpened;
    private IReadOnlyList<MapTile> tiles = [];
    private Size layoutSize;
    private long smallestSize, largestSize;
    private bool dirty = true;
    private static readonly Brush[] Palette = MakePalette();

    public TreemapView()
    {
        ClipToBounds = true;
        Focusable = true;
        ToolTip = "Scan a folder to view its contents.";
    }

    private static void Reset(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((TreemapView)target).dirty = true;

    private static Brush[] MakePalette()
    {
        Color[] stops = [Color.FromRgb(34, 197, 94), Color.FromRgb(250, 204, 21), Color.FromRgb(239, 68, 68)];
        return Enumerable.Range(0, 257).Select(index =>
        {
            var start = index <= 128 ? stops[0] : stops[1];
            var end = index <= 128 ? stops[1] : stops[2];
            var fraction = index <= 128 ? index / 128d : (index - 128) / 128d;
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Round(start.R + (end.R - start.R) * fraction),
                (byte)Math.Round(start.G + (end.G - start.G) * fraction),
                (byte)Math.Round(start.B + (end.B - start.B) * fraction)));
            brush.Freeze();
            return (Brush)brush;
        }).ToArray();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        var borderPen = new Pen(Background, 2);
        var selectedPen = new Pen(SelectionBrush, 3);
        if (dirty || layoutSize != RenderSize)
        {
            tiles = Items is { } items ? TreemapLayout.CreateItems(items, ActualWidth, ActualHeight) :
                TreemapLayout.Create(Node?.Children ?? [], ActualWidth, ActualHeight);
            smallestSize = tiles.Count == 0 ? 0 : tiles.Min(tile => tile.Item.Size);
            largestSize = tiles.Count == 0 ? 0 : tiles.Max(tile => tile.Item.Size);
            layoutSize = RenderSize;
            dirty = false;
        }
        foreach (var tile in tiles)
        {
            var b = tile.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;
            var rect = new Rect(b.X, b.Y, b.Width, b.Height);
            var fraction = largestSize == smallestSize ? 0 :
                (double)(tile.Item.Size - smallestSize) / (largestSize - smallestSize);
            var brush = Palette[(int)Math.Round(fraction * (Palette.Length - 1))];
            dc.DrawRectangle(brush, tile.Item.Node is not null && tile.Item.Node == SelectedNode ? selectedPen : borderPen, rect);
            if (b.Width >= 65 && b.Height >= 34)
            {
                var text = new FormattedText($"{tile.Item.Label}\n{SizeText.Format(tile.Item.Size)}",
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
                    Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = Math.Max(1, b.Width - 10), MaxTextHeight = Math.Max(1, b.Height - 8),
                    Trimming = TextTrimming.CharacterEllipsis
                };
                dc.DrawText(text, new Point(b.X + 5, b.Y + 4));
            }
        }
        if (tiles.Count == 0)
        {
            var text = new FormattedText("No measured non-empty files or folders.", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(14, 14));
        }
    }

    private MapTile? Hit(Point point) => tiles.FirstOrDefault(tile =>
        new Rect(tile.Bounds.X, tile.Bounds.Y, tile.Bounds.Width, tile.Bounds.Height).Contains(point));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tile = Hit(e.GetPosition(this));
        ToolTip = tile is null ? null : $"{tile.Item.Node?.FullPath ?? tile.Item.Label}\n{SizeText.Format(tile.Item.Size)}";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Hit(e.GetPosition(this))?.Item.Node is not { } node) return;
        Focus();
        SelectedNode = node;
        NodeSelected?.Invoke(node);
        if (e.ClickCount == 2) NodeOpened?.Invoke(node);
    }
}
