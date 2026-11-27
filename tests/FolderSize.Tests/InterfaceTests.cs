using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    [InlineData(1240)]
    [InlineData(820)]
    public Task DiskCleanupLaunchesWindowsUtilityAboveSettingsWithoutChangingViews(double width) => Sta.Run(async () =>
    {
        var launches = new List<ProcessStartInfo>();
        var window = new MainWindow(new ThemeSettingsStore(), AppTheme.Light,
            launchDiskCleanup: launches.Add) { Width = width, Height = 640 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = (Button)window.FindName("DiskCleanupButton");
            var settings = (ListBoxItem)window.FindName("SettingsNavigationItem");
            var navigation = (ListBox)window.FindName("MainNavigation");
            var sections = (TabControl)window.FindName("MainSections");
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            var settingsBounds = settings.TransformToAncestor(window).TransformBounds(new Rect(settings.RenderSize));
            Assert.True(button.IsVisible);
            Assert.True(bounds.Bottom <= settingsBounds.Top);
            Assert.True(bounds.Width > 24);
            for (var index = 0; index < sections.Items.Count; index++)
            {
                navigation.SelectedIndex = index;
                button.Focus();
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(index, sections.SelectedIndex);
                Assert.Equal(index, navigation.SelectedIndex);
                ((ListBoxItem)window.FindName("DiskCleanupNavigationItem")).IsSelected = true;
                Assert.Equal(index, sections.SelectedIndex);
                Assert.Equal(index, navigation.SelectedIndex);
            }
            Assert.Equal(sections.Items.Count, launches.Count);
            Assert.All(launches, info =>
            {
                Assert.Equal(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), info.FileName);
                Assert.True(info.UseShellExecute);
                Assert.Empty(info.Arguments);
                Assert.Empty(info.Verb);
            });
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1240, 216)]
    [InlineData(820, 56)]
    public Task LeftNavigationSwitchesExistingViewsAndFitsTheWindow(double width, double paneWidth) => Sta.Run(async () =>
    {
        var window = new MainWindow { Width = width, Height = 640 };
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
            var settings = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
            Assert.Same(window, Window.GetWindow(settings));
            Assert.True(settings.IsVisible);
            var appInfo = (TextBlock)window.FindName("AppInfoText");
            Assert.True(appInfo.IsVisible);
            var assembly = typeof(MainWindow).Assembly;
            var version = Assert.IsType<AssemblyInformationalVersionAttribute>(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>());
            var copyright = Assert.IsType<AssemblyCopyrightAttribute>(
                assembly.GetCustomAttribute<AssemblyCopyrightAttribute>());
            Assert.Equal($"Version {version.InformationalVersion.Split('+')[0]}  |  {copyright.Copyright}", appInfo.Text);
            var infoBounds = appInfo.TransformToAncestor(window).TransformBounds(new Rect(appInfo.RenderSize));
            var status = (TextBlock)window.FindName("StatusText");
            var statusBounds = status.TransformToAncestor(window).TransformBounds(new Rect(status.RenderSize));
            Assert.True(statusBounds.Right <= infoBounds.Left);
            Assert.True(infoBounds.Right <= window.ActualWidth);
            Assert.True(infoBounds.Bottom <= window.ActualHeight);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.False(((TextBox)window.FindName("FolderBox")).IsVisible);
            var settingsItem = (ListBoxItem)window.FindName("SettingsNavigationItem");
            var pane = (Border)window.FindName("NavigationPane");
            var bottom = settingsItem.TranslatePoint(new Point(0, settingsItem.ActualHeight), pane).Y;
            Assert.InRange(pane.ActualHeight - bottom, 0, 4);
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
                ((ListBoxItem)window.FindName("SettingsNavigationItem")).IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
                var box = (CheckBox)view.FindName("StartScanningOnOpenBox");
                Assert.Equal(!enabled, box.IsChecked);
                using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    box.IsChecked = enabled;
                Assert.Equal(!enabled, box.IsChecked);
                Assert.Contains("Could not save", ((TextBlock)view.FindName("SaveStatus")).Text);
                Assert.Equal(!enabled, store.LoadSettings().StartScanningOnOpen);
                box.IsChecked = enabled;
                ((ComboBox)view.FindName("ThemeBox")).SelectedItem = AppTheme.Dark;
                var recursive = (CheckBox)window.FindName("IncludeSubfoldersBox");
                recursive.IsChecked = true;
                recursive.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var restored = new ThemeSettingsStore(store.FilePath).LoadSettings();
                Assert.Equal(enabled, restored.StartScanningOnOpen);
                Assert.Equal(AppTheme.Dark, restored.Theme);
                Assert.False(restored.OpenWithDoubleClick);
                Assert.True(restored.IncludeSubfolders);
                ((ComboBox)view.FindName("ThemeBox")).SelectedItem = AppTheme.Light;
                Assert.True(store.LoadSettings().IncludeSubfolders);
                ((ListBox)window.FindName("MainNavigation")).SelectedIndex = 0;
                recursive.IsChecked = false;
                recursive.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                ((ListBoxItem)window.FindName("SettingsNavigationItem")).IsSelected = true;
                ((ComboBox)view.FindName("ThemeBox")).SelectedItem = AppTheme.Dark;
                Assert.False(store.LoadSettings().IncludeSubfolders);
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
    public Task SettingsPageAppliesThemeAndRestoresItAfterReopening() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "settings.json");
        var store = new ThemeSettingsStore(path);
        store.Save(AppTheme.Light);
        var window = new MainWindow(store, store.Load());
        try
        {
            window.Show();
            ((ListBoxItem)window.FindName("SettingsNavigationItem")).IsSelected = true;
            var view = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
            var box = (ComboBox)view.FindName("ThemeBox");
            Assert.Equal(AppTheme.Light, box.SelectedItem);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                box.SelectedItem = AppTheme.Dark;
            Assert.Equal(AppTheme.Light, box.SelectedItem);
            Assert.Contains("Could not save", ((TextBlock)view.FindName("SaveStatus")).Text);
            Assert.True(Assert.IsType<SolidColorBrush>(window.Background).Color.R >= 128);
            Assert.Equal(AppTheme.Light, new ThemeSettingsStore(path).Load());

            box.SelectedItem = AppTheme.Dark;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Assert.IsType<SolidColorBrush>(window.Background).Color.R < 128);
            Assert.Same(window, Window.GetWindow(view));
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.Equal(AppTheme.Dark, new ThemeSettingsStore(path).Load());
        }
        finally { window.Close(); }

        var restoredStore = new ThemeSettingsStore(path);
        var reopened = new MainWindow(restoredStore, restoredStore.Load());
        try
        {
            reopened.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Assert.IsType<SolidColorBrush>(reopened.Background).Color.R < 128);
            ((ListBoxItem)reopened.FindName("SettingsNavigationItem")).IsSelected = true;
            var view = Assert.IsType<SettingsView>(((ContentControl)reopened.FindName("SettingsContent")).Content);
            var box = (ComboBox)view.FindName("ThemeBox");
            Assert.Equal(AppTheme.Dark, box.SelectedItem);
            box.SelectedItem = AppTheme.Light;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Assert.IsType<SolidColorBrush>(reopened.Background).Color.R >= 128);
            box.SelectedItem = AppTheme.System;
            Assert.Equal(AppTheme.System, restoredStore.Load());
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

    [Fact]
    public Task MouseSideButtonsNavigateCachedHistoryAndClearForwardHistoryOnANewBranch() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"first\a", "same");
        folder.Write(@"first\b", "same");
        folder.Write(@"second\file", "other");
        var window = new MainWindow();
        try
        {
            window.Show();
            var model = window.Model;
            model.FolderPath = folder.Root;
            await model.ScanAsync();
            await model.FindAsync();
            var root = model.Current!;
            var first = model.Rows.Single(row => row.Name == "first").Node;
            var second = model.Rows.Single(row => row.Name == "second").Node;
            var duplicates = model.DuplicateRows;
            Assert.False(model.CanGoBack);
            Assert.False(model.CanGoForward);
            await model.NavigateAsync(first);
            var grid = (DataGrid)window.FindName("UsageGrid");

            async Task Press(MouseButton button, ScanNode expected)
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
                grid.RaiseEvent(args);
                Assert.True(args.Handled);
                for (var i = 0; i < 100 && model.Current != expected; i++) await Task.Delay(10);
                Assert.Same(expected, model.Current);
                Assert.Same(duplicates, model.DuplicateRows);
                Assert.Equal(folder.Root, model.FolderPath);
                Assert.True(model.CanFind);
            }

            await Press(MouseButton.XButton1, root);
            Assert.True(model.CanGoForward);
            await Press(MouseButton.XButton2, first);
            Assert.False(model.CanGoForward);
            await Press(MouseButton.XButton1, root);
            await model.RootAsync();
            Assert.True(model.CanGoForward);
            await model.NavigateAsync(second);
            Assert.False(model.CanGoForward);
            await Press(MouseButton.XButton2, second);
            var primary = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
            window.RaiseEvent(primary);
            Assert.False(primary.Handled);
            Assert.Same(second, model.Current);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task FolderHistoryOutsideTheScanNeverRescansOrRestoresStaleResults(bool recursive, bool scanFirst) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"parent\child\file", "child");
        var parent = Path.Combine(folder.Root, "parent");
        var child = Path.Combine(parent, "child");
        var model = new MainViewModel { FolderPath = child + @"\.", Recursive = recursive };
        if (scanFirst) await model.ScanAsync();
        await model.UpAsync();
        await model.UpAsync();
        await model.BackAsync();
        Assert.Equal(parent, model.CurrentPath);
        await model.BackAsync();
        Assert.Equal(child, model.CurrentPath);
        Assert.False(model.CanGoBack);
        await model.BackAsync();
        Assert.Equal(child, model.CurrentPath);
        Assert.Null(model.Current);
        Assert.Empty(model.Rows);
        Assert.False(model.CanFind);
        Assert.False(model.Busy);
        await model.ForwardAsync();
        Assert.Equal(parent, model.CurrentPath);
        Assert.Null(model.Current);
        Assert.Empty(model.Rows);
        Assert.False(model.CanFind);

        folder.Write(@"parent\child\new-file", "new");
        await model.ScanAsync();
        await model.BackAsync();
        Assert.Equal(child, model.CurrentPath);
        Assert.Equal(recursive, model.CanFind);
        if (recursive)
        {
            Assert.Equal(2, model.Rows.Count);
            Assert.NotNull(model.Current);
        }
        else
        {
            Assert.Empty(model.Rows);
            Assert.Null(model.Current);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UpFromExplorerScanRootSelectsParentWithoutScanningAndClearsOldResults(bool recursive) => Sta.Run(async () =>
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
        model.LargeFileSize = "0";
        await model.FindLargeFilesAsync();
        Assert.NotEmpty(model.LargeFiles);
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        model.FolderPath = Path.Combine(folder.Root, "unscanned-edited-path");
        await model.UpAsync();
        Assert.Equal(parentPath, model.FolderPath);
        Assert.Equal(parentPath, model.CurrentPath);
        Assert.Null(model.Current);
        Assert.Empty(model.Rows);
        Assert.Empty(model.LargeFiles);
        Assert.Empty(model.MapGroups.Extensions);
        Assert.Empty(model.DuplicateRows);
        Assert.False(model.CanRecycle);
        Assert.False(model.CanFind);
        Assert.False(model.Busy);
        Assert.Contains(nameof(MainViewModel.CanFind), changes);
        Assert.Contains(nameof(MainViewModel.Summary), changes);
        Assert.Contains(nameof(MainViewModel.CurrentPath), changes);
        await model.UpAsync();
        Assert.Equal(folder.Root, model.FolderPath);
        Assert.Equal(folder.Root, model.CurrentPath);
        Assert.Null(model.Current);
        Assert.Empty(model.Rows);
        await model.ScanAsync();
        Assert.Contains(model.Rows, row => row.Name == "outer");
        Assert.Equal(recursive, Assert.Single(model.Rows, row => row.Kind == "Folder").Node.IsScanned);
        Assert.True(model.CanFind);
    });

    [Theory]
    [InlineData(true, @"\.")]
    [InlineData(false, @"\.")]
    [InlineData(true, @"\")]
    [InlineData(false, @"\")]
    public Task UpBeforeFirstScanSelectsTheLaunchFoldersParentWithoutScanning(bool recursive, string suffix) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"parent\child\file.txt", "child");
        folder.Write(@"parent\sibling.txt", "sibling");
        var parent = Path.Combine(folder.Root, "parent");
        var model = new MainViewModel { FolderPath = Path.Combine(parent, "child") + suffix, Recursive = recursive };
        Assert.Null(model.Current);
        await model.UpAsync();
        Assert.Equal(parent, model.FolderPath);
        Assert.Equal(parent, model.CurrentPath);
        Assert.Null(model.Current);
        Assert.Empty(model.Rows);
        Assert.False(model.CanFind);
        Assert.Equal(recursive, model.Recursive);
        Assert.False(model.Busy);
    });

    [Fact]
    public Task UpBeforeFirstScanReportsAnInvalidPath() => Sta.Run(async () =>
    {
        var model = new MainViewModel { FolderPath = "invalid\0path" };
        await model.UpAsync();
        Assert.StartsWith("Could not navigate to the parent folder:", model.Status);
        Assert.Single(model.Issues);
        Assert.Null(model.Current);
        Assert.False(model.Busy);
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

    [Theory]
    [InlineData(@"F:\.", @"F:\")]
    [InlineData(@"F:\\.", @"F:\")]
    [InlineData(@"F:\sharpemu-0.0.5-nexus-win-x64\.", @"F:\sharpemu-0.0.5-nexus-win-x64")]
    [InlineData(@"F:\sharpemu-0.0.5-nexus-win-x64\", @"F:\sharpemu-0.0.5-nexus-win-x64")]
    [InlineData(@"\\server\share\.", @"\\server\share")]
    [InlineData(@"\\server\share\folder.name\.", @"\\server\share\folder.name")]
    public Task SelectingAFolderNormalizesItsDisplayBeforeAnyScan(string selected, string expected) => Sta.Run(() =>
    {
        var model = new MainViewModel();
        Assert.True(model.SelectFolder(selected));
        Assert.Equal(expected, model.FolderPath);
        Assert.Equal(expected, model.CurrentPath);
        Assert.Null(model.Current);
        Assert.Empty(model.Rows);
        Assert.False(model.CanFind);
        Assert.False(model.Busy);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SelectingAnInvalidFolderReportsTheErrorAndKeepsThePreviousPath() => Sta.Run(() =>
    {
        var model = new MainViewModel();
        var previous = model.FolderPath;
        Assert.False(model.SelectFolder("invalid\0path"));
        Assert.Equal(previous, model.FolderPath);
        Assert.Single(model.Issues);
        Assert.StartsWith("Could not select the folder:", model.Status);
        Assert.False(model.Busy);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SelectingAFolderDoesNotRescanOrDiscardCachedResults() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"nested\file", "cached");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        var current = model.Current;
        var rows = model.Rows;
        Assert.True(model.SelectFolder(Path.Combine(folder.Root, "nested") + @"\."));
        Assert.Equal(Path.Combine(folder.Root, "nested"), model.FolderPath);
        Assert.Same(current, model.Current);
        Assert.Same(rows, model.Rows);
        Assert.True(model.CanFind);
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
