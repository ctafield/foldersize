using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FolderSize.App;

namespace FolderSize.Tests;

public sealed class FileOpeningTests
{
    [Theory]
    [InlineData("msedge.exe")]
    [InlineData("chrome.exe")]
    [InlineData("brave.exe")]
    [InlineData("opera.exe")]
    [InlineData("vivaldi.exe")]
    [InlineData("chromium.exe")]
    [InlineData("firefox.exe")]
    public void FolderSearchUsesTheBrowserDefaultSearchEngineAndKeepsTheQueryInOneArgument(string executable)
    {
        var browser = Path.Combine(@"C:\Program Files\Test Browser", executable);
        ProcessStartInfo? launched = null;
        new FolderWebSearch(() => browser, info => launched = info).Search("Cache_Data & settings + backups");
        Assert.NotNull(launched);
        Assert.Equal(browser, launched.FileName);
        Assert.False(launched.UseShellExecute);
        Assert.Empty(launched.Arguments);
        Assert.Equal(executable == "firefox.exe"
                ? new[] { "-search", "Cache_Data & settings + backups folder" }
                : new[] { "? Cache_Data & settings + backups folder" },
            launched.ArgumentList);
    }

    [Fact]
    public void FolderSearchReportsUnsupportedBrowsersAndLaunchFailuresWithoutSwitchingSearchEngines()
    {
        var unsupported = new FolderWebSearch(() => @"C:\Browser\unsupported.exe",
            _ => Assert.Fail("Unsupported browsers must not be launched."));
        Assert.Throws<NotSupportedException>(() => unsupported.Search("cache"));
        var failing = new FolderWebSearch(() => @"C:\Browser\chrome.exe",
            _ => throw new System.ComponentModel.Win32Exception("Browser launch failed."));
        Assert.Throws<System.ComponentModel.Win32Exception>(() => failing.Search("cache"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FolderSearchMenuUsesOnlyTheRightClickedFolderNameAndWorksWithoutARecursiveScan(bool recursive) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"Cache_Data & settings\child.txt", "child");
        folder.Write("selected.txt", "selected");
        var launches = new List<ProcessStartInfo>();
        var window = new MainWindow(new ThemeSettingsStore(), AppTheme.Light,
            folderWebSearch: new FolderWebSearch(() => @"C:\Browser\msedge.exe", launches.Add));
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            window.Model.Recursive = recursive;
            await window.Model.ScanAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("UsageGrid");
            foreach (var item in window.Model.Rows) grid.SelectedItems.Add(item);
            var clicked = window.Model.Rows.Single(row => row.Node.IsDirectory);
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(clicked));
            var menu = row.ContextMenu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = Assert.IsType<MenuItem>(menu.Items[4]);
            Assert.Equal("Copy full path", Assert.IsType<MenuItem>(menu.Items[3]).Header);
            Assert.Equal("Search Cache_Data & settings on internet", Assert.IsType<TextBlock>(search.Header).Text);
            Assert.True(search.IsEnabled);
            search.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("? Cache_Data & settings folder", Assert.Single(Assert.Single(launches).ArgumentList));
            Assert.Equal(2, grid.SelectedItems.Count);
            Assert.Equal(folder.Root, window.Model.CurrentPath);
            menu.IsOpen = false;
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("run.EXE")]
    [InlineData("run.cmd")]
    [InlineData("run.ps1")]
    [InlineData("run.js")]
    [InlineData("run.msi")]
    [InlineData("run.lnk")]
    [InlineData("run.url")]
    [InlineData("run.scr")]
    public void ExecutablesScriptsInstallersAndShortcutsAreNeverLaunched(string name)
    {
        using var folder = new TestFolder();
        var path = folder.Write(name, "content");
        var opener = new FileOpener(_ => Assert.Fail("Blocked file must not be launched."));
        Assert.Throws<InvalidOperationException>(() => opener.Open(path));
    }

    [Fact]
    public void RenamedExecutablesAreBlockedAndOrdinaryFilesUseShellDefaultAction()
    {
        using var folder = new TestFolder();
        var binary = folder.Write("renamed.txt", "MZnot-a-document");
        var document = folder.Write("document with spaces.txt", "text");
        ProcessStartInfo? launched = null;
        var opener = new FileOpener(info => launched = info);
        Assert.Throws<InvalidOperationException>(() => opener.Open(binary));
        Assert.Null(launched);
        opener.Open(document);
        Assert.NotNull(launched);
        Assert.Equal(document, launched.FileName);
        Assert.True(launched.UseShellExecute);
        Assert.Equal("", launched.Verb);
        Assert.Equal("", launched.Arguments);
        Assert.Throws<FileNotFoundException>(() => opener.Open(Path.Combine(folder.Root, "missing.txt")));
    }

    [Fact]
    public void ExistingThemeSettingsDefaultToEnabledAndThemeChangesPreserveFileOpeningChoice()
    {
        using var folder = new TestFolder();
        var path = folder.Write("settings.json", """{"Theme":"Dark"}""");
        var store = new ThemeSettingsStore(path);
        Assert.True(store.LoadSettings().OpenWithDoubleClick);
        store.Save(store.LoadSettings() with { OpenWithDoubleClick = false });
        store.Save(AppTheme.Light);
        var restored = new ThemeSettingsStore(path).LoadSettings();
        Assert.Equal(AppTheme.Light, restored.Theme);
        Assert.False(restored.OpenWithDoubleClick);
    }

    [Theory]
    [InlineData("UsageGrid", 0)]
    [InlineData("DuplicatesGrid", 1)]
    [InlineData("LargeFilesGrid", 2)]
    public Task ContextMenuTargetsTheClickedRowAndExplicitOpenIgnoresDoubleClickPreference(string name, int page) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("first.txt", "same");
        folder.Write("second.txt", "same");
        var opened = new List<string>();
        var copied = new List<string>();
        var window = new MainWindow(openWithDoubleClick: false,
            settingsStore: new ThemeSettingsStore(Path.Combine(folder.Root, "preferences.json")),
            theme: AppTheme.Light, fileOpener: new FileOpener(info => opened.Add(info.FileName)),
            copyToClipboard: copied.Add);
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await window.Model.FindAsync();
            window.Model.LargeFileSize = "0";
            await window.Model.FindLargeFilesAsync();
            ((TabControl)window.FindName("MainSections")).SelectedIndex = page;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName(name);
            grid.SelectedIndex = 0;
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(1));
            var path = row.Item is UsageRow usage ? usage.FullPath : ((DuplicateRow)row.Item).Path;
            var menu = row.ContextMenu;
            Assert.NotNull(menu);
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(row.Item, grid.SelectedItem);
            Assert.Equal(7, menu.Items.Count);
            Assert.IsType<Separator>(menu.Items[1]);
            Assert.IsType<Separator>(menu.Items[5]);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { "Open", "Copy filename", "Copy full path", "Search folder on internet", "Send to Recycle Bin" },
                items.Select(item => item.Header is TextBlock label ? label.Text : item.Header));
            Assert.False(items[3].IsEnabled);
            Assert.All(items, item =>
            {
                var icon = Assert.IsType<TextBlock>(item.Icon);
                Assert.Contains("Segoe Fluent Icons", icon.FontFamily.Source);
                Assert.False(string.IsNullOrWhiteSpace(icon.Text));
            });
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(path, Assert.Single(opened));
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new[] { Path.GetFileName(path), path }, copied);
            menu.IsOpen = false;
            Assert.True(File.Exists(path));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ContextOpenNavigatesFoldersAndIncompleteFoldersCannotBeRecycled(bool recursive) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"nested\file.txt", "nested");
        var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "preferences.json")), AppTheme.Light,
            fileOpener: new FileOpener(_ => Assert.Fail("Folder Open must not launch a file.")));
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            window.Model.Recursive = recursive;
            await window.Model.ScanAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("UsageGrid");
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
            var node = Assert.IsType<UsageRow>(row.Item).Node;
            var menu = row.ContextMenu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(recursive, items[0].IsEnabled);
            Assert.Equal(recursive, items[^1].IsEnabled);
            if (recursive)
            {
                items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                for (var i = 0; i < 100 && window.Model.Current != node; i++) await Task.Delay(10);
                Assert.Same(node, window.Model.Current);
            }
            else Assert.Equal(folder.Root, window.Model.CurrentPath);
            menu.IsOpen = false;
            Assert.True(File.Exists(Path.Combine(folder.Root, @"nested\file.txt")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SettingAppliesImmediatelyToAllListsAndFailedSaveRevertsChoice() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"scan\a.txt", "same");
        folder.Write(@"scan\b.txt", "same");
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "preferences", "settings.json"));
        store.Save(AppTheme.Light);
        var opened = new List<string>();
        var window = new MainWindow(store, AppTheme.Light, fileOpener: new FileOpener(info => opened.Add(info.FileName)),
            updateChecker: TestUpdates.NoUpdates);
        try
        {
            window.Show();
            window.Model.FolderPath = Path.Combine(folder.Root, "scan");
            await window.Model.ScanAsync();
            await window.Model.FindAsync();
            window.Model.LargeFileSize = "0";
            await window.Model.FindLargeFilesAsync();
            var tabs = (TabControl)ItemsControl.ItemsControlFromItemContainer((TabItem)window.FindName("DuplicatesTab"));
            foreach (var enabled in new[] { true, false })
            {
                if (!enabled)
                {
                    ((ListBoxItem)window.FindName("SettingsNavigationItem")).IsSelected = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var view = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
                    var box = (CheckBox)view.FindName("OpenWithDoubleClickBox");
                    Assert.True(box.IsChecked);
                    using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        box.IsChecked = false;
                    Assert.True(box.IsChecked);
                    Assert.Contains("Could not save", ((TextBlock)view.FindName("SaveStatus")).Text);
                    box.IsChecked = false;
                    Assert.False(store.LoadSettings().OpenWithDoubleClick);
                }
                opened.Clear();
                var names = new[] { "UsageGrid", "DuplicatesGrid", "LargeFilesGrid" };
                for (var index = 0; index < names.Length; index++)
                {
                    tabs.SelectedIndex = index;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var grid = (DataGrid)window.FindName(names[index]);
                    var column = grid.Columns.First(item => item is not DataGridCheckBoxColumn);
                    var content = column.GetCellContent(grid.Items[0]);
                    Assert.NotNull(content);
                    grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent, Source = content
                    });
                    foreach (var check in grid.Columns.OfType<DataGridCheckBoxColumn>())
                    {
                        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        {
                            RoutedEvent = Control.MouseDoubleClickEvent, Source = check.GetCellContent(grid.Items[0])
                        });
                    }
                }
                Assert.Equal(enabled ? 3 : 0, opened.Count);
                Assert.All(opened, path => Assert.EndsWith(".txt", path));
            }
        }
        finally { window.Close(); }
    });
}
