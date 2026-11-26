using System.Text.Json;
using System.Windows;
using FolderSize.Core;

namespace FolderSize.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 1)
        {
            MessageBox.Show("Usage: FolderSize.exe \"folder path\"", "FolderSize",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var settings = new ThemeSettingsStore();
        var preferences = new AppSettings { Theme = AppTheme.System };
        string? settingsError = null;
        try { preferences = settings.LoadSettings(); }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is JsonException)
        {
            settingsError = $"Could not load your settings. Default settings will be used for this session.\n\n" +
                            $"{settings.FilePath}\n{ex.Message}";
        }

        var window = new MainWindow(settings, preferences.Theme, preferences.OpenWithDoubleClick,
            includeSubfolders: preferences.IncludeSubfolders, startScanningOnOpen: preferences.StartScanningOnOpen,
            windowPlacement: preferences.WindowPlacement);
        WindowPlacement.Track(window, settings, preferences.WindowPlacement);
        if (e.Args.Length == 1) window.Model.FolderPath = e.Args[0];
        MainWindow = window;
        window.Show();
        if (settingsError is not null)
            MessageBox.Show(window, settingsError, "Settings could not be loaded",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        if (preferences.StartScanningOnOpen) await window.Model.ScanAsync();
    }
}

