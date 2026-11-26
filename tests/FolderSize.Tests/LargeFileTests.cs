using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class LargeFileTests
{
    [Theory]
    [InlineData("1", "kb", 1024L)]
    [InlineData("1", "mb", 1048576L)]
    [InlineData("1", "gb", 1073741824L)]
    [InlineData("1", "tb", 1099511627776L)]
    [InlineData("1.5", "kb", 1536L)]
    [InlineData("0.001", "kb", 2L)]
    [InlineData("0", "tb", 0L)]
    public void ConvertsThresholdToInclusiveByteBoundary(string value, string unit, long bytes) =>
        Assert.Equal(bytes, FileSizeThreshold.Parse(value, unit, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("", "mb")]
    [InlineData("-1", "kb")]
    [InlineData("NaN", "gb")]
    [InlineData("abc", "mb")]
    [InlineData("999999999999999999999999999", "tb")]
    [InlineData("1", "pb")]
    public void InvalidThresholdIsRejected(string value, string unit) =>
        Assert.Throws<ArgumentException>(() => FileSizeThreshold.Parse(value, unit, CultureInfo.InvariantCulture));

    [Fact]
    public void DecimalSeparatorRespectsUserCulture() =>
        Assert.Equal(1536, FileSizeThreshold.Parse("1,5", "kb", CultureInfo.GetCultureInfo("fr-FR")));

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    public Task FiltersCompletedScanInclusivelyWithoutFolders(bool recursive, int count) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("below", new string('a', 1023));
        folder.Write("equal", new string('a', 1024));
        folder.Write("above", new string('a', 2048));
        folder.Write(@"nested\large", new string('a', 4096));
        var model = new MainViewModel { FolderPath = folder.Root, Recursive = recursive, LargeFileSize = "1", LargeFileUnit = "kb" };
        await model.ScanAsync();
        if (recursive) await model.NavigateAsync(Assert.Single(model.Rows, row => row.Kind == "Folder").Node);
        model.FolderPath = "unscanned path";
        await model.FindLargeFilesAsync();
        Assert.Equal(count, model.LargeFiles.Count);
        Assert.All(model.LargeFiles, row => Assert.False(row.Node.IsDirectory));
        Assert.Contains(model.LargeFiles, row => row.Name == "equal");
        Assert.DoesNotContain(model.LargeFiles, row => row.Name == "below");
        Assert.Equal(model.LargeFiles.OrderByDescending(row => row.Bytes), model.LargeFiles);
        model.LargeFileSize = "0";
        Assert.Empty(model.LargeFiles);
        await model.FindLargeFilesAsync();
        Assert.Equal(recursive ? 4 : 3, model.LargeFiles.Count);
        model.LargeFileSize = "invalid";
        await model.FindLargeFilesAsync();
        Assert.Empty(model.LargeFiles);
        Assert.Contains("non-negative number", model.LargeFilesSummary);
        model.LargeFileSize = "100";
        model.LargeFileUnit = "tb";
        await model.FindLargeFilesAsync();
        Assert.Empty(model.LargeFiles);
        Assert.Contains("0 files", model.LargeFilesSummary);
        model.FolderPath = folder.Root;
        await model.ScanAsync();
        Assert.Empty(model.LargeFiles);
    });

    [Fact]
    public Task LargeFilesTabControlsShowResultsAndCancelCleanly() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("large.txt", new string('x', 2048));
        var window = new MainWindow();
        try
        {
            window.Show();
            var tab = (TabItem)window.FindName("LargeFilesTab");
            var duplicates = (TabItem)window.FindName("DuplicatesTab");
            var tabs = Assert.IsType<TabControl>(ItemsControl.ItemsControlFromItemContainer(tab));
            Assert.Equal(tabs.Items.IndexOf(duplicates) + 1, tabs.Items.IndexOf(tab));
            tab.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var show = (Button)window.FindName("ShowLargeFilesButton");
            Assert.False(show.IsEnabled);
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            ((TextBox)window.FindName("LargeFileSizeBox")).Text = "1";
            ((ComboBox)window.FindName("LargeFileUnitBox")).SelectedIndex = 0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("1", window.Model.LargeFileSize);
            Assert.Equal("kb", window.Model.LargeFileUnit);
            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (window.Model.Busy) await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)window.FindName("LargeFilesGrid");
            Assert.Single(grid.Items);
            Assert.Equal("large.txt", Assert.Single(window.Model.LargeFiles).Name);
            Assert.False(window.Model.CanRecycleLargeFiles);
            Assert.False(window.Model.LargeFiles[0].Selected);
            window.Model.LargeFiles[0].Selected = true;
            Assert.True(window.Model.CanRecycleLargeFiles);
            Assert.True(((Button)window.FindName("RecycleLargeFilesButton")).IsEnabled);
            var nameColumn = grid.Columns.Single(column => Equals(column.Header, "Name"));
            Assert.Equal("Name", nameColumn.SortMemberPath);
            var content = Assert.IsType<ContentPresenter>(nameColumn.GetCellContent(grid.Items[0]));
            content.ApplyTemplate();
            var panel = Assert.IsType<DockPanel>(VisualTreeHelper.GetChild(content, 0));
            var icon = Assert.IsType<FileTypeIcon>(panel.Children[0]);
            Assert.Same(window.Model.LargeFiles[0], icon.Row);
            var expected = await ShellIcons.GetAsync("large.txt", false);
            for (var i = 0; i < 100 && icon.Source is null; i++) await Task.Delay(20);
            Assert.Same(expected, icon.Source);
            var pending = window.Model.FindLargeFilesAsync();
            window.Model.Cancel();
            await pending;
            Assert.Empty(window.Model.LargeFiles);
            Assert.False(window.Model.CanRecycleLargeFiles);
            Assert.False(((Button)window.FindName("RecycleLargeFilesButton")).IsEnabled);
            Assert.Contains("cancelled", window.Model.LargeFilesSummary);
            Assert.False(window.Model.Busy);
        }
        finally { window.Close(); }
    });
}
