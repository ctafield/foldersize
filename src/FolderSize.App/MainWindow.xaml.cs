using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly Action<string> copyToClipboard;
    private readonly Action<ProcessStartInfo> launchDiskCleanup;
    private readonly FolderWebSearch folderWebSearch;
    private readonly DispatcherTimer driveRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    public MainViewModel Model { get; }

    public MainWindow() : this(new ThemeSettingsStore(), AppTheme.System) { }

    public MainWindow(ThemeSettingsStore settingsStore, AppTheme theme, bool openWithDoubleClick = true,
        FileOpener? fileOpener = null, MainViewModel? model = null, bool includeSubfolders = true,
        bool startScanningOnOpen = true, WindowPlacement? windowPlacement = null, Action<string>? copyToClipboard = null,
        Action<ProcessStartInfo>? launchDiskCleanup = null, FolderWebSearch? folderWebSearch = null)
    {
        this.settingsStore = settingsStore;
        this.openWithDoubleClick = openWithDoubleClick;
        this.includeSubfolders = includeSubfolders;
        this.startScanningOnOpen = startScanningOnOpen;
        this.windowPlacement = windowPlacement;
        this.fileOpener = fileOpener ?? new FileOpener();
        this.copyToClipboard = copyToClipboard ?? Clipboard.SetText;
        this.folderWebSearch = folderWebSearch ?? new FolderWebSearch();
        this.launchDiskCleanup = launchDiskCleanup ?? (info =>
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Windows did not start Disk Clean-up.");
        });
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
        driveRefreshTimer.Tick += async (_, _) => await Model.RefreshDrivesAsync();
        Loaded += async (_, _) =>
        {
            driveRefreshTimer.Start();
            await Model.RefreshDrivesAsync();
        };
        Closed += (_, _) => driveRefreshTimer.Stop();
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
        if (e.Source != MainSections) return;
        if (MainNavigation is not null) MainNavigation.SelectedIndex = MainSections.SelectedIndex;
        if (SettingsTab?.IsSelected != true) return;
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

    private void MainNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainNavigation || MainSections is null) return;
        if (MainNavigation.SelectedItem == DiskCleanupNavigationItem ||
            MainNavigation.SelectedItem == DrivesNavigationItem)
            MainNavigation.SelectedIndex = MainSections.SelectedIndex;
        else if (MainNavigation.SelectedIndex >= 0)
            MainSections.SelectedIndex = MainNavigation.SelectedIndex;
    }

    private void DiskCleanup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            launchDiskCleanup(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"),
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex) ||
                                   ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Could not launch Windows Disk Clean-up.\n\n{ex.Message}",
                "Disk Clean-up not launched", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
    private async void RefreshDrives_Click(object sender, RoutedEventArgs e) => await Model.RefreshDrivesAsync();
    private async void DrivesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!Model.Idle || e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(DrivesList, e.OriginalSource as DependencyObject)
            is ListBoxItem { Content: DriveRow { CanScan: true } drive })
        {
            e.Handled = true;
            MainNavigation.SelectedIndex = 0;
            await Model.ScanDriveAsync(drive);
        }
    }
    private async void DrivesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Model.Idle || DrivesList.SelectedItem is not DriveRow { CanScan: true } drive) return;
        e.Handled = true;
        MainNavigation.SelectedIndex = 0;
        await Model.ScanDriveAsync(drive);
    }
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
        if (!Model.Idle || e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(UsageGrid, e.OriginalSource as DependencyObject) is DataGridRow { Item: UsageRow row })
        {
            if (row.Node.IsDirectory) await Model.NavigateAsync(row.Node);
            else if (openWithDoubleClick) OpenFile(row.FullPath);
        }
    }
    private void FileGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!Model.Idle || !openWithDoubleClick || e.ChangedButton != MouseButton.Left ||
            sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
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
        if (!Model.Idle) return;
        try { fileOpener.Open(path); }
        catch (Exception ex) when (FileErrors.IsExpected(ex) ||
                                   ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Could not open the file:\n{path}\n\n{ex.Message}", "File not opened",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (!Model.Idle || menu.PlacementTarget is not DataGridRow row ||
            row.Item is not (UsageRow or DuplicateRow))
        {
            menu.IsOpen = false;
            return;
        }
        menu.Tag = row.Item;
        if (ItemsControl.ItemsControlFromItemContainer(row) is DataGrid grid)
        {
            if (grid != UsageGrid || !grid.SelectedItems.Contains(row.Item)) grid.SelectedItem = row.Item;
            if (grid == UsageGrid)
                menu.Tag = new UsageContextSelection((UsageRow)row.Item, grid.SelectedItems.Cast<UsageRow>().ToArray());
        }
        var items = menu.Items.OfType<MenuItem>().ToArray();
        foreach (var item in items) { item.IsEnabled = true; item.ToolTip = null; }
        var searchItem = items[3];
        var searchLabel = (TextBlock)searchItem.Header;
        searchItem.IsEnabled = row.Item is UsageRow { Node.IsDirectory: true };
        searchLabel.Text = row.Item is UsageRow { Node.IsDirectory: true } searchFolder
            ? $"Search {searchFolder.Name} on internet" : "Search folder on internet";
        if (!searchItem.IsEnabled) searchItem.ToolTip = "Select a folder to search for its name on the internet.";
        if (row.Item is UsageRow { Node.IsDirectory: true } folder)
        {
            items[0].IsEnabled = folder.Node.IsScanned;
            items[^1].IsEnabled = folder.Node.IsScanned && !folder.Node.HasErrors;
            if (!items[0].IsEnabled) items[0].ToolTip = "Scan this folder before opening it.";
            if (!items[^1].IsEnabled)
                items[^1].ToolTip = "Scan this folder recursively and resolve any Issues before recycling it.";
        }
        if (menu.Tag is UsageContextSelection selection)
        {
            items[^1].IsEnabled = Model.CanFind &&
                selection.Rows.All(selected => !selected.Node.IsDirectory ||
                    (selected.Node.IsScanned && !selected.Node.HasErrors));
            if (!items[^1].IsEnabled)
                items[^1].ToolTip = "Scan the selected folders recursively and resolve any Issues before recycling them.";
        }
    }

    private sealed record UsageContextSelection(UsageRow ClickedRow, IReadOnlyList<UsageRow> Rows);

    private static object? ContextSelection(object sender) => sender is MenuItem item &&
        ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu ? menu.Tag : null;

    private static object? ContextItem(object sender) => ContextSelection(sender) is UsageContextSelection selection
        ? selection.ClickedRow : ContextSelection(sender);

    private static string? ContextPath(object? item) => item switch
    {
        UsageRow row => row.FullPath,
        DuplicateRow row => row.Path,
        _ => null
    };

    private async void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.Idle) return;
        var item = ContextItem(sender);
        if (item is UsageRow { Node.IsDirectory: true } folder) await Model.NavigateAsync(folder.Node);
        else if (ContextPath(item) is { } path) OpenFile(path);
    }

    private void ContextCopyName_Click(object sender, RoutedEventArgs e) => CopyContextPath(sender, filenameOnly: true);
    private void ContextCopyPath_Click(object sender, RoutedEventArgs e) => CopyContextPath(sender, filenameOnly: false);

    private void ContextSearchFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.Idle || ContextItem(sender) is not UsageRow { Node.IsDirectory: true } folder) return;
        try { folderWebSearch.Search(folder.Name); }
        catch (Exception ex) when (FileErrors.IsExpected(ex) ||
                                   ex is Win32Exception or ExternalException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Could not search for the folder on the internet.\n\n{ex.Message}",
                "Folder search not launched", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyContextPath(object sender, bool filenameOnly)
    {
        if (!Model.Idle || ContextPath(ContextItem(sender)) is not { } path) return;
        try { copyToClipboard(filenameOnly ? Path.GetFileName(path) : path); }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, $"Could not copy to the clipboard:\n{ex.Message}", "Copy failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ContextRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanFind) return;
        if (ContextSelection(sender) is UsageContextSelection selection)
        {
            var batchGuidance = "The selected files and folders will be moved to the Recycle Bin. No duplicate copy is required or kept.\n" +
                           "Folders and all of their contents are rechecked against the scan.\n" +
                           "Local fixed drives only; permanent deletion is never used.";
            if (ConfirmRecycling(selection.Rows.Select(row => (row.FullPath, row.Bytes)).ToArray(), batchGuidance, "items"))
                await Model.RecycleItemsAsync(selection.Rows.Select(row => row.Node).ToArray());
            return;
        }
        var item = ContextItem(sender);
        if (ContextPath(item) is not { } path) return;
        var size = item switch { UsageRow row => row.Bytes, DuplicateRow row => row.File.Stamp.Length, _ => 0 };
        var guidance = item is UsageRow { Node.IsDirectory: true }
            ? "This folder and all of its contents will be moved to the Recycle Bin. Contents are rechecked against the scan."
            : item is DuplicateRow
                ? "A verified duplicate copy will be kept."
                : "This file will be moved to the Recycle Bin. No duplicate copy is required or kept.";
        if (!ConfirmRecycling([(path, size)], guidance + "\nLocal fixed drives only; permanent deletion is never used.")) return;
        if (item is UsageRow rowItem) await Model.RecycleItemAsync(rowItem.Node);
        else if (item is DuplicateRow duplicate) await Model.RecycleDuplicateAsync(duplicate);
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
    private bool ConfirmRecycling(IReadOnlyList<(string Path, long Size)> files, string guidance, string itemLabel = "files")
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
            Text = $"Recycle {files.Count:N0} {itemLabel} ({SizeText.Format(files.Sum(file => file.Size))} logical)?\n" + guidance,
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