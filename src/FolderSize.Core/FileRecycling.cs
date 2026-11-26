namespace FolderSize.Core;

public sealed class FileRecycling(IFileAccess access, IRecycler recycler)
{
    public RecycleResult Recycle(IReadOnlyList<ScanNode> files, CancellationToken cancellationToken = default)
    {
        if (files.Any(file => file.IsDirectory))
            throw new ArgumentException("Only individual files can be recycled.", nameof(files));

        var recycled = new List<string>();
        var issues = new List<FileIssue>();
        foreach (var file in files.DistinctBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var path = file.FullPath;
            if (cancellationToken.IsCancellationRequested)
            {
                issues.Add(new(path, "Recycling cancelled; remaining files were not processed."));
                break;
            }
            try
            {
                using (var lease = access.Open(path))
                {
                    if (lease.Stamp.Length != file.Size || lease.Stamp.LastWriteUtc != file.LastWriteUtc)
                        throw new IOException("File changed since the scan; scan again before recycling.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                recycler.Recycle(path);
                recycled.Add(path);
            }
            catch (OperationCanceledException)
            {
                issues.Add(new(path, "Recycling cancelled; remaining files were not processed."));
                break;
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex))
            {
                issues.Add(new(path, ex.Message));
            }
        }
        return new(recycled, issues);
    }
}
