using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FolderSize.Core;

namespace FolderSize.App;

internal static class FolderPaths
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }
}

public sealed class UsageRow(ScanNode node, long total) : Observable
{
    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public ScanNode Node { get; } = node;
    public string Name => Node.Name;
    public string Kind => Node.IsDirectory ? "Folder" : "File";
    public long Bytes => Node.Size;
    public double Percent => total == 0 ? 0 : (double)Bytes / total * 100;
    public string Size => Node.IsDirectory && !Node.IsScanned ? "Not scanned" :
        SizeText.Format(Bytes) + (Node.HasErrors ? " (partial)" : "");
    public string FullPath => Node.FullPath;
}

public sealed class DuplicateRow(int groupNumber, DuplicateFile file, int copies) : Observable
{
    private bool selected;
    public DuplicateFile File { get; } = file;
    public int GroupNumber { get; } = groupNumber;
    public string Path => File.Path;
    public string Size => SizeText.Format(File.Stamp.Length);
    public int Copies { get; } = copies;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

public sealed class MainViewModel : Observable
{
    public RenameViewModel Rename { get; }
    public MainViewModel(RenameStore? renameStore = null)
    {
        Rename = new(renameStore ?? new RenameStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSize")));
    }

    private string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string status = "Choose a folder to see what is taking up space.";
    private bool recursive = true, busy;
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<UsageRow> rows = [];
    private IReadOnlyList<DuplicateRow> duplicateRows = [];
    private IReadOnlyList<FileIssue> issues = [];
    private ScanNode? current;
    private ScanResult? scan;
    private bool scannedRecursively;
    private DuplicateResult? duplicates;
    private long operationId;
    private long navigationId;
    private readonly List<string> navigationHistory = [];
    private int historyIndex = -1;
    private int selectedCount;
    private SpaceMapGroups mapGroups = new([], []);
    public SpaceMapGroups MapGroups { get => mapGroups; private set => Set(ref mapGroups, value); }
    private string largeFileSize = "100", largeFileUnit = "mb";
    private string largeFilesSummary = "Scan a folder, then enter a minimum size and select Show files.";
    private IReadOnlyList<UsageRow> largeFiles = [];
    private int largeFileSelectedCount;
    public string LargeFileSize
    {
        get => largeFileSize;
        set
        {
            if (largeFileSize == value) return;
            Set(ref largeFileSize, value);
            ClearLargeFiles();
        }
    }
    public string LargeFileUnit
    {
        get => largeFileUnit;
        set
        {
            if (largeFileUnit == value) return;
            Set(ref largeFileUnit, value);
            ClearLargeFiles();
        }
    }
    public IReadOnlyList<UsageRow> LargeFiles
    {
        get => largeFiles;
        private set
        {
            foreach (var row in largeFiles) row.PropertyChanged -= LargeFileSelectionChanged;
            Set(ref largeFiles, value);
            largeFileSelectedCount = largeFiles.Count(row => row.Selected);
            foreach (var row in largeFiles) row.PropertyChanged += LargeFileSelectionChanged;
            Changed(nameof(CanRecycleLargeFiles));
        }
    }
    public bool CanRecycleLargeFiles => CanFind && largeFileSelectedCount > 0;
    private void LargeFileSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not UsageRow row || args.PropertyName != nameof(UsageRow.Selected)) return;
        largeFileSelectedCount += row.Selected ? 1 : -1;
        Changed(nameof(CanRecycleLargeFiles));
    }
    public string LargeFilesSummary { get => largeFilesSummary; private set => Set(ref largeFilesSummary, value); }

    private void ClearLargeFiles()
    {
        LargeFiles = [];
        LargeFilesSummary = "Enter a minimum size and select Show files. Uses the completed scan, not just the current folder.";
    }

    public string FolderPath
    {
        get => folderPath;
        set
        {
            if (folderPath == value) return;
            Set(ref folderPath, value);
            if (Current is null) Changed(nameof(CurrentPath));
        }
    }
    public bool SelectFolder(string path)
    {
        if (Busy) return false;
        try
        {
            FolderPath = FolderPaths.Normalize(path);
            return true;
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is ArgumentException or NotSupportedException)
        {
            Issues = [new(path, ex.Message)];
            Status = $"Could not select the folder: {ex.Message}";
            return false;
        }
    }
    public bool Recursive { get => recursive; set => Set(ref recursive, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Busy
    {
        get => busy;
        private set
        {
            Set(ref busy, value);
            Rename.OperationBusy = value;
            Changed(nameof(Idle));
            Changed(nameof(CanFind));
            Changed(nameof(CanRecycle));
            Changed(nameof(CanRecycleLargeFiles));
            Changed(nameof(CanGoBack));
            Changed(nameof(CanGoForward));
        }
    }
    public bool Idle => !Busy;
    public bool CanFind => Idle && scan is not null;
    public bool CanRecycle => Idle && selectedCount > 0;
    public string CurrentPath => current?.FullPath ?? FolderPath;
    public string Summary => scan is null ? "Logical sizes - not allocated disk space" :
        $"{SizeText.Format(scan.Root.Size)}  |  {scan.FileCount:N0} files  |  logical sizes" +
        (scan.Root.HasErrors ? " (partial; see Issues)" : "");
    public string DuplicateSummary => duplicates is null ? "Run Find duplicates after scanning." :
        $"{duplicates.Groups.Count:N0} groups  |  {SizeText.Format(duplicates.Groups.Sum(group => group.ExtraBytes))} extra logical bytes (not guaranteed disk savings)";
    public IReadOnlyList<UsageRow> Rows { get => rows; private set => Set(ref rows, value); }
    public IReadOnlyList<DuplicateRow> DuplicateRows { get => duplicateRows; private set => Set(ref duplicateRows, value); }
    public IReadOnlyList<FileIssue> Issues
    {
        get => issues;
        private set { Set(ref issues, value); Changed(nameof(IssueHeader)); }
    }
    public string IssueHeader => $"Issues ({Issues.Count:N0})";
    public ScanNode? Current { get => current; private set { Set(ref current, value); Changed(nameof(CurrentPath)); } }

    private Progress<WorkProgress> Progress(long id) => new(value =>
    {
        if (Busy && id == operationId)
            Status = $"{value.Phase}: {value.Files:N0} files, {SizeText.Format(value.Bytes)}";
    });

    public void Cancel() => cancellation?.Cancel();

    private CancellationToken Begin(string message)
    {
        cancellation = new();
        operationId++;
        Busy = true;
        Status = message;
        return cancellation.Token;
    }

    private void End()
    {
        operationId++;
        cancellation?.Dispose();
        cancellation = null;
        Busy = false;
        Changed(nameof(Summary));
        Changed(nameof(DuplicateSummary));
    }

    private void ClearDuplicates()
    {
        foreach (var row in DuplicateRows) row.PropertyChanged -= SelectionChanged;
        duplicates = null;
        selectedCount = 0;
        DuplicateRows = [];
        Changed(nameof(DuplicateSummary));
        Changed(nameof(CanRecycle));
    }

    public async Task ScanAsync()
    {
        if (Busy) return;
        Rename.ReloadHistory();
        var path = FolderPath;
        var previousPath = CurrentPath;
        var recurse = Recursive;
        InvalidateScan();
        Issues = [];
        var token = Begin("Scanning...");
        Changed(nameof(Summary));
        try
        {
            path = FolderPaths.Normalize(path);
            FolderPath = path;
            var progress = Progress(operationId);
            scan = await Task.Run(() => new FolderScanner().Scan(path, recurse, progress, token), token);
            scannedRecursively = recurse;
            Issues = scan.Issues;
            if (historyIndex < 0) RecordNavigation(previousPath);
            await NavigateAsync(scan.Root);
            Status = $"Scan complete. {scan.FileCount:N0} files. {Issues.Count:N0} issues.";
        }
        catch (OperationCanceledException) { Status = "Scan cancelled; no complete results. Scan again to continue."; }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is ArgumentException or NotSupportedException)
        {
            Issues = [new(path, ex.Message)];
            Status = $"Scan failed: {ex.Message}";
        }
        finally { End(); }
    }

    public Task NavigateAsync(ScanNode? node) => NavigateAsync(node, recordHistory: true);

    private async Task NavigateAsync(ScanNode? node, bool recordHistory)
    {
        if (node is null || !node.IsDirectory || !node.IsScanned) return;
        var previousPath = CurrentPath;
        var id = ++navigationId;
        var result = await Task.Run(() =>
        {
            var newRows = node.Children.OrderByDescending(child => child.Size)
                .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => new UsageRow(child, node.Size)).ToArray();
            return (Rows: newRows, Groups: SpaceMapGroups.Create(node));
        });
        if (id != navigationId) return;
        if (recordHistory)
        {
            if (historyIndex < 0) RecordNavigation(previousPath);
            RecordNavigation(node.FullPath);
        }
        Current = node;
        Rows = result.Rows;
        MapGroups = result.Groups;
        Rename.SetScope(node);
    }

    public bool CanGoBack => Idle && historyIndex > 0;
    public bool CanGoForward => Idle && historyIndex + 1 < navigationHistory.Count;
    public Task BackAsync() => NavigateHistoryAsync(-1);
    public Task ForwardAsync() => NavigateHistoryAsync(1);

    private void RecordNavigation(string path)
    {
        path = FolderPaths.Normalize(path);
        if (historyIndex >= 0 &&
            string.Equals(navigationHistory[historyIndex], path, StringComparison.OrdinalIgnoreCase)) return;
        navigationHistory.RemoveRange(historyIndex + 1, navigationHistory.Count - historyIndex - 1);
        navigationHistory.Add(path);
        historyIndex++;
        Changed(nameof(CanGoBack));
        Changed(nameof(CanGoForward));
    }

    private async Task NavigateHistoryAsync(int offset)
    {
        if (Busy) return;
        var index = historyIndex + offset;
        if (index < 0 || index >= navigationHistory.Count) return;
        historyIndex = index;
        Changed(nameof(CanGoBack));
        Changed(nameof(CanGoForward));
        var path = navigationHistory[index];
        var node = scan?.Root;
        if (node is not null)
        {
            var relativePath = Path.GetRelativePath(node.FullPath, path);
            foreach (var part in relativePath.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                node = node.Children.FirstOrDefault(child => child.IsDirectory && child.IsScanned &&
                    string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
                if (node is null) break;
            }
        }
        if (node is not null) await NavigateAsync(node, recordHistory: false);
        else SelectUnscannedFolder(path, "Folder selected. Choose Scan to measure its contents.");
    }

    private void SelectUnscannedFolder(string path, string message)
    {
        InvalidateScan();
        Issues = [];
        FolderPath = path;
        Status = message;
        Changed(nameof(Summary));
        Changed(nameof(CanFind));
    }

    public async Task UpAsync()
    {
        if (Busy) return;
        if (Current?.Parent is { } parent)
        {
            await NavigateAsync(parent);
            return;
        }

        var path = Current?.FullPath ?? FolderPath;
        DirectoryInfo? parentDirectory;
        try
        {
            parentDirectory = Directory.GetParent(FolderPaths.Normalize(path));
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is ArgumentException or NotSupportedException)
        {
            Issues = [new(path, ex.Message)];
            Status = $"Could not navigate to the parent folder: {ex.Message}";
            return;
        }
        if (parentDirectory is null) return;
        if (historyIndex < 0) RecordNavigation(path);
        RecordNavigation(parentDirectory.FullName);
        SelectUnscannedFolder(parentDirectory.FullName, "Parent folder selected. Choose Scan to measure its contents.");
    }
    public Task RootAsync() => NavigateAsync(scan?.Root);

    public async Task FindLargeFilesAsync()
    {
        if (!CanFind || scan is null) return;
        ClearLargeFiles();
        long minimum;
        try { minimum = FileSizeThreshold.Parse(LargeFileSize, LargeFileUnit, CultureInfo.CurrentCulture); }
        catch (ArgumentException ex)
        {
            LargeFilesSummary = ex.Message;
            return;
        }

        var snapshot = scan;
        var token = Begin("Filtering large files...");
        try
        {
            var filtered = await Task.Run(() =>
            {
                var matches = new List<UsageRow>();
                foreach (var file in snapshot.Root.Files())
                {
                    token.ThrowIfCancellationRequested();
                    if (file.Size >= minimum) matches.Add(new(file, snapshot.Root.Size));
                }
                var sorted = matches.OrderByDescending(row => row.Bytes)
                    .ThenBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
                token.ThrowIfCancellationRequested();
                return sorted;
            }, token);
            token.ThrowIfCancellationRequested();
            LargeFiles = filtered;
            LargeFilesSummary = $"{LargeFiles.Count:N0} files at least {SizeText.Format(minimum)} in {snapshot.Root.FullPath}" +
                                (snapshot.Root.HasErrors ? " (partial scan; see Issues)." : ".");
            Status = $"Large file search complete. {LargeFiles.Count:N0} matching files.";
        }
        catch (OperationCanceledException)
        {
            LargeFiles = [];
            LargeFilesSummary = "Large file search cancelled; select Show files to try again.";
            Status = "Large file search cancelled.";
        }
        finally { End(); }
    }

    public async Task FindAsync()
    {
        if (!CanFind || scan is null) return;
        ClearDuplicates();
        var snapshot = scan;
        var token = Begin("Finding duplicate files...");
        try
        {
            var progress = Progress(operationId);
            duplicates = await Task.Run(() => new DuplicateFinder(new WindowsFileAccess()).Find(snapshot, progress, token), token);
            var result = duplicates;
            DuplicateRows = await Task.Run(() => result.Groups.SelectMany((group, index) =>
                group.Files.Select((file, fileIndex) => new DuplicateRow(index + 1, file, group.Files.Count)
                {
                    Selected = fileIndex > 0
                })).ToArray());
            selectedCount = DuplicateRows.Count(row => row.Selected);
            foreach (var row in DuplicateRows) row.PropertyChanged += SelectionChanged;
            Issues = snapshot.Issues.Concat(result.Issues).ToArray();
            Status = $"Duplicate search complete. {result.Groups.Count:N0} exact-content groups.";
        }
        catch (OperationCanceledException) { ClearDuplicates(); Status = "Duplicate search cancelled; no complete duplicate results."; }
        catch (Exception ex) when (FileErrors.IsExpected(ex))
        {
            ClearDuplicates();
            Issues = Issues.Append(new(snapshot.Root.FullPath, ex.Message)).ToArray();
            Status = $"Duplicate search failed: {ex.Message}";
        }
        finally { End(); }
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not DuplicateRow row || args.PropertyName != nameof(DuplicateRow.Selected)) return;
        selectedCount += row.Selected ? 1 : -1;
        Changed(nameof(CanRecycle));
    }

    public IReadOnlyList<DuplicateFile> SelectedFiles() =>
        DuplicateRows.Where(row => row.Selected).Select(row => row.File).ToArray();

    public bool KeepsOneCopy() => duplicates is not null &&
        DuplicateRows.GroupBy(row => row.GroupNumber).All(group => group.Any(row => !row.Selected));

    public async Task RecycleAsync()
    {
        if (!CanRecycle || duplicates is null || scan is null) return;
        var selected = SelectedFiles().Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = duplicates.Groups;
        await RecycleAndRefreshAsync(token => new DuplicateRecycling(new WindowsFileAccess(), new ShellRecycler())
            .Recycle(groups, selected, token), refreshLargeFiles: false);
    }

    public async Task RecycleLargeFilesAsync()
    {
        if (!CanRecycleLargeFiles) return;
        var files = LargeFiles.Where(row => row.Selected).Select(row => row.Node).ToArray();
        await RecycleAndRefreshAsync(token => new FileRecycling(new WindowsFileAccess(), new ShellRecycler())
            .Recycle(files, token), refreshLargeFiles: true, refreshDuplicates: false);
    }

    public Task RecycleItemAsync(ScanNode node)
    {
        if (!CanFind || scan is null) return Task.CompletedTask;
        var root = node;
        while (root.Parent is not null) root = root.Parent;
        if (root != scan.Root || node == scan.Root)
        {
            Issues = Issues.Append(new FileIssue(node.FullPath, "Item is no longer in the current scan. Scan again before recycling.")).ToArray();
            Status = "Nothing recycled; see Issues.";
            return Task.CompletedTask;
        }
        return RecycleAndRefreshAsync(token => node.IsDirectory
            ? new FolderRecycling(new WindowsFileAccess(), new ShellRecycler(allowDirectories: true)).Recycle(node, token)
            : new FileRecycling(new WindowsFileAccess(), new ShellRecycler()).Recycle([node], token),
            refreshLargeFiles: LargeFiles.Count > 0, refreshDuplicates: duplicates is not null);
    }

    public Task RecycleDuplicateAsync(DuplicateRow row)
    {
        if (!CanFind || duplicates is null) return Task.CompletedTask;
        if (!DuplicateRows.Contains(row))
        {
            Issues = Issues.Append(new FileIssue(row.Path, "Duplicate results changed. Find duplicates again before recycling.")).ToArray();
            Status = "Nothing recycled; see Issues.";
            return Task.CompletedTask;
        }
        var groups = duplicates.Groups;
        return RecycleAndRefreshAsync(token => new DuplicateRecycling(new WindowsFileAccess(), new ShellRecycler())
            .Recycle(groups, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Path }, token),
            refreshLargeFiles: LargeFiles.Count > 0, refreshDuplicates: true);
    }

    private async Task RecycleAndRefreshAsync(Func<CancellationToken, RecycleResult> recycle, bool refreshLargeFiles,
        bool refreshDuplicates = true)
    {
        if (scan is null) return;
        var rootPath = scan.Root.FullPath;
        var recurse = scannedRecursively;
        var recycleIssues = new List<FileIssue>();
        RecycleResult? result = null;
        var token = Begin("Checking files and recycling selection...");
        try
        {
            result = await RunStaAsync(() => recycle(token));
            recycleIssues.AddRange(result.Issues);
        }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is InvalidOperationException or ArgumentException)
        {
            recycleIssues.Add(new(rootPath, ex.Message));
        }
        finally
        {
            InvalidateScan();
            End();
        }
        FolderPath = rootPath;
        Recursive = recurse;
        await ScanAsync();
        if (scan is not null)
        {
            if (refreshLargeFiles) await FindLargeFilesAsync();
            if (refreshDuplicates) await FindAsync();
        }
        Issues = Issues.Concat(recycleIssues).ToArray();
        Status = $"Recycling finished: {result?.Recycled.Count ?? 0:N0} files confirmed recycled; " +
                 $"{recycleIssues.Count:N0} recycle issues. " +
                 (scan is null ? "Results stale; scan again." : "Results refreshed.");
    }

    private void InvalidateScan()
    {
        ClearDuplicates();
        ClearLargeFiles();
        scan = null;
        navigationId++;
        Current = null;
        Rows = [];
        MapGroups = new([], []);
        Rename.SetScope(null);
    }

    public Task ApplyRenameAsync() => RunRenameAsync(undo: false);
    public Task UndoRenameAsync() => RunRenameAsync(undo: true);
    public Task MoveConflictsAsync(string destination) => RunRenameAsync(undo: false, destination);

    private async Task RunRenameAsync(bool undo, string? moveDestination = null)
    {
        if (Busy || (moveDestination is not null ? !Rename.CanMoveConflicts : undo ? !Rename.CanUndo : !Rename.CanApply)) return;
        var conflicts = moveDestination is not null ? Rename.ConflictFiles() : [];
        var requests = undo || moveDestination is not null ? [] : Rename.SelectedRequests();
        var root = scan?.Root.FullPath ?? Rename.LastRootPath;
        var recurse = scan is null ? Rename.LastRecursive : scannedRecursively;
        if (root is null) return;
        var action = moveDestination is not null ? "Move conflicts" : undo ? "Undo" : "Rename";
        var token = Begin(moveDestination is not null ? "Moving conflicts..." : undo ? "Undoing last rename..." : "Applying renames...");
        var renameIssues = new List<FileIssue>();
        RenameResult? result = null;
        try
        {
            if (moveDestination is not null)
            {
                var moved = await Rename.MoveConflictsAsync(conflicts, moveDestination, token);
                result = new(moved.MovedPaths.Count, moved.Issues);
            }
            else result = undo ? await Rename.UndoAsync(token) : await Rename.ApplyAsync(requests, token);
            renameIssues.AddRange(result.Issues);
        }
        catch (OperationCanceledException) { renameIssues.Add(new(root, $"{action} operation cancelled.")); }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is JsonException or ArgumentException or InvalidOperationException)
        { renameIssues.Add(new(root, ex.Message)); }
        finally
        {
            InvalidateScan();
            End();
        }
        FolderPath = root;
        Recursive = recurse;
        await ScanAsync();
        Issues = Issues.Concat(renameIssues).ToArray();
        var outcome = result is null ? $"{action} did not complete; see Issues. " :
            $"{action} finished: {result.Changed:N0} files changed; {renameIssues.Count:N0} issues. ";
        Status = outcome + (scan is null ? "Scan again to refresh results." : "Results refreshed.");
    }

    private static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "FolderSize recycling" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
