using System.IO;
using System.Text.Json;
using FolderSize.App;

namespace FolderSize.Tests;

public sealed class ThemeSettingsTests
{
    [Fact]
    public void StartupScanningDefaultsToEnabledAndPersistsAcrossThemeChanges()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "settings.json");
        var store = new ThemeSettingsStore(path);
        Assert.True(store.LoadSettings().StartScanningOnOpen);
        File.WriteAllText(path, """{"Theme":"Dark","IncludeSubfolders":false}""");
        Assert.True(store.LoadSettings().StartScanningOnOpen);
        foreach (var enabled in new[] { false, true })
        {
            store.Save(store.LoadSettings() with { StartScanningOnOpen = enabled });
            store.Save(AppTheme.Light);
            var restored = new ThemeSettingsStore(path).LoadSettings();
            Assert.Equal(enabled, restored.StartScanningOnOpen);
            Assert.False(restored.IncludeSubfolders);
            Assert.Equal(AppTheme.Light, restored.Theme);
        }
    }

    [Fact]
    public void ExistingSettingsDefaultToRecursiveAndThemeChangesPreserveScanChoice()
    {
        using var folder = new TestFolder();
        var path = folder.Write("settings.json", """{"Theme":"Dark","OpenWithDoubleClick":false}""");
        var store = new ThemeSettingsStore(path);
        Assert.True(store.LoadSettings().IncludeSubfolders);
        store.Save(store.LoadSettings() with { IncludeSubfolders = false });
        store.Save(AppTheme.Light);
        var restored = new ThemeSettingsStore(path).LoadSettings();
        Assert.False(restored.IncludeSubfolders);
        Assert.False(restored.OpenWithDoubleClick);
        Assert.Equal(AppTheme.Light, restored.Theme);
    }

    [Theory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ThemePersistsAcrossStoreInstances(AppTheme theme)
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "settings", "settings.json");
        var store = new ThemeSettingsStore(path);
        Assert.Equal(AppTheme.System, store.Load());
        store.Save(AppTheme.Dark);
        store.Save(theme);
        Assert.Equal(theme, new ThemeSettingsStore(path).Load());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"Theme":"Sepia"}""")]
    [InlineData("""{"Theme":2}""")]
    public void InvalidSavedThemeIsReportedWithoutOverwritingIt(string json)
    {
        using var folder = new TestFolder();
        var path = folder.Write("settings.json", json);
        Assert.Throws<JsonException>(() => new ThemeSettingsStore(path).Load());
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void FailedSavePreservesPreviousSettingsAndCleansTemporaryFile()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "settings.json");
        var store = new ThemeSettingsStore(path);
        store.Save(AppTheme.Light);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => store.Save(AppTheme.Dark));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(AppTheme.Light, new ThemeSettingsStore(path).Load());
        Assert.Single(Directory.GetFiles(folder.Root));
    }
}
