using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class EmptyFolderTests
{
    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task PermanentModeRemovesScannedFolderContentsButPreservesUnselectedPaths() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var tree = Path.Combine(folder.Root, "tree");
        folder.Write(@"tree\child\file.txt", "contents");
        var retained = folder.Write(@"retained\zero.txt", "");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        var node = model.Rows.Single(row => row.FullPath == tree).Node;
        await model.RecycleItemsAsync([node, node.Children.Single(), node], permanentlyDelete: true);
        Assert.Empty(model.Issues);
        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(retained));
        Assert.True(Directory.Exists(folder.Root));
        Assert.Contains("1 files permanently deleted", model.Status);
    });

    [Theory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("missing-child")]
    public void PermanentDeletionRefusesChangedTreesBeforeRemovingAnyDescendants(string change)
    {
        using var folder = new TestFolder();
        var tree = Path.Combine(folder.Root, "tree");
        var child = Directory.CreateDirectory(Path.Combine(tree, "child")).FullName;
        var node = Assert.Single(EmptyFolderFinder.Find(new FolderScanner().Scan(folder.Root, true))).Node;
        if (change == "file") folder.Write(@"tree\zero.txt", "");
        else if (change == "folder") Directory.CreateDirectory(Path.Combine(tree, "new-child"));
        else Directory.Delete(child);
        var result = new FolderRecycling(new WindowsFileAccess(), new ShellRecycler(allowDirectories: true, permanentlyDelete: true))
            .Recycle(node);
        Assert.Empty(result.Recycled);
        Assert.Contains("changed", Assert.Single(result.Issues).Message);
        Assert.True(Directory.Exists(tree));
        if (change != "missing-child") Assert.True(Directory.Exists(child));
        if (change == "file") Assert.True(File.Exists(Path.Combine(tree, "zero.txt")));
    }

    [Fact]
    public void PermanentDeletionRefusesUnscannedFoldersAndHonoursCancellation()
    {
        using var folder = new TestFolder();
        var tree = Directory.CreateDirectory(Path.Combine(folder.Root, @"tree\child")).Parent!.FullName;
        var shallow = new FolderScanner().Scan(folder.Root, false);
        var service = new FolderRecycling(new WindowsFileAccess(), new ShellRecycler(allowDirectories: true, permanentlyDelete: true));
        Assert.Throws<ArgumentException>(() => service.Recycle(shallow.Root));
        var result = service.Recycle(Assert.Single(shallow.Root.Children));
        Assert.Empty(result.Recycled);
        Assert.Contains("recursively", Assert.Single(result.Issues).Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scanned = new FolderScanner().Scan(folder.Root, true);
        result = service.Recycle(Assert.Single(scanned.Root.Children), cancellation.Token);
        Assert.Empty(result.Recycled);
        Assert.Contains("cancelled", Assert.Single(result.Issues).Message);
        Assert.True(Directory.Exists(Path.Combine(tree, "child")));
    }

    [Fact]
    public void FindsHighestEmptyTreesAndCountsEveryDescendantWithoutTreatingZeroBytesAsEmpty()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, @"tree\a\b"));
        Directory.CreateDirectory(Path.Combine(folder.Root, @"tree\c"));
        Directory.CreateDirectory(Path.Combine(folder.Root, "leaf"));
        Directory.CreateDirectory(Path.Combine(folder.Root, @"occupied\empty"));
        Directory.CreateDirectory(Path.Combine(folder.Root, @"zero\empty"));
        folder.Write(@"occupied\file.txt", "contents");
        folder.Write(@"zero\file.txt", "");
        var hidden = folder.Write(@"hidden\file.txt", "");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var matches = EmptyFolderFinder.Find(new FolderScanner().Scan(folder.Root, true));
        Assert.Equal(new[] { "leaf", @"occupied\empty", "tree", @"zero\empty" },
            matches.Select(match => Path.GetRelativePath(folder.Root, match.Node.FullPath)));
        Assert.Equal(3, Assert.Single(matches, match => match.Node.Name == "tree").EmptySubfolderCount);
        Assert.All(matches.Where(match => match.Node.Name != "tree"), match => Assert.Equal(0, match.EmptySubfolderCount));
    }

    [Fact]
    public void ScanRootUnscannedTreesAndCancellationNeverProduceMatches()
    {
        using var folder = new TestFolder();
        Assert.Empty(EmptyFolderFinder.Find(new FolderScanner().Scan(folder.Root, true)));
        Directory.CreateDirectory(Path.Combine(folder.Root, @"tree\child"));
        Assert.Empty(EmptyFolderFinder.Find(new FolderScanner().Scan(folder.Root, false)));
        var scan = new FolderScanner().Scan(folder.Root, true);
        Assert.NotSame(scan.Root, Assert.Single(EmptyFolderFinder.Find(scan)).Node);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EmptyFolderFinder.Find(scan, cancellation.Token));
    }

    [Fact]
    public void IssueTaintedTreesAreExcludedWhileSafeEmptyBranchesStillQualify()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, @"unsafe\safe"));
        Directory.CreateDirectory(Path.Combine(folder.Root, "good"));
        folder.Write(@"target\file.txt", "contents");
        var link = Path.Combine(folder.Root, @"unsafe\link");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{Path.Combine(folder.Root, "target")}\"",
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not create the test junction.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var scan = new FolderScanner().Scan(folder.Root, true);
            Assert.True(scan.Root.HasErrors);
            Assert.Equal(new[] { "good", @"unsafe\safe" }, EmptyFolderFinder.Find(scan)
                .Select(match => Path.GetRelativePath(folder.Root, match.Node.FullPath)));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void PermanentDeletionRefusesFoldersReplacedByJunctions()
    {
        using var folder = new TestFolder();
        var path = Directory.CreateDirectory(Path.Combine(folder.Root, "empty")).FullName;
        var target = folder.Write(@"target\retained.txt", "retained");
        var node = Assert.Single(EmptyFolderFinder.Find(new FolderScanner().Scan(folder.Root, true))).Node;
        Directory.Delete(path);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{path}\" \"{Path.GetDirectoryName(target)}\"",
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not create the test junction.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = new FolderRecycling(new WindowsFileAccess(), new ShellRecycler(allowDirectories: true, permanentlyDelete: true))
                .Recycle(node);
            Assert.Empty(result.Recycled);
            Assert.Contains("junction", Assert.Single(result.Issues).Message);
            Assert.True(File.Exists(target));
            Assert.True(Directory.Exists(path));
        }
        finally { Directory.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SearchUsesTheCompletedScanAndInvalidatesSelectionsWhenResultsChange(bool recursive) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, @"tree\child"));
        var model = new MainViewModel { FolderPath = folder.Root, Recursive = recursive };
        Assert.False(model.CanFindEmptyFolders);
        await model.ScanAsync();
        model.Recursive = !recursive;
        model.FolderPath = "not the scanned path";
        Assert.Equal(recursive, model.CanFindEmptyFolders);
        await model.FindEmptyFoldersAsync();
        if (!recursive)
        {
            Assert.Empty(model.EmptyFolders);
            Assert.Contains("scan again", model.EmptyFoldersSummary);
            return;
        }
        var row = Assert.Single(model.EmptyFolders);
        Assert.Equal(Path.Combine(folder.Root, "tree"), row.FullPath);
        Assert.Equal(1, row.EmptySubfolderCount);
        Assert.False(row.Selected);
        row.Selected = true;
        Assert.True(model.CanRecycleEmptyFolders);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Busy) && model.Busy) model.Cancel();
        };
        await model.FindEmptyFoldersAsync();
        Assert.Empty(model.EmptyFolders);
        Assert.Contains("cancelled", model.EmptyFoldersSummary);
        Assert.False(model.CanRecycleEmptyFolders);
        Assert.False(model.Busy);
        row.Selected = false;
        row.Selected = true;
        Assert.False(model.CanRecycleEmptyFolders);
    });

    [Fact]
    public void UnreadableFoldersAreNotMistakenForEmptyFolders()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "good"));
        var denied = Directory.CreateDirectory(Path.Combine(folder.Root, "denied"));
        var security = System.IO.FileSystemAclExtensions.GetAccessControl(denied);
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(rule);
        System.IO.FileSystemAclExtensions.SetAccessControl(denied, security);
        try
        {
            var scan = new FolderScanner().Scan(folder.Root, true);
            Assert.Contains(scan.Issues, issue => issue.Path == denied.FullName);
            Assert.Equal("good", Assert.Single(EmptyFolderFinder.Find(scan)).Node.Name);
        }
        finally
        {
            security.RemoveAccessRuleSpecific(rule);
            System.IO.FileSystemAclExtensions.SetAccessControl(denied, security);
        }
    }

    [Theory]
    [InlineData(1240, AppTheme.Light)]
    [InlineData(820, AppTheme.Dark)]
    public Task EmptyFoldersViewShowsCountsAndNavigatesWithoutScanning(double width, AppTheme theme) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, @"tree\child\grandchild"));
        var window = new MainWindow(new ThemeSettingsStore(), theme, openWithDoubleClick: false)
        { Width = width, Height = 640 };
        try
        {
            window.Show();
            var tab = (TabItem)window.FindName("EmptyFoldersTab");
            var sections = (TabControl)window.FindName("MainSections");
            Assert.Equal(sections.Items.IndexOf(window.FindName("LargeFilesTab")) + 1, sections.Items.IndexOf(tab));
            var navigation = (ListBox)window.FindName("MainNavigation");
            navigation.SelectedIndex = sections.Items.IndexOf(tab);
            var find = (Button)window.FindName("FindEmptyFoldersButton");
            Assert.False(find.IsEnabled);
            Assert.False(((Button)window.FindName("SelectAllEmptyFoldersButton")).IsEnabled);
            Assert.False(((Button)window.FindName("InvertEmptyFoldersSelectionButton")).IsEnabled);
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            Assert.True(find.IsEnabled);
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 200 && window.Model.Busy; i++) await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("EmptyFoldersGrid");
            var row = Assert.Single(window.Model.EmptyFolders);
            Assert.Single(grid.Items);
            Assert.False(((Button)window.FindName("RecycleEmptyFoldersButton")).IsEnabled);
            var counts = grid.Columns.Single(column => Equals(column.Header, "Empty subfolders"));
            Assert.Equal("2", Assert.IsType<TextBlock>(counts.GetCellContent(row)).Text);
            var toolbar = Assert.IsType<WrapPanel>(find.Parent);
            var recycle = (Button)window.FindName("RecycleEmptyFoldersButton");
            var recycleBounds = recycle.TransformToAncestor(toolbar).TransformBounds(new Rect(recycle.RenderSize));
            Assert.All(toolbar.Children.OfType<Button>().Where(button => button != recycle), button =>
            {
                var buttonBounds = button.TransformToAncestor(toolbar).TransformBounds(new Rect(button.RenderSize));
                Assert.True(buttonBounds.Right <= recycleBounds.Left);
            });
            var bounds = grid.TransformToAncestor(window).TransformBounds(new Rect(grid.RenderSize));
            Assert.True(bounds.Right <= window.ActualWidth);
            Assert.True(bounds.Bottom <= window.ActualHeight);
            row.Selected = true;
            Assert.True(((Button)window.FindName("RecycleEmptyFoldersButton")).IsEnabled);
            grid.SelectedItem = row;
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
            for (var i = 0; i < 200 && window.Model.CurrentPath != row.FullPath; i++) await Task.Delay(10);
            Assert.Equal(row.FullPath, window.Model.CurrentPath);
            Assert.Equal(folder.Root, window.Model.FolderPath);
            Assert.Equal(0, sections.SelectedIndex);
            Assert.Same(row, Assert.Single(window.Model.EmptyFolders));
            Assert.False(window.Model.Busy);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfirmationIncludesDescendantsAndCancelChangesNothing(bool permanentlyDelete) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, @"tree\child");
        Directory.CreateDirectory(path);
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await window.Model.FindEmptyFoldersAsync();
            ((TabItem)window.FindName("EmptyFoldersTab")).IsSelected = true;
            window.Model.EmptyFolders[0].Selected = true;
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Window? dialog = null;
                try
                {
                    dialog = Assert.Single(window.OwnedWindows.Cast<Window>());
                    Assert.Equal("Confirm recycling", dialog.Title);
                    var panel = Assert.IsType<DockPanel>(dialog.Content);
                    var checkbox = Assert.Single(panel.Children.OfType<CheckBox>());
                    Assert.Equal("Permanently Delete", checkbox.Content);
                    Assert.False(checkbox.IsChecked);
                    checkbox.IsChecked = permanentlyDelete;
                    var heading = panel.Children.OfType<TextBlock>().Single().Text;
                    Assert.Contains("1 empty subfolder", heading);
                    Assert.Contains("empty descendants", heading);
                    if (permanentlyDelete)
                    {
                        Assert.Contains("PERMANENTLY DELETED, not recycled", heading);
                        Assert.Contains("cannot be undone", heading);
                    }
                    Assert.Equal(Path.Combine(folder.Root, "tree"),
                        Assert.Single(panel.Children.OfType<ListBox>().Single().Items.Cast<string>()));
                    var cancel = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                        .Single(button => button.Name == "CancelRecycleButton");
                    Assert.True(cancel.IsDefault);
                    var confirm = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                        .Single(button => button != cancel);
                    Assert.False(confirm.IsDefault);
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    handled.SetResult();
                }
                catch (Exception ex) { handled.SetException(ex); }
                finally { dialog?.Close(); }
            }));
            ((Button)window.FindName("RecycleEmptyFoldersButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await handled.Task;
            Assert.True(Directory.Exists(path));
            Assert.True(window.Model.EmptyFolders[0].Selected);
            Assert.False(window.Model.Busy);
        }
        finally { window.Close(); }
    });

    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task ConfirmedPermanentDeletionRechecksTreesAndRefreshesOriginalScopeAfterPartialFailure() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var good = Directory.CreateDirectory(Path.Combine(folder.Root, @"good\child")).Parent!.FullName;
        var changed = Directory.CreateDirectory(Path.Combine(folder.Root, "changed")).FullName;
        var retained = Directory.CreateDirectory(Path.Combine(folder.Root, "retained")).FullName;
        var window = new MainWindow();
        try
        {
            window.Show();
            var model = window.Model;
            model.FolderPath = folder.Root;
            await model.ScanAsync();
            await model.FindEmptyFoldersAsync();
            foreach (var row in model.EmptyFolders) row.Selected = row.Name != "retained";
            var added = folder.Write(@"changed\zero.txt", "");
            model.FolderPath = "not the scanned path";
            model.Recursive = false;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.Status) &&
                    model.Status.StartsWith("Permanent deletion finished:", StringComparison.Ordinal))
                    finished.TrySetResult();
            };
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Window? dialog = null;
                try
                {
                    dialog = Assert.Single(window.OwnedWindows.Cast<Window>());
                    var panel = Assert.IsType<DockPanel>(dialog.Content);
                    Assert.Single(panel.Children.OfType<CheckBox>()).IsChecked = true;
                    Assert.Equal(new[] { changed, good },
                        panel.Children.OfType<ListBox>().Single().Items.Cast<string>());
                    panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                        .Single(button => button.Name == "ConfirmRecycleButton")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally { dialog?.Close(); }
            }));
            ((Button)window.FindName("RecycleEmptyFoldersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(Directory.Exists(good));
            Assert.True(File.Exists(added));
            Assert.True(Directory.Exists(retained));
            Assert.Equal(retained, Assert.Single(model.EmptyFolders).FullPath);
            Assert.False(model.EmptyFolders[0].Selected);
            Assert.False(model.CanRecycleEmptyFolders);
            Assert.Equal(folder.Root, model.FolderPath);
            Assert.True(model.Recursive);
            Assert.Contains(model.Issues, issue => issue.Path == changed && issue.Message.Contains("changed"));
            Assert.Contains("1 folder permanently deleted", model.Status);
            Assert.Contains("Results refreshed", model.Status);
            Assert.False(model.Busy);
        }
        finally { window.Close(); }
    });

    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task RecyclingEmptyTreesRechecksAddedZeroByteFilesAndRefreshesTheOriginalScope() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var good = Path.Combine(folder.Root, @"good\child\grandchild");
        var changed = Path.Combine(folder.Root, "changed");
        var retained = Path.Combine(folder.Root, "retained");
        Directory.CreateDirectory(good);
        Directory.CreateDirectory(changed);
        Directory.CreateDirectory(retained);
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        await model.FindEmptyFoldersAsync();
        foreach (var row in model.EmptyFolders) row.Selected = row.Name != "retained";
        folder.Write(@"changed\zero.txt", "");
        model.FolderPath = "not the scanned path";
        model.Recursive = false;
        await model.RecycleEmptyFoldersAsync();
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "good")));
        Assert.True(File.Exists(Path.Combine(changed, "zero.txt")));
        Assert.True(Directory.Exists(retained));
        Assert.Equal(retained, Assert.Single(model.EmptyFolders).FullPath);
        Assert.False(model.EmptyFolders[0].Selected);
        Assert.False(model.CanRecycleEmptyFolders);
        Assert.Equal(folder.Root, model.FolderPath);
        Assert.True(model.Recursive);
        Assert.Contains(model.Issues, issue => issue.Path == changed && issue.Message.Contains("changed"));
        Assert.Contains("Results refreshed", model.Status);
    });

    [Fact]
    public Task ExplorerMenuOpensOnlyTheRightClickedFolderWithoutChangingRecyclingSelections() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "first"));
        var target = Directory.CreateDirectory(Path.Combine(folder.Root, "folder & details, empty.exe")).FullName;
        var launches = new List<ProcessStartInfo>();
        var window = new MainWindow(new ThemeSettingsStore(), AppTheme.Light, openWithDoubleClick: false,
            fileOpener: new FileOpener(launches.Add));
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await window.Model.FindEmptyFoldersAsync();
            var tab = (TabItem)window.FindName("EmptyFoldersTab");
            tab.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("EmptyFoldersGrid");
            var clicked = window.Model.EmptyFolders.Single(row => row.FullPath == target);
            var other = window.Model.EmptyFolders.Single(row => row != clicked);
            other.Selected = true;
            grid.SelectedItem = other;
            var selections = window.Model.EmptyFolders.Select(row => row.Selected).ToArray();
            var container = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(clicked));
            var menu = container.ContextMenu;
            Assert.NotNull(menu);
            menu.PlacementTarget = container;
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(clicked, grid.SelectedItem);
            var action = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal("Open in Explorer", action.Header);
            Assert.IsType<TextBlock>(action.Icon);
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            var launch = Assert.Single(launches);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                launch.FileName);
            Assert.False(launch.UseShellExecute);
            Assert.Equal(target, Assert.Single(launch.ArgumentList));
            Assert.Equal(selections, window.Model.EmptyFolders.Select(row => row.Selected));
            Assert.True(tab.IsSelected);
            Assert.Equal(folder.Root, window.Model.CurrentPath);
            Assert.False(window.Model.Busy);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ExplorerOpeningRejectsMissingFoldersAndFilesAndSurfacesLaunchFailures()
    {
        using var folder = new TestFolder();
        var missing = Path.Combine(folder.Root, "missing");
        var file = folder.Write("file.txt", "contents");
        var opener = new FileOpener(_ => Assert.Fail("Invalid folders must not launch Explorer."));
        Assert.ThrowsAny<IOException>(() => opener.OpenFolderInExplorer(missing));
        Assert.Throws<InvalidOperationException>(() => opener.OpenFolderInExplorer(file));
        var failure = new FileOpener(_ => throw new Win32Exception("Explorer launch failed"));
        Assert.Throws<Win32Exception>(() => failure.OpenFolderInExplorer(folder.Root));
    }

    [Fact]
    public Task EmptyFolderBulkSelectionIncludesOffscreenRowsAndKeepsTheListVirtualized() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        for (var i = 0; i < 300; i++) Directory.CreateDirectory(Path.Combine(folder.Root, i.ToString("D3")));
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await window.Model.FindEmptyFoldersAsync();
            ((TabItem)window.FindName("EmptyFoldersTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("EmptyFoldersGrid");
            Assert.Equal(300, grid.Items.Count);
            Assert.InRange(CountRows(grid), 1, 200);
            var selectAll = (Button)window.FindName("SelectAllEmptyFoldersButton");
            var invert = (Button)window.FindName("InvertEmptyFoldersSelectionButton");
            Assert.True(selectAll.IsEnabled);
            Assert.True(invert.IsEnabled);
            Assert.Null(grid.ItemContainerGenerator.ContainerFromItem(window.Model.EmptyFolders[^1]));
            window.Model.EmptyFolders[0].Selected = true;
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.Model.EmptyFolders[0].Selected);
            Assert.All(window.Model.EmptyFolders.Skip(1), row => Assert.True(row.Selected));
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(window.Model.EmptyFolders, row => Assert.True(row.Selected));
            Assert.True(window.Model.CanRecycleEmptyFolders);
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(window.Model.EmptyFolders, row => Assert.False(row.Selected));
            Assert.False(window.Model.CanRecycleEmptyFolders);
            var unchangedDuringOperation = true;
            window.Model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(MainViewModel.Busy) || !window.Model.Busy) return;
                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                unchangedDuringOperation &= window.Model.EmptyFolders.All(row => !row.Selected);
                invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                unchangedDuringOperation &= window.Model.EmptyFolders.All(row => !row.Selected);
            };
            await window.Model.FindLargeFilesAsync();
            Assert.True(unchangedDuringOperation);
            var last = window.Model.EmptyFolders[^1];
            grid.ScrollIntoView(last);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.NotNull(grid.ItemContainerGenerator.ContainerFromItem(last));
            Assert.InRange(CountRows(grid), 1, 200);
        }
        finally { window.Close(); }
    });

    private static int CountRows(DependencyObject root)
    {
        var rows = root is DataGridRow ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            rows += CountRows(VisualTreeHelper.GetChild(root, i));
        return rows;
    }
}
