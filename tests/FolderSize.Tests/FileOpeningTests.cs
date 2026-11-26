using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FolderSize.App;

namespace FolderSize.Tests;

public sealed class FileOpeningTests
{
    [Theory]
    [InlineData("run.EXE")]
    [InlineData("run.cmd")]
    [InlineData("run.ps1")]
    [InlineData("run.js")]
    [InlineData("run.msi")]
    [InlineData("run.lnk")]
    [InlineData("run.url")]
    [InlineData("run.scr")]
    public void ExecutablesScriptsInstallersAndShortcutsAreNeverLaunched(string name)
    {
        using var folder = new TestFolder();
        var path = folder.Write(name, "content");
        var opener = new FileOpener(_ => Assert.Fail("Blocked file must not be launched."));
        Assert.Throws<InvalidOperationException>(() => opener.Open(path));
    }

    [Fact]
    public void RenamedExecutablesAreBlockedAndOrdinaryFilesUseShellDefaultAction()
    {
        using var folder = new TestFolder();
        var binary = folder.Write("renamed.txt", "MZnot-a-document");
        var document = folder.Write("document with spaces.txt", "text");
        ProcessStartInfo? launched = null;
        var opener = new FileOpener(info => launched = info);
        Assert.Throws<InvalidOperationException>(() => opener.Open(binary));
        Assert.Null(launched);
        opener.Open(document);
        Assert.NotNull(launched);
        Assert.Equal(document, launched.FileName);
        Assert.True(launched.UseShellExecute);
        Assert.Equal("", launched.Verb);
        Assert.Equal("", launched.Arguments);
        Assert.Throws<FileNotFoundException>(() => opener.Open(Path.Combine(folder.Root, "missing.txt")));
    }

    [Fact]
    public void ExistingThemeSettingsDefaultToEnabledAndThemeChangesPreserveFileOpeningChoice()
    {
        using var folder = new TestFolder();
        var path = folder.Write("settings.json", """{"Theme":"Dark"}""");
        var store = new ThemeSettingsStore(path);
        Assert.True(store.LoadSettings().OpenWithDoubleClick);
        store.Save(store.LoadSettings() with { OpenWithDoubleClick = false });
        store.Save(AppTheme.Light);
        var restored = new ThemeSettingsStore(path).LoadSettings();
        Assert.Equal(AppTheme.Light, restored.Theme);
        Assert.False(restored.OpenWithDoubleClick);
    }

    [Fact]
    public Task SettingAppliesImmediatelyToAllListsAndFailedSaveRevertsChoice() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write(@"scan\a.txt", "same");
        folder.Write(@"scan\b.txt", "same");
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "preferences", "settings.json"));
        store.Save(AppTheme.Light);
        var opened = new List<string>();
        var window = new MainWindow(store, AppTheme.Light, fileOpener: new FileOpener(info => opened.Add(info.FileName)));
        try
        {
            window.Show();
            window.Model.FolderPath = Path.Combine(folder.Root, "scan");
            await window.Model.ScanAsync();
            await window.Model.FindAsync();
            window.Model.LargeFileSize = "0";
            await window.Model.FindLargeFilesAsync();
            var tabs = (TabControl)ItemsControl.ItemsControlFromItemContainer((TabItem)window.FindName("DuplicatesTab"));
            foreach (var enabled in new[] { true, false })
            {
                if (!enabled)
                {
                    var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        SettingsWindow? dialog = null;
                        try
                        {
                            dialog = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows.Cast<Window>()));
                            var box = (CheckBox)dialog.FindName("OpenWithDoubleClickBox");
                            Assert.True(box.IsChecked);
                            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                box.IsChecked = false;
                            Assert.True(box.IsChecked);
                            Assert.Contains("Could not save", ((TextBlock)dialog.FindName("SaveStatus")).Text);
                            box.IsChecked = false;
                            Assert.False(store.LoadSettings().OpenWithDoubleClick);
                            handled.SetResult();
                        }
                        catch (Exception ex) { handled.SetException(ex); }
                        finally { dialog?.Close(); }
                    }));
                    ((MenuItem)window.FindName("AppearanceMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await handled.Task;
                }
                opened.Clear();
                var names = new[] { "UsageGrid", "DuplicatesGrid", "LargeFilesGrid" };
                for (var index = 0; index < names.Length; index++)
                {
                    tabs.SelectedIndex = index;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var grid = (DataGrid)window.FindName(names[index]);
                    var column = grid.Columns.First(item => item is not DataGridCheckBoxColumn);
                    var content = column.GetCellContent(grid.Items[0]);
                    Assert.NotNull(content);
                    grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent, Source = content
                    });
                    foreach (var check in grid.Columns.OfType<DataGridCheckBoxColumn>())
                    {
                        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        {
                            RoutedEvent = Control.MouseDoubleClickEvent, Source = check.GetCellContent(grid.Items[0])
                        });
                    }
                }
                Assert.Equal(enabled ? 3 : 0, opened.Count);
                Assert.All(opened, path => Assert.EndsWith(".txt", path));
            }
        }
        finally { window.Close(); }
    });
}
