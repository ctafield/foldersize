using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class FileRecyclingTests
{
    [Fact]
    public void RecyclesOnlyUnchangedFilesAndReportsMissingChangedOrRefusedFiles()
    {
        using var folder = new TestFolder();
        folder.Write("good", "good");
        var changed = folder.Write("changed", "original");
        var missing = folder.Write("missing", "missing");
        folder.Write("refused", "refused");
        var scan = new FolderScanner().Scan(folder.Root, true);
        File.AppendAllText(changed, "new content");
        File.Delete(missing);
        var paths = new List<string>();
        var recycler = new CallbackRecycler(path =>
        {
            if (Path.GetFileName(path) == "refused") throw new IOException("Recycling unavailable.");
            paths.Add(path);
        });
        var result = new FileRecycling(new WindowsFileAccess(), recycler).Recycle(scan.Root.Files().ToArray());
        Assert.Equal("good", Path.GetFileName(Assert.Single(result.Recycled)));
        Assert.Single(paths);
        Assert.Equal(3, result.Issues.Count);
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(Path.Combine(folder.Root, "good")));
    }

    [Fact]
    public void RechecksModificationTimeEvenWhenLengthMatches()
    {
        using var folder = new TestFolder();
        var path = folder.Write("changed", "same");
        var scan = new FolderScanner().Scan(folder.Root, true);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var result = new FileRecycling(new WindowsFileAccess(),
            new CallbackRecycler(_ => Assert.Fail("Changed file must not reach the recycler.")))
            .Recycle(scan.Root.Files().ToArray());
        Assert.Empty(result.Recycled);
        Assert.Contains("changed since", Assert.Single(result.Issues).Message);
    }

    [Fact]
    public void CancellationStopsBetweenFilesAndDirectoriesAreRejected()
    {
        using var folder = new TestFolder();
        folder.Write("a", "a");
        folder.Write("b", "b");
        var scan = new FolderScanner().Scan(folder.Root, true);
        using var source = new CancellationTokenSource();
        var service = new FileRecycling(new WindowsFileAccess(), new CallbackRecycler(_ => source.Cancel()));
        Assert.Throws<ArgumentException>(() => service.Recycle([scan.Root]));
        var result = service.Recycle(scan.Root.Files().ToArray(), source.Token);
        Assert.Single(result.Recycled);
        Assert.Contains("cancelled", Assert.Single(result.Issues).Message);
        var cancelled = service.Recycle(scan.Root.Files().ToArray(), source.Token);
        Assert.Empty(cancelled.Recycled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfirmationShowsSelectionAndCanBeCancelledWithoutChangingFiles(bool contextMenu) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = folder.Write("selected.txt", new string('x', 2048));
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            window.Model.LargeFileSize = "0";
            await window.Model.FindLargeFilesAsync();
            ((TabItem)window.FindName("LargeFilesTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.Model.LargeFiles[0].Selected = true;
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Window? dialog = null;
                try
                {
                    dialog = Assert.Single(window.OwnedWindows.Cast<Window>());
                    Assert.Equal("Confirm recycling", dialog.Title);
                    var panel = Assert.IsType<DockPanel>(dialog.Content);
                    var heading = panel.Children.OfType<TextBlock>().Single().Text;
                    Assert.Contains("1 files", heading);
                    Assert.Contains(SizeText.Format(2048), heading);
                    Assert.Contains("No duplicate copy", heading);
                    Assert.Equal(path, Assert.Single(panel.Children.OfType<ListBox>().Single().Items.Cast<string>()));
                    var cancel = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                        .Single(button => button.Name == "CancelRecycleButton");
                    Assert.True(cancel.IsDefault);
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    handled.SetResult();
                }
                catch (Exception ex) { handled.SetException(ex); }
                finally { dialog?.Close(); }
            }));
            if (contextMenu)
            {
                var grid = (DataGrid)window.FindName("LargeFilesGrid");
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                var menu = row.ContextMenu;
                menu.PlacementTarget = row;
                menu.IsOpen = true;
                menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
            }
            else ((Button)window.FindName("RecycleLargeFilesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await handled.Task;
            Assert.True(File.Exists(path));
            Assert.True(window.Model.LargeFiles[0].Selected);
            Assert.False(window.Model.Busy);
        }
        finally { window.Close(); }
    });

    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task RecyclesOnlySelectedFixturesAndRefreshesOriginalScanScopeAfterPartialFailure() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var selected = folder.Write("selected", "same");
        var missing = folder.Write("missing", "same");
        var retained = folder.Write("retained", "same");
        var nested = folder.Write(@"nested\unscanned", "same");
        var model = new MainViewModel { FolderPath = folder.Root, Recursive = false, LargeFileSize = "0" };
        await model.ScanAsync();
        await model.FindAsync();
        await model.FindLargeFilesAsync();
        Assert.False(model.CanRecycleLargeFiles);
        foreach (var row in model.LargeFiles.Where(row => row.FullPath != retained)) row.Selected = true;
        File.Delete(missing);
        model.FolderPath = "an edited, unscanned path";
        model.Recursive = true;
        await model.RecycleLargeFilesAsync();
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(retained));
        Assert.True(File.Exists(nested));
        Assert.Equal(retained, Assert.Single(model.LargeFiles).FullPath);
        Assert.False(model.LargeFiles[0].Selected);
        Assert.False(model.CanRecycleLargeFiles);
        Assert.False(model.Recursive);
        Assert.Equal(folder.Root, model.FolderPath);
        Assert.Empty(model.DuplicateRows);
        Assert.Contains(model.Issues, issue => issue.Path == missing);
        Assert.Contains("1 files confirmed recycled", model.Status);
        Assert.Contains("Results refreshed", model.Status);
    });

    private sealed class CallbackRecycler(Action<string> recycle) : IRecycler
    {
        public void Recycle(string path) => recycle(path);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("unscanned")]
    public void FolderRecyclingRefusesChangedOrIncompleteContents(string change)
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"nested\file.txt", "original");
        var scan = new FolderScanner().Scan(folder.Root, change != "unscanned");
        var node = Assert.Single(scan.Root.Children);
        if (change == "added") folder.Write(@"nested\new.txt", "new");
        if (change == "changed") File.AppendAllText(path, "changed");
        if (change == "missing") File.Delete(path);
        var result = new FolderRecycling(new WindowsFileAccess(),
            new CallbackRecycler(_ => Assert.Fail("Unsafe folder must not reach the recycler."))).Recycle(node);
        Assert.Empty(result.Recycled);
        Assert.NotEmpty(result.Issues);
        Assert.True(Directory.Exists(node.FullPath));
    }

    [Fact]
    public void CancelledFolderRecyclingNeverCallsTheRecycler()
    {
        using var folder = new TestFolder();
        folder.Write(@"nested\file.txt", "original");
        var scan = new FolderScanner().Scan(folder.Root, true);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var service = new FolderRecycling(new WindowsFileAccess(),
            new CallbackRecycler(_ => Assert.Fail("Cancelled folder must not reach the recycler.")));
        Assert.Throws<ArgumentException>(() => service.Recycle(scan.Root));
        var result = service.Recycle(Assert.Single(scan.Root.Children), source.Token);
        Assert.Empty(result.Recycled);
        Assert.Contains("cancelled", Assert.Single(result.Issues).Message);
    }

    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task ContextFolderRecyclingMovesOnlyTheChosenFolderAndRefreshesWithoutFindingDuplicates() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var chosen = folder.Write(@"chosen\nested\file.txt", "chosen");
        var retained = folder.Write(@"retained\file.txt", "retained");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        var node = model.Rows.Single(row => row.Name == "chosen").Node;
        await model.RecycleItemAsync(node);
        Assert.False(File.Exists(chosen));
        Assert.False(Directory.Exists(node.FullPath));
        Assert.True(File.Exists(retained));
        Assert.Equal("retained", Assert.Single(model.Rows).Name);
        Assert.Empty(model.Issues);
        Assert.Empty(model.DuplicateRows);
        Assert.Contains("Run Find duplicates", model.DuplicateSummary);
        Assert.Contains("1 files confirmed recycled", model.Status);
    });
}
