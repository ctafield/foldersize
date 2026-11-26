using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSize.App;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public sealed record AppSettings
{
    public required AppTheme Theme { get; init; }
    public bool OpenWithDoubleClick { get; init; } = true;
    public bool IncludeSubfolders { get; init; } = true;
    public bool StartScanningOnOpen { get; init; } = true;
    public WindowPlacement? WindowPlacement { get; init; }
}

public sealed class ThemeSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AppTheme>(allowIntegerValues: false) }
    };

    public string FilePath { get; }

    public ThemeSettingsStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FolderSize", "settings.json"));
    }

    public AppTheme Load() => LoadSettings().Theme;

    public AppSettings LoadSettings()
    {
        string json;
        try { json = File.ReadAllText(FilePath); }
        catch (FileNotFoundException) { return new() { Theme = AppTheme.System }; }
        catch (DirectoryNotFoundException) { return new() { Theme = AppTheme.System }; }

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        if (settings is null || !Enum.IsDefined(settings.Theme))
            throw new InvalidDataException("The saved theme is not supported.");
        settings.WindowPlacement?.Validate();
        return settings;
    }

    public void Save(AppTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        Save(LoadSettings() with { Theme = theme });
    }

    public void Save(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme)) throw new ArgumentOutOfRangeException(nameof(settings));
        settings.WindowPlacement?.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

}
