using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FolderSize.Core;
using Microsoft.Win32;

namespace FolderSize.App;

public partial class MainWindow : Window
{
    private readonly ThemeSettingsStore settingsStore;
    private AppTheme selectedTheme;
    private bool openWithDoubleClick;
    private bool includeSubfolders;
    private bool startScanningOnOpen;
    private readonly WindowPlacement? windowPlacement;
    private readonly FileOpener fileOpener;
    public MainViewModel Model { get; }

    public MainWindow() : this(new ThemeSettingsStore(), AppTheme.System) { }

    public MainWindow(ThemeSettingsStore settingsStore, AppTheme theme, bool openWithDoubleClick = true,
        FileOpener? fileOpener = null, MainViewModel? model = null, bool includeSubfolders = true,
        bool startScanningOnOpen = true, WindowPlacement? windowPlacement = null)
    {
        this.settingsStore = settingsStore;
        this.openWithDoubleClick = openWithDoubleClick;
        this.includeSubfolders = includeSubfolders;
        this.startScanningOnOpen = startScanningOnOpen;
        this.windowPlacement = windowPlacement;
        this.fileOpener = fileOpener ?? new FileOpener();
        Model = model ?? new MainViewModel();
        Model.Recursive = includeSubfolders;
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("Application version metadata is missing.");
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? throw new InvalidOperationException("Application copyright metadata is missing.");
        AppInfoText.Text = $"Version {version.Split('+')[0]}  |  {copyright}";
        ApplyTheme(theme);
        DataContext = Model;
        Map.NodeSelected += node =>
        {
            UsageGrid.SelectedItem = Model.Rows.FirstOrDefault(row => row.Node == node);
            if (UsageGrid.SelectedItem is { } item) UsageGrid.ScrollIntoView(item);
        };
        Map.NodeOpened += async node => await Model.NavigateAsync(node);
    }

    // WPF's code-based ThemeMode API remains marked experimental.
#pragma warning disable WPF0001
    private void ApplyTheme(AppTheme theme)
    {
        var mode = theme switch
        {
            AppTheme.System => System.Windows.ThemeMode.System,
            AppTheme.Light => System.Windows.ThemeMode.Light,
            AppTheme.Dark => System.Windows.ThemeMode.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(theme))
        };
        if (Application.Current is { } app) app.ThemeMode = mode;
        ThemeMode = mode;
        foreach (Window owned in OwnedWindows) owned.ThemeMode = mode;
        selectedTheme = theme;
    }
#pragma warning restore WPF0001

    private AppSettings CurrentSettings() => new()
    {
        Theme = selectedTheme,
        OpenWithDoubleClick = openWithDoubleClick,
        IncludeSubfolders = Model.Recursive,
        StartScanningOnOpen = startScanningOnOpen,
        WindowPlacement = windowPlacement
    };

    private void MainSections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainSections || SettingsTab?.IsSelected != true) return;
        if (SettingsContent.Content is SettingsView view)
        {
            view.LoadSettings(CurrentSettings());
            return;
        }
        SettingsContent.Content = new SettingsView(settingsStore, CurrentSettings(), updated =>
        {
            openWithDoubleClick = updated.OpenWithDoubleClick;
            startScanningOnOpen = updated.StartScanningOnOpen;
            ApplyTheme(updated.Theme);
        });
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to scan",
            InitialDirectory = Model.FolderPath
        };
        if (dialog.ShowDialog(this) == true) Model.SelectFolder(dialog.FolderName);
    }

    private void IncludeSubfolders_Click(object sender, RoutedEventArgs e)
    {
        var enabled = IncludeSubfoldersBox.IsChecked == true;
        try
        {
            settingsStore.Save(CurrentSettings());
            includeSubfolders = enabled;
            if (SettingsContent.Content is SettingsView view) view.LoadSettings(CurrentSettings());
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex))
        {
            Model.Recursive = includeSubfolders;
            MessageBox.Show(this, $"Could not save your settings. Your previous choice is unchanged.\n{ex.Message}",
                "Settings could not be saved", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Model.ScanAsync();
    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.XButton1) await Model.BackAsync();
        else await Model.ForwardAsync();
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        NavigationPane.Width = e.NewSize.Width < 1100 ? 56 : 216;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Model.Cancel();
    private async void Up_Click(object sender, RoutedEventArgs e) => await Model.UpAsync();
    private async void Root_Click(object sender, RoutedEventArgs e) => await Model.RootAsync();
    private async void Find_Click(object sender, RoutedEventArgs e) => await Model.FindAsync();
    private async void ShowLargeFiles_Click(object sender, RoutedEventArgs e) => await Model.FindLargeFilesAsync();
    private async void LargeFileSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model.CanFind)
        {
            e.Handled = true;
            await Model.FindLargeFilesAsync();
        }
    }
    private async void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model.Idle) { e.Handled = true; await Model.ScanAsync(); }
    }
    private void UsageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Map.SelectedNode = (UsageGrid.SelectedItem as UsageRow)?.Node;
    private async void UsageGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!Model.Idle) return;
        if (ItemsControl.ContainerFromElement(UsageGrid, e.OriginalSource as DependencyObject) is DataGridRow { Item: UsageRow row })
        {
            if (row.Node.IsDirectory) await Model.NavigateAsync(row.Node);
            else OpenFile(row.FullPath);
        }
    }
    private void FileGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!Model.Idle || sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        for (var element = source; element is not null && element != grid;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is DataGridCell { Column: DataGridCheckBoxColumn }) return;
            if (element is not DataGridRow row) continue;
            var path = row.Item switch
            {
                UsageRow file => file.FullPath,
                DuplicateRow duplicate => duplicate.Path,
                _ => null
            };
            if (path is not null) OpenFile(path);
            return;
        }
    }
    private void OpenFile(string path)
    {
        if (!openWithDoubleClick || !Model.Idle) return;
        try { fileOpener.Open(path); }
        catch (Exception ex) when (FileErrors.IsExpected(ex) ||
                                   ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Could not open the file:\n{path}\n\n{ex.Message}", "File not opened",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void UsageGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && UsageGrid.SelectedItem is UsageRow row)
        {
            e.Handled = true;
            await Model.NavigateAsync(row.Node);
        }
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Model.DuplicateRows) row.Selected = false;
    }
    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        DuplicatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DuplicatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in Model.DuplicateRows) row.Selected = !row.Selected;
    }
    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        DuplicatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DuplicatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Model.KeepsOneCopy())
        {
            MessageBox.Show(this, "Keep at least one file in every duplicate group.", "Nothing recycled",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var files = Model.SelectedFiles();
        if (files.Count == 0) return;
        if (ConfirmRecycling(files.Select(file => (file.Path, file.Stamp.Length)).ToArray(),
                "One copy per group will be kept. Unsupported files will not be permanently deleted."))
            await Model.RecycleAsync();
    }
    private async void RecycleLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        LargeFilesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LargeFilesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Model.CanRecycleLargeFiles) return;
        var files = Model.LargeFiles.Where(row => row.Selected).Select(row => (row.FullPath, row.Bytes)).ToArray();
        if (ConfirmRecycling(files,
                "These files will be moved to the Recycle Bin. No duplicate copy is required or kept.\n" +
                "Files whose size or modification time changed since the scan will be skipped.\n" +
                "Local fixed drives only; permanent deletion is never used."))
            await Model.RecycleLargeFilesAsync();
    }
    private bool ConfirmRecycling(IReadOnlyList<(string Path, long Size)> files, string guidance)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Confirm recycling", Width = 760, Height = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize
        };
        dialog.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        dialog.SetResourceReference(ForegroundProperty, "WindowForeground");
        ApplyTheme(selectedTheme);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var confirm = new Button { Name = "ConfirmRecycleButton", Content = "Recycle selected", IsDefault = false };
        var cancel = new Button { Name = "CancelRecycleButton", Content = "Cancel", IsCancel = true, IsDefault = true };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var heading = new TextBlock
        {
            Text = $"Recycle {files.Count:N0} files ({SizeText.Format(files.Sum(file => file.Size))} logical)?\n" + guidance,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        panel.Children.Add(new ListBox { ItemsSource = files.Select(file => file.Path) });
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!Model.Busy) return;
        Model.Cancel();
        e.Cancel = true;
        MessageBox.Show(this, "Cancellation requested. Wait for the operation to finish, then close the app.",
            "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}