using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class ShellIconTests
{
    [Fact]
    public async Task LoadsDefaultFolderAndAssociatedFileIconsWithSharedFrozenCache()
    {
        var text = ShellIcons.GetAsync("sample.txt", false);
        var sameType = ShellIcons.GetAsync("other.TXT", false);
        Assert.Same(text, sameType);
        foreach (var task in new[] { text, ShellIcons.GetAsync("folder", true),
                     ShellIcons.GetAsync("file.foldersize-unknown-extension", false) })
        {
            var icon = await task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(icon.IsFrozen);
            Assert.True(icon.PixelWidth > 0 && icon.PixelHeight > 0);
        }
    }

    [Fact]
    public Task RecycledRowUsesItsCurrentIconAndNameSortingStillWorks() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("z.txt", "text");
        folder.Write(@"a-folder\file", "other");
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)window.FindName("UsageGrid");
            Assert.Equal("Name", grid.Columns[0].SortMemberPath);
            var row = window.Model.Rows.First(item => item.Kind == "Folder");
            var text = window.Model.Rows.First(item => item.Kind == "File");
            var icon = new FileTypeIcon { Row = text };
            var host = new System.Windows.Window { Content = icon, Width = 100, Height = 100 };
            try
            {
                host.Show();
                icon.Row = row;
                var expected = await ShellIcons.GetAsync(row.Name, true);
                for (var i = 0; i < 100 && icon.Source is null; i++) await Task.Delay(20);
                Assert.Same(expected, Assert.IsAssignableFrom<BitmapSource>(icon.Source));
                icon.Row = null;
                Assert.Null(icon.Source);
            }
            finally { host.Close(); }
        }
        finally { window.Close(); }
    });
}
