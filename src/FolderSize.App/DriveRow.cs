using System.IO;
using FolderSize.Core;

namespace FolderSize.App;

public sealed record DriveRow(string Path, string Label, DriveType Type, long? TotalBytes, long? FreeBytes,
    string? Error = null)
{
    public string Name => string.IsNullOrWhiteSpace(Label) ? Path : $"{Label} ({Path})";
    public bool CanScan => TotalBytes.HasValue && FreeBytes.HasValue && Error is null;
    public double UsedPercent => TotalBytes is > 0 && FreeBytes is { } free
        ? (double)(TotalBytes.Value - free) / TotalBytes.Value * 100 : 0;
    public bool IsLowOnSpace => CanScan && TotalBytes is > 0 && FreeBytes is { } free
        && free < TotalBytes.Value / 10m;
    public string SpaceText => TotalBytes is { } total && FreeBytes is { } free && Error is null
        ? $"{SizeText.Format(total - free)} used\n{SizeText.Format(free)} free"
        : Error ?? "Not ready (no media)";
    public string TypeText => Type switch
    {
        DriveType.Fixed => "Local drive",
        DriveType.Removable => "Removable drive",
        DriveType.Network => "Network drive",
        DriveType.CDRom => "CD/DVD drive",
        DriveType.Ram => "RAM drive",
        _ => "Drive"
    };
    public string Icon => Type switch
    {
        DriveType.Removable => "\uE88E",
        DriveType.Network => "\uE968",
        DriveType.CDRom => "\uE958",
        DriveType.Ram => "\uE950",
        _ => "\uEDA2"
    };
    public string ToolTip => $"{Name}\n{TypeText}\n{SpaceText}" +
        (CanScan && TotalBytes is { } total ? $"\n{SizeText.Format(total)} total\nDouble-click or press Enter to scan" : "");

    public static IReadOnlyList<DriveRow> ReadAll() => DriveInfo.GetDrives()
        .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase).Select(Read).ToArray();

    private static DriveRow Read(DriveInfo drive)
    {
        var type = DriveType.Unknown;
        try
        {
            type = drive.DriveType;
            if (!drive.IsReady) return new(drive.Name, "", type, null, null);
            return new(drive.Name, drive.VolumeLabel, type, drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex))
        {
            return new(drive.Name, "", type, null, null, $"Unavailable: {ex.Message}");
        }
    }
}
