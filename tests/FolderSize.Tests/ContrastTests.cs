using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class ContrastTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SpaceMapTextMeetsWcagAaAcrossTheSizePalette(bool dark) => Sta.Run(() =>
    {
        var map = new TreemapView { Background = dark ? Brushes.Black : Brushes.White };
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32);
        for (var index = 0; index <= 256; index++)
        {
            map.Items =
            [
                new MapItem(null, 256, "Small"),
                new MapItem(null, 256 + index, "Sample"),
                new MapItem(null, 512, "Large")
            ];
            map.Measure(new Size(900, 600));
            map.Arrange(new Rect(0, 0, 900, 600));
            map.UpdateLayout();
            bitmap.Clear();
            bitmap.Render(map);
            var drawings = DrawingDescendants(Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(map))).ToArray();
            var backgrounds = drawings.OfType<GeometryDrawing>()
                .Where(drawing => drawing.Geometry is RectangleGeometry && drawing.Brush is not null)
                .Skip(1).Select(drawing => ColorOf(drawing.Brush)).ToArray();
            Assert.Equal(3, backgrounds.Length);
            Assert.Contains(backgrounds, color => color.R > color.G && color.R > color.B);
            var text = drawings.OfType<GlyphRunDrawing>().ToArray();
            Assert.NotEmpty(text);
            foreach (var glyph in text)
                foreach (var background in backgrounds)
                    AssertRatio(ColorOf(glyph.ForegroundBrush), background, 4.5);
        }
        return Task.CompletedTask;
    });

    private static IEnumerable<Drawing> DrawingDescendants(DrawingGroup group)
    {
        foreach (var drawing in group.Children)
        {
            yield return drawing;
            if (drawing is DrawingGroup nested)
                foreach (var child in DrawingDescendants(nested)) yield return child;
        }
    }

    [Theory]
    [InlineData(false, 1240)]
    [InlineData(true, 1240)]
    [InlineData(false, 820)]
    [InlineData(true, 820)]
    public Task TabBordersAreNotClipped(bool dark, double width) => Sta.Run(async () =>
    {
        var window = new MainWindow { Width = width, Height = 640 };
        try
        {
#pragma warning disable WPF0001
            window.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach (var tabs in new[] { (TabControl)window.FindName("SpaceMapTabs") })
            {
                foreach (TabItem selected in tabs.Items)
                {
                    selected.IsSelected = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    foreach (TabItem tab in tabs.Items)
                    {
                        var border = Assert.IsType<Border>(VisualTreeHelper.GetChild(tab, 0));
                        var bounds = border.TransformToAncestor(tab).TransformBounds(new Rect(border.RenderSize));
                        var clip = VisualTreeHelper.GetClip(tab);
                        Assert.True(clip is null || clip.Bounds.Contains(bounds),
                            $"{tab.Header}: border {bounds} extends past clip {clip?.Bounds}");
                        Assert.True(new Rect(tab.RenderSize).Contains(bounds));
                    }
                }
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FluentSurfacesStaySubtleAndTextReadableWhenSwitchingThemes() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("a.txt", new string('x', 2048));
        folder.Write("b.txt", new string('x', 2048));
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await window.Model.FindAsync();
            window.Model.LargeFileSize = "0";
            await window.Model.FindLargeFilesAsync();
            var navigation = (ListBox)window.FindName("MainNavigation");
            foreach (var dark in new[] { false, true, false })
            {
#pragma warning disable WPF0001
                window.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var background = ColorOf(window.Background);
                var card = Composite(ColorOf((Brush)window.FindResource("CardBrush")), background);
                Assert.Equal(dark, background.R < 128);
                Assert.InRange(Ratio(card, background), 1, 1.5);
                AssertRatio(ColorOf((Brush)window.FindResource("SecondaryTextBrush")), card, 4.5);
                Assert.InRange(Ratio(ColorOf((Brush)window.FindResource("CardBorderBrush")), card), 1, 1.5);

                var names = new[] { "UsageGrid", "DuplicatesGrid", "LargeFilesGrid" };
                for (var index = 0; index < names.Length; index++)
                {
                    navigation.SelectedIndex = index;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var grid = (DataGrid)window.FindName(names[index]);
                    grid.UnselectAll();
                    window.UpdateLayout();
                    var first = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                    var second = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(1));
                    var rowBackground = Composite(ColorOf(first.Background), card);
                    Assert.Equal(rowBackground, Composite(ColorOf(second.Background), card));
                    AssertRatio(ColorOf(first.Foreground), rowBackground, 4.5);
                    AssertRatio(ColorOf(second.Foreground), rowBackground, 4.5);

                    var header = Descendants<DataGridColumnHeader>(grid).First(item => item.Column is not null);
                    var headerBackground = Composite(ColorOf(header.Background), card);
                    AssertRatio(ColorOf(header.Foreground), headerBackground, 4.5);
                    Assert.InRange(Ratio(headerBackground, card), 1, 1.5);
                    grid.SelectedItem = grid.Items[0];
                    window.UpdateLayout();
                    var cell = Descendants<DataGridCell>(first).First();
                    var cellBorder = Assert.IsType<Border>(cell.Template.FindName("Border", cell));
                    var selectedBackground = Composite(ColorOf(cellBorder.Background), card);
                    Assert.NotEqual(card, selectedBackground);
                    AssertRatio(ColorOf(cell.Foreground), selectedBackground, 4.5);

                    var item = (ListBoxItem)navigation.Items[index];
                    var indicator = Assert.IsType<Border>(item.Template.FindName("ActiveIndicator", item));
                    Assert.Equal(Visibility.Visible, indicator.Visibility);
                    var selectedSurface = Composite(ColorOf(item.Background), background);
                    Assert.InRange(Ratio(selectedSurface, background), 1, 1.5);
                    AssertRatio(ColorOf(item.Foreground), selectedSurface, 4.5);
                    AssertRatio(ColorOf(indicator.Background), selectedSurface, 3);
                    var unselected = (ListBoxItem)navigation.Items[(index + 1) % navigation.Items.Count];
                    Assert.NotEqual(ColorOf(item.Background), ColorOf(unselected.Background));
                    Assert.Equal(Visibility.Collapsed,
                        Assert.IsType<Border>(unselected.Template.FindName("ActiveIndicator", unselected)).Visibility);
                }
            }
        }
        finally { window.Close(); }
    });

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void AssertRatio(Color foreground, Color background, double minimum)
    {
        var ratio = Ratio(foreground, background);
        Assert.True(ratio >= minimum, $"{foreground} on {background}: {ratio:F2}:1, expected at least {minimum}:1");
    }

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Ratio(Color foreground, Color background)
    {
        var first = Luminance(Composite(foreground, background));
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
