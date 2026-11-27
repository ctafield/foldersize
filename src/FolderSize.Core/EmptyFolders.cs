namespace FolderSize.Core;

public sealed record EmptyFolderMatch(ScanNode Node, int EmptySubfolderCount);

public static class EmptyFolderFinder
{
    public static IReadOnlyList<EmptyFolderMatch> Find(ScanResult scan, CancellationToken cancellationToken = default)
    {
        var emptyTrees = new Dictionary<ScanNode, int>();
        var pending = new Stack<(ScanNode Node, bool Aggregate)>();
        pending.Push((scan.Root, false));
        while (pending.TryPop(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!work.Aggregate && RecycleBinPaths.IsWithin(work.Node.FullPath)) continue;
            if (!work.Aggregate)
            {
                pending.Push((work.Node, true));
                foreach (var child in work.Node.Children)
                    if (child.IsDirectory) pending.Push((child, false));
                continue;
            }
            if (!work.Node.IsScanned || work.Node.HasErrors) continue;
            var empty = true;
            var subfolders = 0;
            foreach (var child in work.Node.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!child.IsDirectory || !emptyTrees.TryGetValue(child, out var descendants))
                {
                    empty = false;
                    break;
                }
                subfolders = checked(subfolders + descendants + 1);
            }
            if (empty) emptyTrees.Add(work.Node, subfolders);
        }

        var matches = new List<EmptyFolderMatch>();
        var folders = new Stack<ScanNode>(scan.Root.Children.Where(child => child.IsDirectory));
        while (folders.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RecycleBinPaths.IsWithin(folder.FullPath)) continue;
            if (emptyTrees.TryGetValue(folder, out var subfolders))
                matches.Add(new(folder, subfolders));
            else
                foreach (var child in folder.Children)
                    if (child.IsDirectory) folders.Push(child);
        }
        var sorted = matches.OrderBy(match => match.Node.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return sorted;
    }
}
