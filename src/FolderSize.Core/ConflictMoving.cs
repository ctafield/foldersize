using System.Runtime.Versioning;

namespace FolderSize.Core;

public sealed record ConflictMoveResult(IReadOnlyList<string> MovedPaths, IReadOnlyList<FileIssue> Issues);

[SupportedOSPlatform("windows")]
public sealed class ConflictMoving(IFileAccess access)
{
    public ConflictMoveResult Move(IReadOnlyList<ScanNode> files, string destination, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);
        if (files.Any(file => file.IsDirectory ||
            string.Equals(Path.GetDirectoryName(file.FullPath), Path.TrimEndingDirectorySeparator(destination),
                StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a destination different from the source folders.");
        var moved = new List<string>();
        var issues = new List<FileIssue>();
        if (files.Count == 0) return new(moved, issues);
        token.ThrowIfCancellationRequested();
        for (string? parent = destination; parent is not null;
             parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(parent)))
        {
            if (!Directory.Exists(parent)) continue;
            WindowsFileAccess.EnsureNoLinks(parent);
            break;
        }
        Directory.CreateDirectory(destination);
        WindowsFileAccess.EnsureNoLinks(destination);
        foreach (var file in files.DistinctBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (token.IsCancellationRequested)
            {
                issues.Add(new(file.FullPath, "Moving conflicts cancelled; remaining files were not processed."));
                break;
            }
            try
            {
                using (var lease = access.Open(file.FullPath))
                {
                    if (lease.Stamp.Length != file.Size || lease.Stamp.LastWriteUtc != file.LastWriteUtc)
                        throw new IOException("File changed since the scan; scan again before moving.");
                }
                for (var suffix = 0; ; suffix = checked(suffix + 1))
                {
                    token.ThrowIfCancellationRequested();
                    var name = suffix == 0 ? file.Name :
                        $"{Path.GetFileNameWithoutExtension(file.Name)} ({suffix}){Path.GetExtension(file.Name)}";
                    var target = Path.Combine(destination, name);
                    if (File.Exists(target) || Directory.Exists(target)) continue;
                    WindowsFileAccess.EnsureNoLinks(destination);
                    try { File.Move(file.FullPath, target, overwrite: false); }
                    catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { continue; }
                    moved.Add(target);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                issues.Add(new(file.FullPath, "Moving conflicts cancelled; remaining files were not processed."));
                break;
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex)) { issues.Add(new(file.FullPath, ex.Message)); }
        }
        return new(moved, issues);
    }
}
