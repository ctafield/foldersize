using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RenameInterfaceTests
{
    [Fact]
    public Task DetectPatternChooserLoadsOnlyTheChosenRuleAndNeverChangesFiles() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var first = folder.Write(@"files\Report_1 - intro.txt", "first");
        var second = folder.Write(@"files\Report 02 - other.txt", "second");
        var unrelated = folder.Write(@"files\unrelated.txt", "other");
        folder.Write(@"files\nested\Report_3.txt", "nested");
        folder.Write(@"files\Report_4.pdf", "ignored");
        var model = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")))
        { FolderPath = Path.GetDirectoryName(first)!, Recursive = true };
        var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json")), AppTheme.Light, model: model);
        try
        {
            window.Show();
            ((TabItem)window.FindName("RenameTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = (RenameView)window.FindName("RenamePanel");
            var detect = (Button)view.FindName("DetectPatternButton");
            Assert.False(detect.IsEnabled);
            await model.ScanAsync();
            var rename = model.Rename;
            rename.Filter = "*.txt";
            await rename.PreviewTask;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(rename.CanApply);
            Assert.True(detect.IsEnabled);
            async Task Choose(bool use)
            {
                var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    var dialog = window.OwnedWindows.Cast<Window>().SingleOrDefault();
                    if (dialog is null) return;
                    timer.Stop();
                    try
                    {
                        Assert.Equal("Detect pattern", dialog.Title);
                        var panel = Assert.IsType<DockPanel>(dialog.Content);
                        var content = panel.Children.OfType<Grid>().Single();
                        var list = content.Children.OfType<ListBox>().Single();
                        var selected = Assert.IsType<RenamePatternSuggestion>(list.SelectedItem);
                        Assert.Equal(2, selected.Matched);
                        Assert.Equal(3, selected.Total);
                        Assert.Contains("intro.txt", content.Children.OfType<TextBox>().Single().Text);
                        dialog.Width = dialog.MinWidth;
                        dialog.Height = dialog.MinHeight;
                        dialog.UpdateLayout();
                        Assert.True(list.ActualHeight >= 50);
                        Assert.True(content.Children.OfType<TextBox>().Single().ActualHeight >= 50);
                        var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                        Assert.True(buttons.Single(item => Equals(item.Content, "Cancel")).IsDefault);
                        buttons.Single(item => Equals(item.Content, use ? "Use suggestion" : "Cancel"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        handled.SetResult();
                    }
                    catch (Exception ex) { handled.SetException(ex); }
                    finally { dialog.Close(); }
                };
                timer.Start();
                try
                {
                    detect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await rename.PreviewTask;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                finally { timer.Stop(); }
            }
            await Choose(false);
            Assert.Equal((int)RenameRule.FindReplace, rename.RuleIndex);
            await Choose(true);
            Assert.Equal((int)RenameRule.ExtractFormat, rename.RuleIndex);
            Assert.Single(rename.Rows, row => row.Status == "Ready");
            Assert.Single(rename.Rows, row => row.Status == "Unchanged");
            var unmatched = Assert.Single(rename.Rows, row => row.FullPath == unrelated);
            Assert.False(unmatched.Selected);
            Assert.Contains("did not match", unmatched.Status);
            Assert.True(rename.CanApply);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.False(rename.CanUndo);
            rename.IncludeSubfolders = true;
            var recursive = await rename.DetectPatternsAsync();
            Assert.NotNull(recursive);
            Assert.Contains(recursive, suggestion => suggestion.Title.StartsWith("Numbered series:", StringComparison.Ordinal)
                && suggestion.Matched == 3 && suggestion.Total == 4);
            rename.Filter = "*.pdf";
            Assert.Throws<InvalidOperationException>(() => rename.UsePatternSuggestion(recursive[0]));
            var stale = rename.DetectPatternsAsync();
            rename.Filter = "*.txt";
            Assert.Null(await stale);
            await rename.PreviewTask;
            rename.Filter = @"..\*.txt";
            Assert.Null(await rename.DetectPatternsAsync());
            Assert.StartsWith("Pattern detection failed:", rename.Status);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DetectedRulesKeepConflictProtectionAndCanBeSavedAsPresets() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var a = folder.Write(@"files\Photo1.txt", "a");
        var b = folder.Write(@"files\photo01.txt", "b");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var model = new MainViewModel(store) { FolderPath = Path.GetDirectoryName(a)! };
        await model.ScanAsync();
        var rename = model.Rename;
        var suggestions = await rename.DetectPatternsAsync();
        Assert.NotNull(suggestions);
        var suggestion = suggestions.First(item => item.Title.StartsWith("Numbered series", StringComparison.Ordinal));
        rename.UsePatternSuggestion(suggestion);
        await rename.PreviewTask;
        Assert.True(rename.HasConflicts);
        Assert.False(rename.CanApply);
        Assert.All(rename.Rows, row => Assert.StartsWith("Conflict:", row.Status));
        Assert.NotNull(await rename.DetectPatternsAsync());
        Assert.StartsWith("Conflict:", rename.Status);
        rename.PresetName = "Detected photo pattern";
        rename.SavePreset();
        Assert.Equal(suggestion.Options, store.LoadPresets()["Detected photo pattern"]);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Null(store.LoadJournal());
    });

    [Fact]
    public Task MoveConflictsIsSeparateFromRenamingAndExcludesMovedFilesFromRecursivePreview() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var a = folder.Write(@"files\a.txt", "a");
        var b = folder.Write(@"files\b.txt", "b");
        var ready = folder.Write(@"files\ready.txt", "ready");
        var history = folder.Write(@"history\old.txt", "history");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        new Renaming(new WindowsFileAccess(), store).Apply(
            [new(history, "new.txt", new FileInfo(history).Length, File.GetLastWriteTimeUtc(history))],
            Path.GetDirectoryName(history)!, false, null);
        var journal = File.ReadAllText(store.JournalPath);
        var model = new MainViewModel(store) { FolderPath = Path.GetDirectoryName(a)!, Recursive = true };
        var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json")), AppTheme.Light, model: model);
        try
        {
            window.Show();
            await model.ScanAsync();
            var rename = model.Rename;
            rename.RuleIndex = (int)RenameRule.ExtractFormat;
            rename.Pattern = ".*";
            rename.Template = "same";
            rename.IncludeSubfolders = true;
            await rename.PreviewTask;
            rename.Rows.Single(row => row.FullPath == ready).ProposedName = "ready-renamed.txt";
            await rename.PreviewTask;
            Assert.Equal(2, rename.ConflictFiles().Count);
            Assert.All(rename.Rows.Where(row => row.FullPath != ready), row => Assert.False(row.Selected));
            ((TabItem)window.FindName("RenameTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = (RenameView)window.FindName("RenamePanel");
            var button = (Button)view.FindName("MoveConflictsButton");
            Assert.True(button.IsEnabled);
            var destination = Path.Combine(Path.GetDirectoryName(a)!, "Conflicts");
            async Task ClickMove(bool confirm)
            {
                var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Window? dialog = null;
                    try
                    {
                        dialog = Assert.Single(window.OwnedWindows.Cast<Window>());
                        var panel = Assert.IsType<StackPanel>(dialog.Content);
                        Assert.Equal(destination, panel.Children.OfType<TextBox>().Single().Text);
                        var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                        Assert.True(buttons.Single(item => Equals(item.Content, "Cancel")).IsDefault);
                        buttons.Single(item => Equals(item.Content, confirm ? "Move conflicts" : "Cancel"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        handled.SetResult();
                    }
                    catch (Exception ex) { handled.SetException(ex); }
                    finally { dialog?.Close(); }
                }));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await handled.Task;
                while (model.Busy) await Task.Delay(10);
                await rename.PreviewTask;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            await ClickMove(false);
            Assert.True(File.Exists(a));
            Assert.False(Directory.Exists(destination));
            await ClickMove(true);
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(b));
            Assert.Equal("a", File.ReadAllText(Path.Combine(destination, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(destination, "b.txt")));
            Assert.True(File.Exists(ready));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ready)!, "same.txt")));
            Assert.Equal(ready, Assert.Single(rename.Rows).FullPath);
            var suggestions = await rename.DetectPatternsAsync();
            Assert.NotNull(suggestions);
            Assert.Empty(suggestions);
            Assert.False(rename.HasConflicts);
            Assert.True(rename.CanApply);
            Assert.False(button.IsEnabled);
            Assert.Equal(journal, File.ReadAllText(store.JournalPath));
            Assert.Contains("Move conflicts finished: 2 files changed", model.Status);
            Assert.Empty(model.Issues);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ApplyAndUndoButtonsRequireConfirmationAndCancelIsSafe() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"files\old.txt", "content");
        var model = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")));
        var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json")), AppTheme.Light, model: model);
        try
        {
            window.Show();
            model.FolderPath = Path.GetDirectoryName(path)!;
            await model.ScanAsync();
            model.Rename.Find = "old";
            model.Rename.Replace = "new";
            await model.Rename.PreviewTask;
            ((TabItem)window.FindName("RenameTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = (RenameView)window.FindName("RenamePanel");
            async Task ClickAndConfirm(string name, bool confirm)
            {
                var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Window? dialog = null;
                    try
                    {
                        dialog = Assert.Single(window.OwnedWindows.Cast<Window>());
                        var panel = Assert.IsType<DockPanel>(dialog.Content);
                        var text = Assert.Single(panel.Children.OfType<ListBox>().Single().Items.Cast<string>());
                        Assert.Contains("old.txt", text);
                        Assert.Contains("new.txt", text);
                        var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                        Assert.True(buttons.Single(button => Equals(button.Content, "Cancel")).IsDefault);
                        (confirm ? buttons[0] : buttons[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        handled.SetResult();
                    }
                    catch (Exception ex) { handled.SetException(ex); }
                    finally { dialog?.Close(); }
                }));
                ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await handled.Task;
                while (model.Busy) await Task.Delay(10);
                await model.Rename.PreviewTask;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            await ClickAndConfirm("ApplyRenameButton", false);
            Assert.True(File.Exists(path));
            Assert.False(model.Rename.CanUndo);
            await ClickAndConfirm("ApplyRenameButton", true);
            Assert.False(File.Exists(path));
            Assert.True(model.Rename.CanUndo);
            await ClickAndConfirm("UndoRenameButton", true);
            Assert.True(File.Exists(path));
            Assert.False(model.Rename.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LivePreviewUsesFiltersScopeAndLatestRuleWithoutChangingFiles() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var pdf = folder.Write(@"files\old.pdf", "pdf");
        folder.Write(@"files\old.txt", "text");
        folder.Write(@"files\nested\old.pdf", "nested");
        var model = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")))
        { FolderPath = Path.GetDirectoryName(pdf)! };
        await model.ScanAsync();
        var rename = model.Rename;
        rename.Find = "old";
        rename.Replace = "new";
        rename.Filter = "*.pdf";
        await rename.PreviewTask;
        Assert.Equal("new.pdf", Assert.Single(rename.Rows).ProposedName);
        Assert.True(rename.CanApply);
        rename.IncludeSubfolders = true;
        rename.Replace = "discarded";
        rename.Replace = "latest";
        await rename.PreviewTask;
        Assert.Equal(2, rename.Rows.Count);
        Assert.All(rename.Rows, row => Assert.Equal("latest.pdf", row.ProposedName));
        Assert.False(rename.HasConflicts);
        Assert.True(rename.CanApply);
        Assert.True(File.Exists(pdf));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(pdf)!, "latest.pdf")));
        await model.NavigateAsync(model.Rows.Single(row => row.Kind == "Folder").Node);
        await rename.PreviewTask;
        Assert.Single(rename.Rows);
        Assert.EndsWith("nested", rename.ScopePath);
        rename.Filter = @"..\*.pdf";
        await rename.PreviewTask;
        Assert.False(rename.CanApply);
        Assert.Contains("without folder paths", rename.Status);
    });

    [Fact]
    public Task ConflictsAndManualCorrectionsUpdateApplyAvailability() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"files\a.txt", "a");
        folder.Write(@"files\b.txt", "b");
        var model = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")))
        { FolderPath = Path.Combine(folder.Root, "files") };
        await model.ScanAsync();
        var rename = model.Rename;
        rename.RuleIndex = (int)RenameRule.ExtractFormat;
        rename.Pattern = ".*";
        rename.Template = "same";
        await rename.PreviewTask;
        Assert.All(rename.Rows, row =>
        {
            Assert.StartsWith("Conflict", row.Status);
            Assert.False(row.Selected);
        });
        Assert.False(rename.CanApply);
        var first = rename.Rows[0];
        first.Selected = true;
        await rename.PreviewTask;
        Assert.False(rename.CanApply);
        Assert.True(rename.HasConflicts);
        Assert.StartsWith("Conflict:", rename.Status);
        first.ProposedName = "../outside.txt";
        await rename.PreviewTask;
        Assert.False(rename.CanApply);
        Assert.Contains("Invalid", first.Status);
        first.ProposedName = "corrected.txt";
        await rename.PreviewTask;
        Assert.True(rename.CanApply);
        Assert.Equal("corrected.txt", Assert.Single(rename.SelectedRequests()).NewName);
        rename.Pattern = "[";
        await rename.PreviewTask;
        Assert.Empty(rename.Rows);
        Assert.False(rename.CanApply);
        Assert.Contains("Preview failed", rename.Status);
    });

    [Fact]
    public Task UncheckedCaseInsensitiveConflictsBlockTheEntireBatchAndShowAMessage() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var a = folder.Write(@"files\duplicate-a.txt", "a");
        var b = folder.Write(@"files\duplicate-b.txt", "b");
        var unique = folder.Write(@"files\unique-c.txt", "c");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var model = new MainViewModel(store) { FolderPath = Path.GetDirectoryName(a)! };
        var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json")), AppTheme.Light, model: model);
        try
        {
            window.Show();
            await model.ScanAsync();
            var rename = model.Rename;
            rename.RuleIndex = (int)RenameRule.ExtractFormat;
            rename.Pattern = @"^(?<word>.*?)-[abc]$";
            rename.Template = "{word}";
            await rename.PreviewTask;
            ((TabItem)window.FindName("RenameTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = (RenameView)window.FindName("RenamePanel");
            Assert.StartsWith("Conflict:", ((TextBlock)view.FindName("RenameStatusText")).Text);
            Assert.False(((Button)view.FindName("ApplyRenameButton")).IsEnabled);
            var conflicting = rename.Rows.Where(row => row.Status.StartsWith("Conflict:", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, conflicting.Length);
            Assert.All(conflicting, row => Assert.False(row.Selected));
            var ready = Assert.Single(rename.Rows, row => row.Status == "Ready");
            Assert.True(ready.Selected);
            ready.Selected = false;
            ready.Selected = true;
            await rename.PreviewTask;
            conflicting[0].ProposedName = "DUPLICATE.txt";
            await rename.PreviewTask;
            Assert.True(rename.HasConflicts);
            Assert.False(rename.CanApply);
            Assert.Contains("Conflict:", Assert.Throws<InvalidOperationException>(() => rename.SelectedRequests()).Message);
            await model.ApplyRenameAsync();
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
            Assert.True(File.Exists(unique));
            Assert.Null(store.LoadJournal());
            conflicting[0].ProposedName = "resolved.txt";
            await rename.PreviewTask;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(rename.HasConflicts);
            Assert.True(rename.CanApply);
            Assert.True(((Button)view.FindName("ApplyRenameButton")).IsEnabled);
            Assert.DoesNotContain("Conflict:", ((TextBlock)view.FindName("RenameStatusText")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ApplyRefreshesAllResultsAndUndoWorksInNewViewModelWithoutScan() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"files\old.txt", "unchanged content");
        folder.Write(@"files\other.txt", "unchanged content");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var model = new MainViewModel(store) { FolderPath = Path.GetDirectoryName(path)! };
        await model.ScanAsync();
        await model.FindAsync();
        model.LargeFileSize = "0";
        await model.FindLargeFilesAsync();
        model.Rename.Find = "old";
        model.Rename.Replace = "new";
        await model.Rename.PreviewTask;
        await model.ApplyRenameAsync();
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "new.txt")));
        Assert.False(File.Exists(path));
        Assert.Empty(model.DuplicateRows);
        Assert.Empty(model.LargeFiles);
        Assert.Contains(model.Rows, row => row.Name == "new.txt");
        Assert.True(model.Rename.CanUndo);
        var reopened = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")));
        Assert.True(reopened.Rename.CanUndo);
        await reopened.UndoRenameAsync();
        Assert.Equal("unchanged content", File.ReadAllText(path));
        Assert.False(reopened.Rename.CanUndo);
        Assert.Contains(reopened.Rows, row => row.Name == "old.txt");
        Assert.Contains("1 files changed", reopened.Status);
    });

    [Fact]
    public Task PresetsPersistAndCorruptHistoryBlocksRenamingWithoutBeingOverwritten() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"files\old.txt", "content");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var model = new MainViewModel(store) { FolderPath = Path.GetDirectoryName(path)! };
        await model.ScanAsync();
        model.Rename.Find = "old";
        model.Rename.Replace = "saved";
        model.Rename.PresetName = "My filenames";
        model.Rename.SavePreset();
        Assert.Contains("saved", model.Rename.PresetStatus);
        var reloaded = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")));
        reloaded.Rename.SelectedPreset = "My filenames";
        Assert.Equal("old", reloaded.Rename.Find);
        Assert.Equal("saved", reloaded.Rename.Replace);
        Directory.CreateDirectory(Path.GetDirectoryName(store.JournalPath)!);
        File.WriteAllText(store.JournalPath, "{broken");
        var blocked = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")))
        { FolderPath = Path.GetDirectoryName(path)! };
        await blocked.ScanAsync();
        blocked.Rename.SelectedPreset = "My filenames";
        await blocked.Rename.PreviewTask;
        Assert.False(blocked.Rename.CanApply);
        Assert.False(blocked.Rename.CanUndo);
        Assert.Contains("Cannot read undo history", blocked.Rename.HistoryStatus);
        Assert.Equal("{broken", File.ReadAllText(store.JournalPath));
        Assert.True(File.Exists(path));
    });

    [Theory]
    [InlineData(false, 1240)]
    [InlineData(true, 1240)]
    [InlineData(false, 820)]
    [InlineData(true, 820)]
    public Task RenameTabHasLiveControlsAndFitsAllRulePanels(bool dark, double width) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"files\old.txt", "content");
        var model = new MainViewModel(new RenameStore(Path.Combine(folder.Root, "state")));
        var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json")),
            dark ? AppTheme.Dark : AppTheme.Light, model: model) { Width = width, Height = 640 };
        try
        {
            window.Show();
            var tab = (TabItem)window.FindName("RenameTab");
            var tabs = (TabControl)ItemsControl.ItemsControlFromItemContainer(tab);
            Assert.Equal(tabs.Items.IndexOf(window.FindName("EmptyFoldersTab")) + 1, tabs.Items.IndexOf(tab));
            tab.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = (RenameView)window.FindName("RenamePanel");
            var apply = (Button)view.FindName("ApplyRenameButton");
            var undo = (Button)view.FindName("UndoRenameButton");
            var move = (Button)view.FindName("MoveConflictsButton");
            var detect = (Button)view.FindName("DetectPatternButton");
            Assert.False(apply.IsEnabled);
            Assert.False(undo.IsEnabled);
            Assert.False(move.IsEnabled);
            Assert.False(detect.IsEnabled);
            model.FolderPath = Path.GetDirectoryName(path)!;
            await model.ScanAsync();
            ((TextBox)view.FindName("RenameFindBox")).Text = "old";
            model.Rename.Replace = "new";
            await model.Rename.PreviewTask;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(apply.IsEnabled);
            Assert.Equal("new.txt", Assert.Single(((DataGrid)view.FindName("RenameGrid")).Items.Cast<RenameRow>()).ProposedName);
            var content = (FrameworkElement)window.Content;
            for (var index = 0; index < 4; index++)
            {
                ((ComboBox)view.FindName("RenameRuleBox")).SelectedIndex = index;
                await model.Rename.PreviewTask;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var grid = (DataGrid)view.FindName("RenameGrid");
                Assert.True(grid.ActualHeight >= 68, $"Rule {index}, grid height {grid.ActualHeight}");
                if (index == 3)
                {
                    var rule = (ComboBox)view.FindName("RenameRuleBox");
                    var pattern = (TextBox)view.FindName("RenamePatternBox");
                    var format = (TextBox)view.FindName("RenameFormatBox");
                    Assert.True(rule.ActualWidth >= 220);
                    Assert.True(pattern.ActualWidth >= (width >= 1240 ? 200 : 100), $"Pattern width: {pattern.ActualWidth}");
                    Assert.True(format.ActualWidth >= (width >= 1240 ? 200 : 100), $"Format width: {format.ActualWidth}");
                    foreach (var box in new[] { pattern, format })
                    {
                        var bounds = box.TransformToAncestor(content).TransformBounds(new Rect(box.RenderSize));
                        Assert.True(new Rect(content.RenderSize).Contains(bounds));
                    }
                    var before = pattern.ActualWidth;
                    window.Width += 200;
                    window.UpdateLayout();
                    Assert.True(pattern.ActualWidth > before);
                    window.Width = width;
                    window.UpdateLayout();
                }
                foreach (var button in new[] { apply, undo, move, detect })
                {
                    var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(new Rect(content.RenderSize).Contains(bounds));
                }
            }
        }
        finally { window.Close(); }
    });
}
