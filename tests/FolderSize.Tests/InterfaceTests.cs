using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class InterfaceTests
{
    [Theory]
    [InlineData(1240, 216)]
    [InlineData(820, 56)]
    public Task LeftNavigationSwitchesExistingViewsAndFitsTheWindow(double width, double paneWidth) => Sta.Run(async () =>
    {
        var window = new MainWindow { Width = width };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var navigation = (ListBox)window.FindName("MainNavigation");
            var sections = (TabControl)window.FindName("MainSections");
            Assert.Equal(0, navigation.SelectedIndex);
            Assert.Equal(paneWidth, ((Border)window.FindName("NavigationPane")).ActualWidth);
            var names = new[] { "UsageGrid", "DuplicatesGrid", "LargeFilesGrid", "RenamePanel" };
            for (var index = 0; index < sections.Items.Count; index++)
            {
                navigation.SelectedIndex = index;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(index, sections.SelectedIndex);
                if (index < names.Length)
                {
                    var view = (FrameworkElement)window.FindName(names[index]);
                    Assert.True(view.IsVisible);
                    Assert.True(view.ActualWidth > 100);
                    Assert.True(view.ActualHeight > 40);
                    var bounds = view.TransformToAncestor(window).TransformBounds(new Rect(view.RenderSize));
                    Assert.True(bounds.Right <= window.ActualWidth, $"{names[index]} extends beyond the window.");
                }
            }
            ((TabItem)window.FindName("DuplicatesTab")).IsSelected = true;
            Assert.Equal(1, navigation.SelectedIndex);
            navigation.SelectedIndex = 2;
            ((TextBox)window.FindName("LargeFileSizeBox")).Text = "256";
            navigation.SelectedIndex = 0;
            navigation.SelectedIndex = 2;
            Assert.Equal("256", ((TextBox)window.FindName("LargeFileSizeBox")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task StartupScanningPreferencePersistsAndFailedSaveRevertsChoice() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json"));
        store.Save(new AppSettings { Theme = AppTheme.Light, OpenWithDoubleClick = false, IncludeSubfolders = false });
        foreach (var enabled in new[] { false, true })
        {
            var preferences = store.LoadSettings();
            var window = new MainWindow(store, preferences.Theme, preferences.OpenWithDoubleClick,
                includeSubfolders: preferences.IncludeSubfolders, startScanningOnOpen: preferences.StartScanningOnOpen);
            try
            {
                window.Show();
                var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    SettingsWindow? dialog = null;
                    try
                    {
                        dialog = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows.Cast<Window>()));
                        var box = (CheckBox)dialog.FindName("StartScanningOnOpenBox");
                        Assert.Equal(!enabled, box.IsChecked);
                        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            box.IsChecked = enabled;
                        Assert.Equal(!enabled, box.IsChecked);
                        Assert.Contains("Could not save", ((TextBlock)dialog.FindName("SaveStatus")).Text);
                        Assert.Equal(!enabled, store.LoadSettings().StartScanningOnOpen);
                        box.IsChecked = enabled;
                        ((ComboBox)dialog.FindName("ThemeBox")).SelectedItem = AppTheme.Dark;
                        handled.SetResult();
                    }
                    catch (Exception ex) { handled.SetException(ex); }
                    finally { dialog?.Close(); }
                }));
                ((MenuItem)window.FindName("AppearanceMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await handled.Task;
                var recursive = (CheckBox)window.FindName("IncludeSubfoldersBox");
                recursive.IsChecked = true;
                recursive.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var restored = new ThemeSettingsStore(store.FilePath).LoadSettings();
                Assert.Equal(enabled, restored.StartScanningOnOpen);
                Assert.Equal(AppTheme.Dark, restored.Theme);
                Assert.False(restored.OpenWithDoubleClick);
                Assert.True(restored.IncludeSubfolders);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task IncludeSubfoldersPersistsAndRestoresWithoutChangingOtherPreferences() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json"));
        store.Save(new AppSettings { Theme = AppTheme.Light, OpenWithDoubleClick = false });
        foreach (var enabled in new[] { false, true })
        {
            var preferences = store.LoadSettings();
            var window = new MainWindow(store, preferences.Theme, preferences.OpenWithDoubleClick,
                includeSubfolders: preferences.IncludeSubfolders);
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var checkbox = (CheckBox)window.FindName("IncludeSubfoldersBox");
                Assert.Equal(!enabled, checkbox.IsChecked);
                Assert.Equal(!enabled, window.Model.Recursive);
                checkbox.IsChecked = enabled;
                checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(enabled, window.Model.Recursive);
                var restored = new ThemeSettingsStore(store.FilePath).LoadSettings();
                Assert.Equal(enabled, restored.IncludeSubfolders);
                Assert.Equal(AppTheme.Light, restored.Theme);
                Assert.False(restored.OpenWithDoubleClick);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task SpaceMapTabsDefaultToFoldersAndUpdateWithNavigation() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("root.txt", "12345");
        folder.Write(@"nested\photo.png", "123");
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var tabs = (TabControl)window.FindName("SpaceMapTabs");
            Assert.Equal(0, tabs.SelectedIndex);
            var extensionMap = (TreemapView)window.FindName("ExtensionMap");
            var kindMap = (TreemapView)window.FindName("KindMap");
            foreach (var index in new[] { 1, 2 })
            {
                tabs.SelectedIndex = index;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var map = index == 1 ? extensionMap : kindMap;
                Assert.NotNull(map.Items);
                Assert.Equal(8, map.Items.Sum(item => item.Size));
                Assert.True(map.ActualHeight > 40);
                var bitmap = new RenderTargetBitmap((int)map.ActualWidth, (int)map.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(map);
            }
            await window.Model.NavigateAsync(Assert.Single(window.Model.Rows, row => row.Kind == "Folder").Node);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(3, Assert.Single(extensionMap.Items!).Size);
            Assert.Equal(".png", Assert.Single(extensionMap.Items!).Label);
            Assert.Equal("Images", Assert.Single(kindMap.Items!).Label);
            await window.Model.UpAsync();
            Assert.Equal(8, window.Model.MapGroups.Kinds.Sum(item => item.Size));
            window.Model.FolderPath = System.IO.Path.Combine(folder.Root, "missing");
            await window.Model.ScanAsync();
            Assert.Empty(window.Model.MapGroups.Extensions);
            Assert.Empty(window.Model.MapGroups.Kinds);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SettingsMenuAppliesThemeToBothWindowsAndRestoresItAfterReopening() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "settings.json");
        var store = new ThemeSettingsStore(path);
        store.Save(AppTheme.Light);
        var window = new MainWindow(store, store.Load());
        try
        {
            window.Show();
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                SettingsWindow? dialog = null;
                try
                {
                    dialog = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows.Cast<Window>()));
                    var box = (ComboBox)dialog.FindName("ThemeBox");
                    Assert.Equal(AppTheme.Light, box.SelectedItem);
                    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        box.SelectedItem = AppTheme.Dark;
                    Assert.Equal(AppTheme.Light, box.SelectedItem);
                    Assert.Contains("Could not save", ((TextBlock)dialog.FindName("SaveStatus")).Text);
                    Assert.True(Assert.IsType<SolidColorBrush>(window.Background).Color.R >= 128);
                    Assert.Equal(AppTheme.Light, new ThemeSettingsStore(path).Load());

                    box.SelectedItem = AppTheme.Dark;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(Assert.IsType<SolidColorBrush>(window.Background).Color.R < 128);
                    Assert.True(Assert.IsType<SolidColorBrush>(dialog.Background).Color.R < 128);
                    Assert.Equal(AppTheme.Dark, new ThemeSettingsStore(path).Load());
                    handled.SetResult();
                }
                catch (Exception ex) { handled.SetException(ex); }
                finally { dialog?.Close(); }
            }));
            ((MenuItem)window.FindName("AppearanceMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await handled.Task;
        }
        finally { window.Close(); }

        var restoredStore = new ThemeSettingsStore(path);
        var reopened = new MainWindow(restoredStore, restoredStore.Load());
        try
        {
            reopened.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Assert.IsType<SolidColorBrush>(reopened.Background).Color.R < 128);
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                SettingsWindow? dialog = null;
                try
                {
                    dialog = Assert.IsType<SettingsWindow>(Assert.Single(reopened.OwnedWindows.Cast<Window>()));
                    var box = (ComboBox)dialog.FindName("ThemeBox");
                    Assert.Equal(AppTheme.Dark, box.SelectedItem);
                    box.SelectedItem = AppTheme.Light;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(Assert.IsType<SolidColorBrush>(reopened.Background).Color.R >= 128);
                    Assert.True(Assert.IsType<SolidColorBrush>(dialog.Background).Color.R >= 128);
                    box.SelectedItem = AppTheme.System;
                    Assert.Equal(AppTheme.System, restoredStore.Load());
                    handled.SetResult();
                }
                catch (Exception ex) { handled.SetException(ex); }
                finally { dialog?.Close(); }
            }));
            ((MenuItem)reopened.FindName("AppearanceMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await handled.Task;
        }
        finally { reopened.Close(); }
    });

    [Theory]
    [InlineData("Light", 1240, 820)]
    [InlineData("Dark", 1240, 820)]
    [InlineData("Light", 820, 640)]
    [InlineData("Dark", 820, 640)]
    public Task FluentThemesKeepContentVisible(string theme, double width, double height) => Sta.Run(async () =>
    {
        var window = (MainWindow)XamlReader.Parse($"""
            <local:MainWindow xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:local="clr-namespace:FolderSize.App;assembly=FolderSize"
                              ThemeMode="{theme}" Width="{width}" Height="{height}"/>
            """);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var background = Assert.IsType<SolidColorBrush>(window.Background).Color;
            Assert.Equal(theme == "Dark", background.R < 128);
            var secondary = Assert.IsType<SolidColorBrush>(window.FindResource("SecondaryTextBrush")).Color;
            Assert.True(secondary.A > 0);
            var map = (TreemapView)window.FindName("Map");
            Assert.True(map.ActualWidth >= 100);
            Assert.True(map.ActualHeight >= 40, $"Map height: {map.ActualHeight}");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var duplicatesTab = (TabItem)window.FindName("DuplicatesTab");
            duplicatesTab.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var invert = (Button)window.FindName("InvertSelectionButton");
            var panel = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var bounds = invert.TransformToAncestor(panel).TransformBounds(new Rect(invert.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= panel.ActualWidth);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= panel.ActualHeight);
            var grid = (DataGrid)window.FindName("DuplicatesGrid");
            Assert.True(grid.ActualHeight >= 60, $"Duplicate grid height: {grid.ActualHeight}");
            bitmap.Render(window);
            ((TabItem)window.FindName("LargeFilesTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var largeFilesGrid = (DataGrid)window.FindName("LargeFilesGrid");
            Assert.True(largeFilesGrid.ActualHeight >= 60, $"Large files grid height: {largeFilesGrid.ActualHeight}");
            var recycleLargeFiles = (Button)window.FindName("RecycleLargeFilesButton");
            var recycleBounds = recycleLargeFiles.TransformToAncestor(panel).TransformBounds(new Rect(recycleLargeFiles.RenderSize));
            Assert.True(recycleBounds.Left >= 0 && recycleBounds.Right <= panel.ActualWidth);
            Assert.True(recycleBounds.Top >= 0 && recycleBounds.Bottom <= panel.ActualHeight);
            bitmap.Render(window);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ScanSortSelectNavigateResizeAndFindDuplicates() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("a", "123");
        folder.Write(@"nested\b", "123");
        folder.Write("large", "123456789");
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(window.Model.CanFind);
            Assert.Equal(3, window.Model.Rows.Count);
            var grid = (DataGrid)window.FindName("UsageGrid");
            var map = (TreemapView)window.FindName("Map");
            Assert.Equal(folder.Root, map.Node?.FullPath);
            var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            view.SortDescriptions.Add(new(nameof(UsageRow.Bytes), ListSortDirection.Ascending));
            Assert.Equal(3, ((UsageRow)view.Cast<object>().First()).Bytes);
            var nested = Assert.Single(window.Model.Rows, row => row.Kind == "Folder");
            grid.SelectedItem = nested;
            Assert.Equal(nested.Node, map.SelectedNode);
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
            for (var i = 0; i < 100 && window.Model.CurrentPath != nested.FullPath; i++) await Task.Delay(10);
            Assert.Equal(nested.FullPath, window.Model.CurrentPath);
            Assert.Single(window.Model.Rows);
            await window.Model.UpAsync();
            Assert.Equal(folder.Root, window.Model.CurrentPath);
            window.Width = 920;
            window.Height = 650;
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)map.ActualWidth, (int)map.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(map);
            Assert.True(bitmap.PixelWidth > 0);
            await window.Model.FindAsync();
            Assert.Equal(2, window.Model.DuplicateRows.Count);
            Assert.False(window.Model.DuplicateRows[0].Selected);
            Assert.True(window.Model.DuplicateRows[1].Selected);
            Assert.True(window.Model.CanRecycle);
            Assert.True(window.Model.KeepsOneCopy());
            window.Model.DuplicateRows[0].Selected = true;
            Assert.False(window.Model.KeepsOneCopy());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DefaultSelectionAndInvertApplyToEveryGroup() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        folder.Write("c", "same");
        folder.Write("d", "other");
        folder.Write("e", "other");
        folder.Write("unique", "no duplicates");
        var window = new MainWindow();
        try
        {
            window.Show();
            var invert = (Button)window.FindName("InvertSelectionButton");
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.Model.CanRecycle);
            window.Model.FolderPath = folder.Root;
            await window.Model.ScanAsync();
            await window.Model.FindAsync();
            Assert.Equal(5, window.Model.DuplicateRows.Count);
            Assert.Equal(2, window.Model.DuplicateRows.Select(row => row.GroupNumber).Distinct().Count());
            foreach (var group in window.Model.DuplicateRows.GroupBy(row => row.GroupNumber))
            {
                Assert.False(group.First().Selected);
                Assert.All(group.Skip(1), row => Assert.True(row.Selected));
            }
            Assert.Equal(3, window.Model.SelectedFiles().Count);
            Assert.True(window.Model.CanRecycle);
            Assert.True(window.Model.KeepsOneCopy());
            var initial = window.Model.DuplicateRows.Select(row => row.Selected).ToArray();
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial.Select(selected => !selected), window.Model.DuplicateRows.Select(row => row.Selected));
            Assert.Equal(2, window.Model.SelectedFiles().Count);
            Assert.True(window.Model.KeepsOneCopy());
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, window.Model.DuplicateRows.Select(row => row.Selected));
            foreach (var row in window.Model.DuplicateRows) row.Selected = false;
            Assert.False(window.Model.CanRecycle);
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(window.Model.DuplicateRows, row => Assert.True(row.Selected));
            Assert.True(window.Model.CanRecycle);
            Assert.False(window.Model.KeepsOneCopy());
            invert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.Model.CanRecycle);
            await window.Model.FindAsync();
            Assert.Equal(initial, window.Model.DuplicateRows.Select(row => row.Selected));
            Assert.True(window.Model.CanRecycle);
            await window.Model.ScanAsync();
            Assert.Empty(window.Model.DuplicateRows);
            Assert.False(window.Model.CanRecycle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task UpWithinScanDoesNotRescanOrClearDuplicates() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"nested\a", "same");
        folder.Write(@"nested\b", "same");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        await model.FindAsync();
        var root = model.Current;
        var duplicates = model.DuplicateRows;
        await model.NavigateAsync(Assert.Single(model.Rows).Node);
        await model.UpAsync();
        Assert.Same(root, model.Current);
        Assert.Same(duplicates, model.DuplicateRows);
        Assert.Equal(folder.Root, model.FolderPath);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UpFromExplorerScanRootScansParentAndClearsOldDuplicates(bool recursive) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"parent\child\a", "same");
        folder.Write(@"parent\child\b", "same");
        folder.Write(@"parent\sibling", "other");
        folder.Write("outer", "outer");
        var parentPath = Path.Combine(folder.Root, "parent");
        var model = new MainViewModel
        {
            FolderPath = Path.Combine(parentPath, "child") + @"\.",
            Recursive = recursive
        };
        await model.ScanAsync();
        await model.FindAsync();
        Assert.Equal(2, model.DuplicateRows.Count);
        model.FolderPath = Path.Combine(folder.Root, "unscanned-edited-path");
        await model.UpAsync();
        Assert.Equal(parentPath, model.FolderPath);
        Assert.Equal(parentPath, model.CurrentPath);
        Assert.Contains(model.Rows, row => row.Name == "sibling");
        Assert.Empty(model.DuplicateRows);
        Assert.False(model.CanRecycle);
        Assert.True(model.CanFind);
        Assert.Equal(recursive, Assert.Single(model.Rows, row => row.Kind == "Folder").Node.IsScanned);
        await model.UpAsync();
        Assert.Equal(folder.Root, model.FolderPath);
        Assert.Equal(folder.Root, model.CurrentPath);
        Assert.Contains(model.Rows, row => row.Name == "outer");
    });

    [Fact]
    public Task UpAtDriveRootDoesNotStartAnotherScan() => Sta.Run(async () =>
    {
        var rootPath = Path.GetPathRoot(Path.GetTempPath())!;
        var model = new MainViewModel { FolderPath = rootPath, Recursive = false };
        await model.ScanAsync();
        Assert.Equal(rootPath, model.CurrentPath);
        var root = model.Current;
        var rows = model.Rows;
        await model.UpAsync();
        Assert.Same(root, model.Current);
        Assert.Same(rows, model.Rows);
        Assert.Equal(rootPath, model.FolderPath);
        Assert.False(model.Busy);
    });

    [Fact]
    public Task ExplorerFolderArgumentDisplaysNormalizedPath() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("sample", "content");
        var model = new MainViewModel { FolderPath = folder.Root + @"\." };
        await model.ScanAsync();
        Assert.Equal(folder.Root, model.FolderPath);
        Assert.Equal(folder.Root, model.CurrentPath);
        Assert.Single(model.Rows);
        Assert.True(model.CanFind);
    });

    [Theory]
    [InlineData(@".")]
    [InlineData(@"\.")]
    public Task ExplorerDriveRootArgumentPreservesRootSeparator(string suffix) => Sta.Run(async () =>
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var model = new MainViewModel { FolderPath = root + suffix, Recursive = false };
        var scan = model.ScanAsync();
        model.Cancel();
        await scan;
        Assert.Equal(root, model.FolderPath);
        Assert.False(model.Busy);
    });

    [Fact]
    public Task CancelAndFailedScanDisableDuplicateActions() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        for (var i = 0; i < 1000; i++) folder.Write(i.ToString(), "123");
        var model = new MainViewModel { FolderPath = folder.Root };
        var scan = model.ScanAsync();
        Assert.True(model.Busy);
        model.Cancel();
        await scan;
        Assert.Contains("cancelled", model.Status);
        Assert.False(model.CanFind);
        Assert.Empty(model.Rows);
        model.FolderPath = Path.Combine(folder.Root, "missing");
        await model.ScanAsync();
        Assert.Contains("failed", model.Status);
        Assert.Single(model.Issues);
        Assert.False(model.CanFind);
    });
}
