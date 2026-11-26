using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.IO;
using Microsoft.Win32;
using FolderSize.Core;

namespace FolderSize.App;

public partial class RenameView : UserControl
{
    public RenameView() => InitializeComponent();

    private MainWindow OwnerWindow => (MainWindow)Window.GetWindow(this);

    private async void DetectPattern_Click(object sender, RoutedEventArgs e)
    {
        RenameGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RenameGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (DataContext is not RenameViewModel model || !model.CanDetectPattern) return;
        var owner = OwnerWindow;
        var suggestions = await model.DetectPatternsAsync();
        if (suggestions is not { Count: > 0 } || !owner.IsVisible) return;
        var dialog = new Window
        {
            Owner = owner, Title = "Detect pattern", Width = 800, Height = 520, MinWidth = 620, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        dialog.SetResourceReference(ForegroundProperty, "WindowForeground");
#pragma warning disable WPF0001
        dialog.ThemeMode = owner.ThemeMode;
#pragma warning restore WPF0001
        var panel = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock
        {
            Text = "Suggestions use filenames only and may be imperfect. Select a pattern to see examples, " +
                "then load it into the editable preview. Descriptive trailing text and extensions are preserved. " +
                "Nothing is renamed until you review the preview and Apply.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10)
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var use = new Button { Content = "Use suggestion", Padding = new(12, 6, 12, 6), Margin = new(0, 8, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, Padding = new(12, 6, 12, 6), Margin = new(0, 8, 0, 0) };
        use.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(use);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var list = new ListBox { ItemsSource = suggestions, DisplayMemberPath = nameof(RenamePatternSuggestion.Summary), Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetName(list, "Detected rename patterns");
        var details = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8)
        };
        AutomationProperties.SetName(details, "Pattern examples and rule");
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not RenamePatternSuggestion selected) return;
            var rule = selected.Options.Rule == RenameRule.ExtractFormat
                ? $"Format: {selected.Options.Template}\nPattern: {selected.Options.Pattern}"
                : selected.Options.Rule == RenameRule.Capitalisation ? "Rule: Title case" :
                    $"Find: \"{selected.Options.Find}\"   Replace: \"{selected.Options.Replace}\"";
            details.Text = selected.Summary + "\n\n" + rule + "\n\nExamples:\n" + string.Join("\n", selected.Examples);
        };
        Grid.SetRow(details, 1);
        content.Children.Add(list);
        content.Children.Add(details);
        panel.Children.Add(content);
        dialog.Content = panel;
        list.SelectedIndex = 0;
        if (dialog.ShowDialog() == true && list.SelectedItem is RenamePatternSuggestion suggestion)
            model.UsePatternSuggestion(suggestion);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        RenameGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RenameGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (DataContext is not RenameViewModel model || !model.CanApply) return;
        var files = model.SelectedRequests();
        if (Confirm("Apply rename", $"Rename {files.Count:N0} files?\nNo files will be overwritten. " +
                "This replaces the previous undo history.",
                files.Select(file => $"{file.OriginalPath}  ->  {file.NewName}").ToArray()))
            await OwnerWindow.Model.ApplyRenameAsync();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RenameViewModel { CanUndo: true } model) return;
        if (Confirm("Undo last rename", "Restore the previous names? Changed or replaced files and occupied names " +
                "will be skipped. Unresolved entries stay available for another undo attempt.",
                model.UndoEntries.Select(entry => $"{entry.NewPath}  ->  {entry.OriginalPath}").ToArray()))
            await OwnerWindow.Model.UndoRenameAsync();
    }

    private async void MoveConflicts_Click(object sender, RoutedEventArgs e)
    {
        RenameGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RenameGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (DataContext is not RenameViewModel { CanMoveConflicts: true } model) return;
        var files = model.ConflictFiles();
        var dialog = new Window
        {
            Owner = OwnerWindow, Title = "Move conflicts", Width = 660, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize
        };
        dialog.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        dialog.SetResourceReference(ForegroundProperty, "WindowForeground");
#pragma warning disable WPF0001
        dialog.ThemeMode = OwnerWindow.ThemeMode;
#pragma warning restore WPF0001
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Move all {files.Count:N0} conflicting files before renaming. Choose a destination folder; " +
                "it will be created if needed. Current filenames are kept, with numeric suffixes only to avoid overwriting. " +
                "This is separate from Apply and cannot be undone with Undo last rename.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12)
        });
        var path = new TextBox { Text = model.DefaultConflictFolder, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetName(path, "Conflict destination folder");
        panel.Children.Add(path);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var browse = new Button { Content = "Browse...", Margin = new(0, 0, 8, 0), Padding = new(12, 6, 12, 6) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Choose conflict destination", InitialDirectory = model.ScopePath };
            if (picker.ShowDialog(dialog) != true) return;
            try { path.Text = FolderPaths.Normalize(picker.FolderName); }
            catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is ArgumentException or NotSupportedException)
            { error.Text = ex.Message; }
        };
        string? destination = null;
        var move = new Button { Content = "Move conflicts", Margin = new(0, 0, 8, 0), Padding = new(12, 6, 12, 6) };
        move.Click += (_, _) =>
        {
            try
            {
                if (!Path.IsPathFullyQualified(path.Text)) throw new ArgumentException("Enter a full folder path.");
                var target = FolderPaths.Normalize(path.Text);
                if (files.Any(file => string.Equals(Path.GetDirectoryName(file.FullPath), target, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Choose a destination different from the source folders.");
                destination = target;
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { error.Text = ex.Message; }
        };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, Padding = new(12, 6, 12, 6) };
        buttons.Children.Add(browse);
        buttons.Children.Add(move);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() == true && destination is not null)
            await OwnerWindow.Model.MoveConflictsAsync(destination);
    }

    private bool Confirm(string title, string message, IReadOnlyList<string> entries)
    {
        var window = new Window
        {
            Owner = OwnerWindow, Title = title, Width = 740, Height = entries.Count == 0 ? 240 : 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        window.SetResourceReference(ForegroundProperty, "WindowForeground");
#pragma warning disable WPF0001
        window.ThemeMode = OwnerWindow.ThemeMode;
#pragma warning restore WPF0001
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, Padding = new(16, 8, 16, 8), Margin = new(8, 0, 0, 0) };
        var apply = new Button { Content = title, Padding = new(16, 8, 16, 8) };
        apply.Click += (_, _) => window.DialogResult = true;
        cancel.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var heading = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        panel.Children.Add(new ListBox { ItemsSource = entries });
        window.Content = panel;
        return window.ShowDialog() == true;
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RenameViewModel model) model.SavePreset();
    }
}
