using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class TreemapTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RenderedTilesUseSizeInsteadOfNamesForGreenYellowRedScale(bool folderMap) => Sta.Run(() =>
    {
        using var folder = new TestFolder();
        folder.Write("small.txt", new string('x', 100));
        folder.Write("medium.txt", new string('x', 200));
        folder.Write("large.txt", new string('x', 300));
        var root = new FolderScanner().Scan(folder.Root, true).Root;
        var items = root.Children.Select(node => new MapItem(node, node.Size, node.Name)).ToArray();
        var map = folderMap ? new TreemapView { Node = root } : new TreemapView { Items = items };
        var colors = RenderTileColors(map, items);
        var small = colors[100];
        var medium = colors[200];
        var large = colors[300];
        Assert.True(small.G > small.R && small.G > small.B);
        Assert.True(medium.R > 200 && medium.G > 180 && medium.B < 80);
        Assert.True(large.R > large.G && large.R > large.B);
        Assert.True(small.R < medium.R && medium.G > large.G);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task EqualSizedTilesAndSingleTilesUseGreen(int count) => Sta.Run(() =>
    {
        var items = Enumerable.Range(0, count).Select(index => new MapItem(null, 100, $"Entry {index}")).ToArray();
        var colors = RenderTileColors(new TreemapView { Items = items }, items);
        var color = Assert.Single(colors).Value;
        Assert.True(color.G > color.R && color.G > color.B);
        return Task.CompletedTask;
    });

    private static Dictionary<long, Color> RenderTileColors(TreemapView map, IReadOnlyList<MapItem> items)
    {
        map.Measure(new Size(600, 400));
        map.Arrange(new Rect(0, 0, 600, 400));
        map.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(map);
        var colors = new Dictionary<long, Color>();
        foreach (var tile in TreemapLayout.CreateItems(items, 600, 400))
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)tile.Bounds.X + 3, (int)tile.Bounds.Y + 3, 1, 1), pixel, 4, 0);
            var color = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            if (colors.TryGetValue(tile.Item.Size, out var previous)) Assert.Equal(previous, color);
            colors[tile.Item.Size] = color;
        }
        return colors;
    }

    [Theory]
    [InlineData(true, 13)]
    [InlineData(false, 10)]
    public void GroupedMapsRespectScopeAndAccountForEveryByte(bool recursive, long total)
    {
        using var folder = new TestFolder();
        folder.Write("photo.JPG", "1234");
        folder.Write(@"nested\photo.jpg", "123");
        folder.Write("clip.mp4", "12");
        folder.Write("readme", "1");
        folder.Write("unknown.xyz", "123");
        folder.Write("empty.pdf", "");
        var root = new FolderScanner().Scan(folder.Root, recursive).Root;
        var groups = SpaceMapGroups.Create(root);
        Assert.Equal(total, root.Size);
        foreach (var items in new[] { groups.Extensions, groups.Kinds })
        {
            Assert.Equal(total, items.Sum(item => item.Size));
            Assert.All(items, item => Assert.Null(item.Node));
            var tiles = TreemapLayout.CreateItems(items, 800, 600);
            Assert.Equal(total, tiles.Sum(tile => tile.Item.Size));
            Assert.Equal(800 * 600, tiles.Sum(tile => tile.Bounds.Width * tile.Bounds.Height), 5);
        }
        Assert.Equal(recursive ? 7 : 4, Assert.Single(groups.Extensions, item => item.Label == ".jpg").Size);
        Assert.Equal(recursive ? 7 : 4, Assert.Single(groups.Kinds, item => item.Label == "Images").Size);
        Assert.Equal(2, Assert.Single(groups.Kinds, item => item.Label == "Videos").Size);
        Assert.Equal(1, Assert.Single(groups.Kinds, item => item.Label == "No extension").Size);
        Assert.Equal(3, Assert.Single(groups.Kinds, item => item.Label == "Other").Size);
        Assert.DoesNotContain(groups.Extensions, item => item.Label == ".pdf");
        var child = Assert.Single(root.Children, node => node.IsDirectory);
        Assert.Equal(recursive ? 3 : 0, SpaceMapGroups.Create(child).Extensions.Sum(item => item.Size));
    }

    [Fact]
    public void EmptyFolderHasNoGroupedTiles()
    {
        using var folder = new TestFolder();
        var groups = SpaceMapGroups.Create(new FolderScanner().Scan(folder.Root, true).Root);
        Assert.Empty(groups.Extensions);
        Assert.Empty(groups.Kinds);
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(200, 900)]
    [InlineData(1, 1)]
    public void LayoutAccountsForAreaWithoutOverlap(double width, double height)
    {
        using var folder = new TestFolder();
        for (var i = 1; i <= 30; i++) folder.Write(i.ToString(), new string('x', i));
        var scan = new FolderScanner().Scan(folder.Root, true);
        var tiles = TreemapLayout.Create(scan.Root.Children, width, height);
        Assert.Equal(width * height, tiles.Sum(tile => tile.Bounds.Width * tile.Bounds.Height), 5);
        Assert.Equal(scan.Root.Size, tiles.Sum(tile => tile.Item.Size));
        for (var i = 0; i < tiles.Count; i++)
        {
            var a = tiles[i].Bounds;
            Assert.True(a.X >= -0.00001 && a.Y >= -0.00001);
            Assert.True(a.X + a.Width <= width + 0.00001 && a.Y + a.Height <= height + 0.00001);
            foreach (var b in tiles.Skip(i + 1).Select(tile => tile.Bounds))
            {
                var overlapX = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var overlapY = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.True(overlapX <= 0.00001 || overlapY <= 0.00001);
            }
        }
    }

    [Fact]
    public void EmptyAndZeroSizedInputsAreSafe()
    {
        using var folder = new TestFolder();
        folder.Write("empty", "");
        var nodes = new FolderScanner().Scan(folder.Root, true).Root.Children;
        Assert.Empty(TreemapLayout.Create(nodes, 800, 600));
        Assert.Empty(TreemapLayout.Create(nodes, 0, 600));
    }
}
