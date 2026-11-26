using System.Diagnostics;
using System.IO.Enumeration;

namespace FolderSize.Core;

public sealed class ScanNode(string name, ScanNode? parent, bool isDirectory)
{
    public string Name { get; } = name;
    public ScanNode? Parent { get; } = parent;
    public bool IsDirectory { get; } = isDirectory;
    public bool IsScanned { get; internal set; }
    public bool HasErrors { get; internal set; }
    public long Size { get; internal set; }
    public DateTime LastWriteUtc { get; internal set; }
    public List<ScanNode> Children { get; } = [];
    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    public IEnumerable<ScanNode> Files()
    {
        var pending = new Stack<ScanNode>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            if (!node.IsDirectory) yield return node;
            else foreach (var child in node.Children) pending.Push(child);
        }
    }
}

public sealed record FileIssue(string Path, string Message);
public sealed record WorkProgress(long Files, long Bytes, string Phase);
public sealed record ScanResult(ScanNode Root, long FileCount, IReadOnlyList<FileIssue> Issues);

public static class FileErrors
{
    public static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}

public sealed class FolderScanner
{
    public ScanResult Scan(string path, bool recursive, IProgress<WorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var rootInfo = new DirectoryInfo(path);
        if (!rootInfo.Exists) throw new DirectoryNotFoundException($"Folder not found: {path}");
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose a real folder, not a junction or symbolic link.");

        var root = new ScanNode(path, null, true);
        var pending = new Stack<(ScanNode Node, bool Aggregate)>();
        var issues = new List<FileIssue>();
        long count = 0, bytes = 0;
        var firstReport = true;
        var timer = Stopwatch.StartNew();
        pending.Push((root, false));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 65536
        };
        while (pending.TryPop(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = work.Node;
            if (work.Aggregate)
            {
                foreach (var child in node.Children)
                {
                    node.Size = checked(node.Size + child.Size);
                    node.HasErrors |= child.HasErrors;
                }
                continue;
            }
            pending.Push((node, true));
            node.IsScanned = true;
            try
            {
                var entries = new FileSystemEnumerable<ScanNode?>(node.FullPath, (ref FileSystemEntry entry) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            issues.Add(new(entry.ToFullPath(), "Skipped junction or symbolic link."));
                            node.HasErrors = true;
                            return null;
                        }
                        return new ScanNode(entry.FileName.ToString(), node, entry.IsDirectory)
                        {
                            LastWriteUtc = entry.LastWriteTimeUtc.UtcDateTime,
                            Size = entry.IsDirectory ? 0 : entry.Length,
                            IsScanned = !entry.IsDirectory
                        };
                    }
                    catch (Exception ex) when (FileErrors.IsExpected(ex))
                    {
                        issues.Add(new(entry.ToFullPath(), ex.Message));
                        node.HasErrors = true;
                        return null;
                    }
                }, options);
                foreach (var child in entries)
                {
                    if (child is null) continue;
                    if (!child.IsDirectory)
                    {
                        count++;
                        bytes += child.Size;
                    }
                    node.Children.Add(child);
                    if (child.IsDirectory && recursive) pending.Push((child, false));
                    if (firstReport || timer.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(count, bytes, "Scanning"));
                        firstReport = false;
                        timer.Restart();
                    }
                }
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex))
            {
                issues.Add(new(node.FullPath, ex.Message));
                node.HasErrors = true;
            }
        }
        progress?.Report(new(count, root.Size, "Scan complete"));
        return new(root, count, issues);
    }
}

public static class SizeText
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N2} {units[unit]}";
    }
}
