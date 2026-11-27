using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderSize.App;

namespace FolderSize.Tests;

public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SizePositionAndMaximizedStateSurviveClosingAndReopening(bool maximized) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json"));
        store.Save(new AppSettings { Theme = AppTheme.Light, IncludeSubfolders = false });
        var window = new MainWindow(store, AppTheme.Light, includeSubfolders: false);
        WindowPlacement.Track(window, store, null);
        WindowPlacement expected;
        try
        {
            window.Show();
            window.Width = 900;
            window.Height = 660;
            window.Left = SystemParameters.WorkArea.Left + 20;
            window.Top = SystemParameters.WorkArea.Top + 20;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            expected = WindowPlacement.Capture(window) with { Maximized = maximized };
            if (maximized)
            {
                window.WindowState = WindowState.Maximized;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            window.WindowState = WindowState.Minimized;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(expected, WindowPlacement.Capture(window));
        }
        finally { window.Close(); }

        var restored = store.LoadSettings();
        Assert.Equal(expected, restored.WindowPlacement);
        Assert.False(restored.IncludeSubfolders);
        var reopened = new MainWindow(store, restored.Theme, includeSubfolders: restored.IncludeSubfolders,
            windowPlacement: restored.WindowPlacement, updateChecker: TestUpdates.NoUpdates);
        WindowPlacement.Track(reopened, store, restored.WindowPlacement);
        try
        {
            reopened.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(maximized ? WindowState.Maximized : WindowState.Normal, reopened.WindowState);
            Assert.Equal(expected, WindowPlacement.Capture(reopened));
            ((ListBoxItem)reopened.FindName("SettingsNavigationItem")).IsSelected = true;
            var settings = Assert.IsType<SettingsView>(((ContentControl)reopened.FindName("SettingsContent")).Content);
            ((ComboBox)settings.FindName("ThemeBox")).SelectedItem = AppTheme.Dark;
            Assert.Equal(expected, store.LoadSettings().WindowPlacement);
        }
        finally { reopened.Close(); }
        Assert.Equal(AppTheme.Dark, store.LoadSettings().Theme);
    });

    [Theory]
    [InlineData(1350, 1000)]
    [InlineData(100, 100)]
    public Task MissingMonitorPlacementIsRestoredOnScreenAndRespectsMinimumSize(int width, int height) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json"));
        var placement = new WindowPlacement
        {
            Left = 1_000_000, Top = 1_000_000, Right = 1_000_000 + width, Bottom = 1_000_000 + height
        };
        store.Save(new AppSettings { Theme = AppTheme.Light, WindowPlacement = placement });
        var window = new MainWindow(store, AppTheme.Light, windowPlacement: placement);
        WindowPlacement.Track(window, store, placement);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Assert.True(desktop.IntersectsWith(new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)));
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(window.ActualWidth >= window.MinWidth);
            Assert.True(window.ActualHeight >= window.MinHeight);
        }
        finally { window.Close(); }
    });
}
