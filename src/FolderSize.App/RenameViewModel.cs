using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using FolderSize.Core;

namespace FolderSize.App;

public sealed class RenameRow(ScanNode node, string proposedName, string? ruleIssue) : Observable
{
    private string proposed = proposedName, status = "";
    private bool selected;
    public ScanNode Node { get; } = node;
    public string OriginalName => Node.Name;
    public string FullPath => Node.FullPath;
    public string ProposedName { get => proposed; set => Set(ref proposed, value); }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Status { get => status; internal set => Set(ref status, value); }
    internal string? RuleIssue { get; set; } = ruleIssue;
}

public sealed class RenameViewModel : Observable
{
    private readonly RenameStore store;
    private ScanNode? scope;
    private RenameOptions options = new();
    private IReadOnlyList<RenameRow> rows = [];
    private CancellationTokenSource? previewCancellation;
    private CancellationTokenSource? detectionCancellation;
    private bool detecting;
    private IReadOnlyList<RenamePatternSuggestion> suggestions = [];
    private bool operationBusy, previewBusy, updatingRows, historyError, previewError;
    private bool includeSubfolders;
    private string filter = "*", status = "Scan a folder to preview renames.";
    private string historyStatus = "", presetStatus = "", presetName = "", selectedPreset = "";
    private IReadOnlyDictionary<string, RenameOptions> presets = new Dictionary<string, RenameOptions>();
    private RenameJournal? journal;
    private readonly HashSet<string> movedConflicts = new(StringComparer.OrdinalIgnoreCase);

    public RenameViewModel(RenameStore store)
    {
        this.store = store;
        ReloadHistory();
        try { presets = store.LoadPresets(); }
        catch (Exception ex) when (StorageError(ex)) { PresetStatus = $"Could not load presets: {ex.Message}"; }
    }

    public IReadOnlyList<string> RuleNames { get; } = ["Find and replace", "Capitalisation", "Numbering", "Extract and format"];
    public IReadOnlyList<string> CaseNames { get; } = ["Title case", "lowercase", "UPPERCASE"];
    public int RuleIndex { get => (int)options.Rule; set => Change(options with { Rule = (RenameRule)value }); }
    public int CaseIndex { get => (int)options.Case; set => Change(options with { Case = (RenameCase)value }); }
    public string Find { get => options.Find; set => Change(options with { Find = value }); }
    public string Replace { get => options.Replace; set => Change(options with { Replace = value }); }
    public bool IgnoreCase { get => options.IgnoreCase; set => Change(options with { IgnoreCase = value }); }
    public string Prefix { get => options.Prefix; set => Change(options with { Prefix = value }); }
    public string Start { get => options.Start; set => Change(options with { Start = value }); }
    public string Padding { get => options.Padding; set => Change(options with { Padding = value }); }
    public string Pattern { get => options.Pattern; set => Change(options with { Pattern = value }); }
    public string Template { get => options.Template; set => Change(options with { Template = value }); }
    public bool IncludeSubfolders
    {
        get => includeSubfolders;
        set { Set(ref includeSubfolders, value); QueuePreview(); }
    }
    public string Filter { get => filter; set { Set(ref filter, value); QueuePreview(); } }
    public string ScopePath => scope?.FullPath ?? "No completed scan";
    public IReadOnlyList<RenameRow> Rows { get => rows; private set => Set(ref rows, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string HistoryStatus { get => historyStatus; private set => Set(ref historyStatus, value); }
    public string PresetStatus { get => presetStatus; private set => Set(ref presetStatus, value); }
    public string PresetName { get => presetName; set => Set(ref presetName, value); }
    public IEnumerable<string> PresetNames => presets.Keys.Order(StringComparer.OrdinalIgnoreCase);
    public string SelectedPreset
    {
        get => selectedPreset;
        set
        {
            Set(ref selectedPreset, value);
            if (value is not null && presets.TryGetValue(value, out var saved))
            {
                PresetName = value;
                Change(saved);
            }
        }
    }
    public bool OperationBusy
    {
        get => operationBusy;
        internal set
        {
            Set(ref operationBusy, value);
            if (value) InvalidateDetection();
            Changed(nameof(Idle));
            NotifyActions();
        }
    }
    public bool Idle => !OperationBusy;
    public bool PreviewBusy { get => previewBusy; private set { Set(ref previewBusy, value); NotifyActions(); } }
    public bool Detecting { get => detecting; private set { Set(ref detecting, value); NotifyActions(); } }
    public bool CanDetectPattern => Idle && !Detecting && scope is not null;
    public bool HasConflicts => Rows.Any(IsConflict);
    public bool CanMoveConflicts => Idle && !Detecting && !PreviewBusy && !previewError && HasConflicts;
    public string DefaultConflictFolder => scope is null ? "" : Path.Combine(scope.FullPath, "Conflicts");
    public bool CanApply => Idle && !Detecting && !PreviewBusy && !historyError && !previewError && !HasConflicts &&
        Rows.Any(row => row.Selected) && Rows.All(row => !row.Selected || row.Status == "Ready");
    public bool CanUndo => Idle && !Detecting && !historyError && journal is { Entries.Count: > 0 };
    public Task PreviewTask { get; private set; } = Task.CompletedTask;
    public string? LastRootPath => journal?.RootPath;
    public bool LastRecursive => journal?.Recursive ?? false;
    public IReadOnlyList<RenameEntry> UndoEntries => journal?.Entries ?? [];

    private static bool IsConflict(RenameRow row) => row.Status.StartsWith("Conflict:", StringComparison.Ordinal);
    private void NotifyActions()
    {
        Changed(nameof(HasConflicts));
        Changed(nameof(CanDetectPattern));
        Changed(nameof(CanMoveConflicts));
        Changed(nameof(CanApply));
        Changed(nameof(CanUndo));
    }

    private void Change(RenameOptions updated)
    {
        if (options == updated) return;
        options = updated;
        foreach (var name in new[] { nameof(RuleIndex), nameof(CaseIndex), nameof(Find), nameof(Replace),
                     nameof(IgnoreCase), nameof(Prefix), nameof(Start), nameof(Padding), nameof(Pattern), nameof(Template) })
            Changed(name);
        QueuePreview();
    }

    internal void SetScope(ScanNode? node)
    {
        scope = node;
        Changed(nameof(ScopePath));
        QueuePreview();
    }

    private void QueuePreview(bool rebuild = true)
    {
        InvalidateDetection();
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new();
        if (rebuild) ReplaceRows([]);
        PreviewTask = PreviewAsync(rebuild, previewCancellation.Token);
    }

    private async Task PreviewAsync(bool rebuild, CancellationToken token)
    {
        PreviewBusy = true;
        var node = scope;
        var settings = options;
        var recursive = IncludeSubfolders;
        var fileFilter = Filter;
        var culture = CultureInfo.CurrentCulture;
        var excluded = movedConflicts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = Rows.Select(row => new RowInput(row, row.ProposedName, row.Selected, row.RuleIssue)).ToArray();
        try
        {
            if (node is null)
            {
                ReplaceRows([]);
                Status = "Scan a folder to preview renames.";
                return;
            }
            await Task.Delay(200, token);
            var result = await Task.Run(() =>
            {
                var patterns = ParseFilters(fileFilter);
                var inputs = existing;
                if (rebuild)
                {
                    var transform = RenameRules.Create(settings, culture);
                    var files = ScopedFiles(node, recursive, patterns, excluded);
                    var built = new List<RowInput>();
                    var index = 0;
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        string proposed;
                        string? issue = null;
                        try
                        {
                            var name = transform(Path.GetFileNameWithoutExtension(file.Name), index++);
                            issue = name is null ? "Pattern did not match; edit the proposal to include this file." : null;
                            proposed = name is null ? file.Name : name + Path.GetExtension(file.Name);
                        }
                        catch (ArgumentException ex) { proposed = file.Name; issue = ex.Message; }
                        var row = new RenameRow(file, proposed, issue);
                        built.Add(new(row, proposed, true, issue));
                    }
                    inputs = built.ToArray();
                }
                var counts = inputs.Where(input => input.Issue is null &&
                        RenameRules.NameError(input.Row.FullPath, input.Name) is null)
                    .GroupBy(input => Target(input.Row, input.Name), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                var results = new List<RowResult>(inputs.Length);
                foreach (var input in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    var row = input.Row;
                    var error = input.Issue ?? RenameRules.NameError(row.FullPath, input.Name);
                    if (error is null)
                    {
                        var target = Target(row, input.Name);
                        if (row.OriginalName == input.Name) error = "Unchanged";
                        else if ((File.Exists(target) || Directory.Exists(target)) &&
                            !string.Equals(row.FullPath, target, StringComparison.OrdinalIgnoreCase))
                            error = "Conflict: destination exists.";
                        else if (counts.GetValueOrDefault(target) > 1)
                            error = "Conflict: multiple files have this destination.";
                    }
                    results.Add(new(row, error ?? "Ready", rebuild ? error is null : input.Selected));
                }
                return results;
            }, token);
            token.ThrowIfCancellationRequested();
            updatingRows = true;
            previewError = false;
            try
            {
                foreach (var item in result) { item.Row.Status = item.Status; item.Row.Selected = item.Selected; }
                if (rebuild) ReplaceRows(result.Select(item => item.Row).ToArray());
            }
            finally { updatingRows = false; }
            var conflicts = Rows.Count(IsConflict);
            Status = conflicts > 0
                ? $"Conflict: {conflicts:N0} preview entries have conflicting destinations. Apply is blocked, even for unchecked entries. Move conflicts, edit names or change the rule/filter."
                : $"{Rows.Count:N0} files; {Rows.Count(row => row.Selected && row.Status == "Ready"):N0} selected and ready. " +
                  "Preview only; original extensions are preserved.";
            if (movedConflicts.Count > 0) Status += " Moved conflict files are excluded from rename previews for this session.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is ArgumentException or RegexMatchTimeoutException or OverflowException)
        {
            if (token.IsCancellationRequested) return;
            previewError = true;
            if (rebuild) ReplaceRows([]);
            Status = $"Preview failed: {ex.Message}";
        }
        finally { if (!token.IsCancellationRequested) PreviewBusy = false; }
    }

    private static string Target(RenameRow row, string name) => Path.Combine(Path.GetDirectoryName(row.FullPath)!, name);
    private static string[] ParseFilters(string filter)
    {
        var patterns = filter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0 || patterns.Any(pattern => pattern.IndexOfAny(['\\', '/', ':']) >= 0))
            throw new ArgumentException("Enter filename filters such as *.pdf or *.txt;*.jpg, without folder paths.");
        return patterns;
    }

    private static IEnumerable<ScanNode> ScopedFiles(ScanNode node, bool recursive, string[] patterns,
        HashSet<string> excluded) =>
        (recursive ? node.Files() : node.Children.Where(child => !child.IsDirectory))
            .Where(file => !excluded.Contains(file.FullPath))
            .Where(file => patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, file.Name, ignoreCase: true)))
            .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase);

    private void InvalidateDetection()
    {
        detectionCancellation?.Cancel();
        suggestions = [];
    }

    public async Task<IReadOnlyList<RenamePatternSuggestion>?> DetectPatternsAsync()
    {
        if (!CanDetectPattern || scope is not { } node) return null;
        using var cancellation = new CancellationTokenSource();
        detectionCancellation = cancellation;
        var token = cancellation.Token;
        var recursive = IncludeSubfolders;
        var fileFilter = Filter;
        var excluded = movedConflicts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var culture = CultureInfo.CurrentCulture;
        Detecting = true;
        try
        {
            await PreviewTask;
            token.ThrowIfCancellationRequested();
            Status = "Detecting patterns locally from filenames...";
            var result = await Task.Run(() =>
            {
                var names = new List<string>();
                foreach (var file in ScopedFiles(node, recursive, ParseFilters(fileFilter), excluded))
                {
                    token.ThrowIfCancellationRequested();
                    names.Add(file.Name);
                }
                return RenamePatternDetection.Detect(names, culture, token);
            }, token);
            token.ThrowIfCancellationRequested();
            suggestions = result;
            Status = (HasConflicts ? "Conflict: Apply is blocked. " : "") + (result.Count == 0
                ? "No usable pattern suggestions found. At least two matching files and one changed name are needed; try another filter or edit a rule manually."
                : $"Found {result.Count:N0} pattern suggestions. Choose one to load the preview; no files have been renamed.");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is ArgumentException or RegexMatchTimeoutException or OverflowException)
        {
            if (token.IsCancellationRequested) return null;
            Status = $"Pattern detection failed: {ex.Message}";
            return null;
        }
        finally
        {
            detectionCancellation = null;
            Detecting = false;
        }
    }

    public void UsePatternSuggestion(RenamePatternSuggestion suggestion)
    {
        if (!Idle || Detecting || !suggestions.Contains(suggestion))
            throw new InvalidOperationException("The detected patterns are out of date. Detect patterns again.");
        if (options == suggestion.Options) QueuePreview();
        else Change(suggestion.Options);
    }

    private sealed record RowInput(RenameRow Row, string Name, bool Selected, string? Issue);
    private sealed record RowResult(RenameRow Row, string Status, bool Selected);

    private void ReplaceRows(IReadOnlyList<RenameRow> value)
    {
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows = value;
        foreach (var row in Rows) row.PropertyChanged += RowChanged;
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (updatingRows || sender is not RenameRow row) return;
        if (args.PropertyName == nameof(RenameRow.ProposedName)) row.RuleIssue = null;
        else if (args.PropertyName != nameof(RenameRow.Selected)) return;
        QueuePreview(rebuild: false);
    }

    public IReadOnlyList<RenameRequest> SelectedRequests()
    {
        if (HasConflicts) throw new InvalidOperationException("Conflict: resolve all conflicting destinations in the preview before applying.");
        if (!CanApply) throw new InvalidOperationException("Wait for a valid preview and select files marked Ready.");
        return Rows.Where(row => row.Selected).Select(row =>
            new RenameRequest(row.FullPath, row.ProposedName, row.Node.Size, row.Node.LastWriteUtc)).ToArray();
    }

    public IReadOnlyList<ScanNode> ConflictFiles()
    {
        if (!CanMoveConflicts) throw new InvalidOperationException("Wait for a valid preview containing conflicts.");
        return Rows.Where(IsConflict).Select(row => row.Node).ToArray();
    }

    internal async Task<ConflictMoveResult> MoveConflictsAsync(IReadOnlyList<ScanNode> files, string destination,
        CancellationToken token)
    {
        var result = await Task.Run(() => new ConflictMoving(new WindowsFileAccess()).Move(files, destination, token));
        movedConflicts.UnionWith(result.MovedPaths);
        return result;
    }

    internal async Task<RenameResult> ApplyAsync(IReadOnlyList<RenameRequest> requests, CancellationToken token)
    {
        var root = ScopePath;
        var recursive = IncludeSubfolders;
        var previousId = journal?.Id;
        try { return await Task.Run(() => new Renaming(new WindowsFileAccess(), store).Apply(requests, root, recursive, previousId, token)); }
        finally { ReloadHistory(); }
    }

    internal async Task<RenameResult> UndoAsync(CancellationToken token)
    {
        var id = journal?.Id ?? throw new InvalidOperationException("There is no rename to undo.");
        try { return await Task.Run(() => new Renaming(new WindowsFileAccess(), store).Undo(id, token)); }
        finally { ReloadHistory(); }
    }

    internal void ReloadHistory()
    {
        try
        {
            journal = store.LoadJournal();
            historyError = false;
            HistoryStatus = journal is { Entries.Count: > 0 }
                ? $"Undo available: {journal.Entries.Count:N0} entries from {journal.CreatedUtc.ToLocalTime():g}. Undo checks file identity and changes."
                : "No rename to undo. Undo history is saved across restarts.";
        }
        catch (Exception ex) when (StorageError(ex))
        {
            historyError = true;
            HistoryStatus = $"Cannot read undo history ({store.JournalPath}). Renaming is disabled until it is repaired or removed: {ex.Message}";
        }
        Changed(nameof(UndoEntries));
        NotifyActions();
    }

    public void SavePreset()
    {
        try
        {
            RenameRules.Create(options, CultureInfo.CurrentCulture);
            store.SavePreset(PresetName, options);
            presets = store.LoadPresets();
            Changed(nameof(PresetNames));
            PresetStatus = $"Preset '{PresetName.Trim()}' saved.";
        }
        catch (Exception ex) when (StorageError(ex) || ex is RegexMatchTimeoutException)
        { PresetStatus = $"Could not save preset: {ex.Message}"; }
    }

    private static bool StorageError(Exception ex) => FileErrors.IsExpected(ex) || ex is JsonException or ArgumentException;
}
