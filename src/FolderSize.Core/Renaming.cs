namespace FolderSize.Core;

public sealed record RenameRequest(string OriginalPath, string NewName, long Length, DateTime LastWriteUtc);
public sealed record RenameResult(int Changed, IReadOnlyList<FileIssue> Issues);

public sealed class Renaming(IFileAccess access, RenameStore store)
{
    public RenameResult Apply(IReadOnlyList<RenameRequest> requests, string root, bool recursive, Guid? previousId,
        CancellationToken token = default)
    {
        using var locked = store.Lock();
        CheckHistory(previousId);
        var entries = new List<RenameEntry>();
        var issues = new List<FileIssue>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(Path.GetDirectoryName(request.OriginalPath)!, request.NewName);
            if (!RenameStore.ValidPair(request.OriginalPath, target, root) ||
                !sources.Add(request.OriginalPath) || !targets.Add(target))
                throw new ArgumentException("Invalid or conflicting rename selection.");
            try
            {
                using var lease = access.Open(request.OriginalPath);
                if (lease.Stamp.Length != request.Length || lease.Stamp.LastWriteUtc != request.LastWriteUtc)
                    throw new IOException("File changed since the scan; scan again.");
                CheckDestination(request.OriginalPath, target);
                entries.Add(new(request.OriginalPath, target, lease.Stamp));
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex)) { issues.Add(new(request.OriginalPath, ex.Message)); }
        }
        if (entries.Count == 0) return new(0, issues);
        token.ThrowIfCancellationRequested();
        // Write the complete intent before any move. Undo reconciles partial work after a crash or cancellation.
        store.SaveJournal(new(1, Guid.NewGuid(), DateTime.UtcNow, root, recursive, entries));
        var changed = 0;
        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested)
            {
                issues.Add(new(entry.OriginalPath, "Renaming cancelled; remaining files were not processed."));
                break;
            }
            try
            {
                Verify(entry.OriginalPath, entry.Stamp);
                CheckDestination(entry.OriginalPath, entry.NewPath);
                token.ThrowIfCancellationRequested();
                File.Move(entry.OriginalPath, entry.NewPath, overwrite: false);
                changed++;
            }
            catch (OperationCanceledException)
            {
                issues.Add(new(entry.OriginalPath, "Renaming cancelled; remaining files were not processed."));
                break;
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex)) { issues.Add(new(entry.OriginalPath, ex.Message)); }
        }
        return new(changed, issues);
    }

    public RenameResult Undo(Guid expectedId, CancellationToken token = default)
    {
        using var locked = store.Lock();
        var journal = CheckHistory(expectedId) ?? throw new InvalidOperationException("There is no rename to undo.");
        var remaining = new List<RenameEntry>();
        var issues = new List<FileIssue>();
        var changed = 0;
        foreach (var entry in journal.Entries)
        {
            if (token.IsCancellationRequested)
            {
                remaining.Add(entry);
                continue;
            }
            try
            {
                var caseOnly = string.Equals(entry.OriginalPath, entry.NewPath, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && File.Exists(entry.OriginalPath) && !File.Exists(entry.NewPath))
                {
                    Verify(entry.OriginalPath, entry.Stamp);
                    continue;
                }
                CheckDestination(entry.NewPath, entry.OriginalPath);
                Verify(entry.NewPath, entry.Stamp);
                token.ThrowIfCancellationRequested();
                File.Move(entry.NewPath, entry.OriginalPath, overwrite: false);
                changed++;
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is OperationCanceledException)
            {
                remaining.Add(entry);
                issues.Add(new(entry.NewPath, ex is OperationCanceledException ? "Undo cancelled." : ex.Message));
            }
        }
        if (token.IsCancellationRequested)
            issues.Add(new(journal.RootPath, "Undo cancelled; remaining entries are kept for another attempt."));
        store.SaveJournal(remaining.Count == 0 ? null : journal with { Entries = remaining });
        return new(changed, issues);
    }

    private RenameJournal? CheckHistory(Guid? expectedId)
    {
        var journal = store.LoadJournal();
        if (journal?.Id != expectedId)
            throw new IOException("Rename history changed in another window or process. Review the refreshed history and try again.");
        return journal;
    }

    private void Verify(string path, FileStamp expected)
    {
        using var lease = access.Open(path);
        if (lease.Stamp != expected) throw new IOException("File changed or was replaced; it was not renamed.");
    }

    private static void CheckDestination(string source, string destination)
    {
        if ((File.Exists(destination) || Directory.Exists(destination)) &&
            !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Destination already exists; nothing was overwritten.");
    }
}
