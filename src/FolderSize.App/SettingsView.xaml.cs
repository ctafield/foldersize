using System.Windows;
using System.Windows.Controls;
using FolderSize.Core;

namespace FolderSize.App;

public partial class SettingsView : UserControl
{
    private readonly ThemeSettingsStore store;
    private readonly Action<AppSettings> applySettings;
    private AppSettings savedSettings;

    public SettingsView(ThemeSettingsStore store, AppSettings settings, Action<AppSettings> applySettings)
    {
        this.store = store;
        this.applySettings = applySettings;
        savedSettings = settings;
        InitializeComponent();
        ThemeBox.ItemsSource = Enum.GetValues<AppTheme>();
        LoadSettings(settings);
        ThemeBox.SelectionChanged += Theme_SelectionChanged;
        OpenWithDoubleClickBox.Checked += FileOpening_Changed;
        OpenWithDoubleClickBox.Unchecked += FileOpening_Changed;
        StartScanningOnOpenBox.Checked += StartupScanning_Changed;
        StartScanningOnOpenBox.Unchecked += StartupScanning_Changed;
    }

    public void LoadSettings(AppSettings settings)
    {
        savedSettings = settings;
        ThemeBox.SelectedItem = settings.Theme;
        OpenWithDoubleClickBox.IsChecked = settings.OpenWithDoubleClick;
        StartScanningOnOpenBox.IsChecked = settings.StartScanningOnOpen;
        SaveStatus.Text = "Changes apply immediately and are saved for future launches.";
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not AppTheme theme || theme == savedSettings.Theme) return;
        Save(savedSettings with { Theme = theme });
    }

    private void FileOpening_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = OpenWithDoubleClickBox.IsChecked == true;
        if (enabled == savedSettings.OpenWithDoubleClick) return;
        Save(savedSettings with { OpenWithDoubleClick = enabled });
    }

    private void StartupScanning_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = StartScanningOnOpenBox.IsChecked == true;
        if (enabled == savedSettings.StartScanningOnOpen) return;
        Save(savedSettings with { StartScanningOnOpen = enabled });
    }

    private void Save(AppSettings settings)
    {
        try { store.Save(settings); }
        catch (Exception ex) when (FileErrors.IsExpected(ex))
        {
            ThemeBox.SelectedItem = savedSettings.Theme;
            OpenWithDoubleClickBox.IsChecked = savedSettings.OpenWithDoubleClick;
            StartScanningOnOpenBox.IsChecked = savedSettings.StartScanningOnOpen;
            SaveStatus.Text = $"Could not save your settings. Your previous choice is unchanged.\n{ex.Message}";
            return;
        }

        savedSettings = settings;
        applySettings(settings);
        SaveStatus.Text = "Settings saved. Your choices will be used next time you open FolderSize.";
    }

}
