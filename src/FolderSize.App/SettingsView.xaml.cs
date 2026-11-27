using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using FolderSize.Core;

namespace FolderSize.App;

public partial class SettingsView : UserControl
{
    private readonly ThemeSettingsStore store;
    private readonly Action<AppSettings> applySettings;
    private readonly AppUpdateChecker updateChecker;
    private readonly Action<ProcessStartInfo> launchReleases;
    private readonly string installedVersion;
    private readonly CancellationTokenSource updateCancellation = new();
    private bool automaticCheckAttempted, checkingUpdates, closed;
    private AppSettings savedSettings;

    public SettingsView(ThemeSettingsStore store, AppSettings settings, Action<AppSettings> applySettings,
        string installedVersion, AppUpdateChecker updateChecker, Action<ProcessStartInfo>? launchReleases = null)
    {
        this.store = store;
        this.applySettings = applySettings;
        this.installedVersion = installedVersion;
        this.updateChecker = updateChecker;
        this.launchReleases = launchReleases ?? (info =>
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Windows did not open the releases page.");
        });
        savedSettings = settings;
        InitializeComponent();
        ThemeBox.ItemsSource = Enum.GetValues<AppTheme>();
        LoadSettings(settings);
        InstalledVersionText.Text = $"Installed version {installedVersion.Split('+')[0]}";
        Loaded += Settings_Loaded;
        ThemeBox.SelectionChanged += Theme_SelectionChanged;
        OpenWithDoubleClickBox.Checked += FileOpening_Changed;
        OpenWithDoubleClickBox.Unchecked += FileOpening_Changed;
        StartScanningOnOpenBox.Checked += StartupScanning_Changed;
        StartScanningOnOpenBox.Unchecked += StartupScanning_Changed;
    }

    private async void Settings_Loaded(object sender, RoutedEventArgs e)
    {
        if (automaticCheckAttempted || closed) return;
        automaticCheckAttempted = true;
        await CheckForUpdatesAsync();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (checkingUpdates || closed) return;
        checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for updates...";
        try
        {
            var result = await updateChecker.CheckAsync(installedVersion, updateCancellation.Token);
            if (closed) return;
            var latest = result.LatestVersion;
            var latestText = latest?.ToString(latest.Revision == 0 ? 3 : 4);
            UpdateStatus.Text = result.LatestVersion is null
                ? "No stable releases have been published yet."
                : result.UpdateAvailable
                    ? $"\u2728 New version available: {latestText}. Use the download link below."
                    : $"No newer stable version is available (latest: {latestText}).";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Update check timed out. Please try again.";
        }
        catch (HttpRequestException ex)
        {
            if (closed) return;
            UpdateStatus.Text = ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? $"GitHub refused the update check or a rate limit was reached (HTTP {(int)ex.StatusCode.Value}). Please try again later."
                : $"Could not check for updates. Please try again.\n{ex.Message}";
        }
        catch (Exception ex) when (ex is JsonException || FileErrors.IsExpected(ex))
        {
            if (!closed) UpdateStatus.Text = $"Could not check for updates. Please try again.\n{ex.Message}";
        }
        finally
        {
            checkingUpdates = false;
            if (!closed) CheckUpdatesButton.IsEnabled = true;
        }
    }

    internal void CancelUpdateCheck()
    {
        if (closed) return;
        closed = true;
        updateCancellation.Cancel();
        updateCancellation.Dispose();
    }

    private void DownloadLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            launchReleases(new ProcessStartInfo(AppUpdateChecker.ReleasesUri.AbsoluteUri) { UseShellExecute = true });
            ReleaseLinkStatus.Text = "";
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is Win32Exception or InvalidOperationException)
        {
            ReleaseLinkStatus.Text = $"Could not open the releases page.\n{ex.Message}";
        }
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
