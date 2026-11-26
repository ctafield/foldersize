using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSize.Core;

public sealed record RenameEntry(string OriginalPath, string NewPath, FileStamp Stamp);
public sealed record RenameJournal(int Version, Guid Id, DateTime CreatedUtc, string RootPath, bool Recursive,
    IReadOnlyList<RenameEntry> Entries);

public sealed class RenameStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<RenameRule>(allowIntegerValues: false),
            new JsonStringEnumConverter<RenameCase>(allowIntegerValues: false) }
    };
    public string JournalPath { get; } = Path.Combine(Path.GetFullPath(directory), "rename-undo.json");
    public string PresetsPath { get; } = Path.Combine(Path.GetFullPath(directory), "rename-presets.json");

    public FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        return new FileStream(JournalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public RenameJournal? LoadJournal()
    {
        var journal = Read<RenameJournal>(JournalPath);
        if (journal is null) return null;
        if (journal.Version != 1 || journal.Id == Guid.Empty || string.IsNullOrEmpty(journal.RootPath) || !Path.IsPathFullyQualified(journal.RootPath) ||
            journal.Entries is null || journal.Entries.Any(entry => entry is null || entry.Stamp is null ||
                entry.Stamp.Length < 0 || string.IsNullOrEmpty(entry.Stamp.Identity) ||
                !ValidPair(entry.OriginalPath, entry.NewPath, journal.RootPath)))
            throw new InvalidDataException("The saved rename history is invalid or from an unsupported version.");
        return journal;
    }

    internal static bool ValidPair(string original, string target, string root) =>
        !string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(root) &&
        Path.IsPathFullyQualified(original) && Path.IsPathFullyQualified(target) &&
        original != target &&
        string.Equals(Path.GetDirectoryName(original), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) &&
        RenameRules.NameError(original, Path.GetFileName(target)) is null &&
        !Path.GetRelativePath(root, original).Split(Path.DirectorySeparatorChar).Contains("..") &&
        !Path.IsPathRooted(Path.GetRelativePath(root, original));

    public void SaveJournal(RenameJournal? journal) => Write(JournalPath, journal);

    public IReadOnlyDictionary<string, RenameOptions> LoadPresets()
    {
        var presets = Read<Dictionary<string, RenameOptions>>(PresetsPath) ?? new Dictionary<string, RenameOptions>();
        if (presets.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                !Enum.IsDefined(pair.Value.Rule) || !Enum.IsDefined(pair.Value.Case)))
            throw new InvalidDataException("Saved rename presets are invalid.");
        return presets;
    }

    public void SavePreset(string name, RenameOptions options)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("Enter a preset name of 1 to 80 characters.");
        using var locked = Lock();
        var presets = new Dictionary<string, RenameOptions>(LoadPresets(), StringComparer.OrdinalIgnoreCase)
        { [name.Trim()] = options };
        Write(PresetsPath, presets);
    }

    private static T? Read<T>(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
