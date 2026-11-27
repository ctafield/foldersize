namespace FolderSize.Core;

public sealed class FolderRecycling(IFileAccess access, IRecycler recycler)
{
    public RecycleResult Recycle(ScanNode folder, CancellationToken cancellationToken = default)
    {
        if (!folder.IsDirectory || folder.Parent is null)
            throw new ArgumentException("Choose a scanned subfolder, not a file or scan root.", nameof(folder));
        var path = folder.FullPath;
        try
        {
            if (!folder.IsScanned || folder.HasErrors)
                throw new IOException("Scan this folder recursively and resolve any Issues before removing it.");
            var current = new FolderScanner().Scan(path, true, cancellationToken: cancellationToken);
            if (current.Root.HasErrors) return new([], current.Issues);
            var pending = new Stack<(ScanNode Expected, ScanNode Current)>();
            pending.Push((folder, current.Root));
            while (pending.TryPop(out var pair))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.Expected.IsDirectory != pair.Current.IsDirectory)
                    throw new IOException("Folder contents changed since the scan; scan again before removal.");
                if (pair.Expected.IsDirectory)
                {
                    if (!pair.Expected.IsScanned || pair.Expected.HasErrors ||
                        pair.Expected.Children.Count != pair.Current.Children.Count)
                        throw new IOException("Folder contents changed or were not fully scanned; scan again before removal.");
                    var children = pair.Current.Children.ToDictionary(child => child.Name, StringComparer.Ordinal);
                    foreach (var child in pair.Expected.Children)
                    {
                        if (!children.TryGetValue(child.Name, out var actual))
                            throw new IOException("Folder contents changed since the scan; scan again before removal.");
                        pending.Push((child, actual));
                    }
                }
                else
                {
                    using var lease = access.Open(pair.Expected.FullPath);
                    if (lease.Stamp.Length != pair.Expected.Size || lease.Stamp.LastWriteUtc != pair.Expected.LastWriteUtc)
                        throw new IOException("File changed since the scan; scan again before removing the folder.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            recycler.Recycle(path);
            return new([path], []);
        }
        catch (OperationCanceledException)
        {
            return new([], [new(path, "Folder removal cancelled; nothing was removed.")]);
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex))
        {
            return new([], [new(path, ex.Message)]);
        }
    }
}
