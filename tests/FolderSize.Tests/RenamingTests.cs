using System.Globalization;
using System.IO;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RenamingTests
{
    [Fact]
    public void FourGeneralRulesPreserveExtractedValuesAndSupportPadding()
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal("new REPORT", RenameRules.Create(new() { Find = "old", Replace = "new" }, culture)("OLD REPORT", 0));
        Assert.Equal("My Report", RenameRules.Create(new() { Rule = RenameRule.Capitalisation }, culture)("MY report", 0));
        Assert.Equal("Item 009", RenameRules.Create(new() { Rule = RenameRule.Numbering, Prefix = "Item ", Start = "7", Padding = "3" }, culture)("anything", 2));
        var extract = RenameRules.Create(new()
        {
            Rule = RenameRule.ExtractFormat, Pattern = @"^(?<label>.*?)_(?<id>\d+)$", Template = "{label} - {id:000} - {n:00}"
        }, culture);
        Assert.Equal("Invoice - 012 - 02", extract("Invoice_12", 1));
        Assert.Null(extract("No matching numbers", 0));
        Assert.Throws<ArgumentException>(() => RenameRules.Create(new() { Rule = RenameRule.ExtractFormat, Template = "{missing}" }, culture));
        Assert.Throws<ArgumentException>(() => RenameRules.Create(new() { Rule = RenameRule.Numbering, Start = "-1" }, culture));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("CON.txt")]
    [InlineData("aux.backup.txt")]
    [InlineData("name.txt.")]
    [InlineData("name.txt ")]
    [InlineData("name.exe")]
    [InlineData("")]
    public void RejectsInvalidNamesPathsAndExtensionChanges(string target) =>
        Assert.NotNull(RenameRules.NameError(@"C:\example\original.txt", target));

    [Theory]
    [InlineData("new.txt")]
    [InlineData("ORIGINAL.txt")]
    public void ApplyAndUndoSurviveReloadWithoutChangingContents(string targetName)
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"files\original.txt", "keep this content");
        var root = Path.GetDirectoryName(path)!;
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var result = new Renaming(new WindowsFileAccess(), store).Apply([Request(path, targetName)], root, false, null);
        Assert.Equal(1, result.Changed);
        Assert.Empty(result.Issues);
        var target = Path.Combine(root, targetName);
        Assert.Equal("keep this content", File.ReadAllText(target));
        Assert.Contains(targetName, Directory.EnumerateFiles(root).Select(Path.GetFileName));
        var reloaded = new RenameStore(Path.Combine(folder.Root, "state"));
        var undo = new Renaming(new WindowsFileAccess(), reloaded).Undo(reloaded.LoadJournal()!.Id);
        Assert.Equal(1, undo.Changed);
        Assert.Empty(undo.Issues);
        Assert.Contains("original.txt", Directory.EnumerateFiles(root).Select(Path.GetFileName));
        Assert.Equal("keep this content", File.ReadAllText(path));
        Assert.Null(reloaded.LoadJournal());
    }

    [Fact]
    public void ExistingTargetsAndChangedSourcesAreSkippedWithoutOverwriting()
    {
        using var folder = new TestFolder();
        var first = folder.Write("first.txt", "first");
        var second = folder.Write("second.txt", "second");
        var changed = folder.Write("changed.txt", "original");
        var request = Request(changed, "new.txt");
        File.AppendAllText(changed, " changed");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var result = new Renaming(new WindowsFileAccess(), store)
            .Apply([Request(first, "second.txt"), request], folder.Root, false, null);
        Assert.Equal(0, result.Changed);
        Assert.Equal(2, result.Issues.Count);
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.Null(store.LoadJournal());
    }

    [Fact]
    public void UndoPreservesOccupiedOriginalNamesAndModifiedFilesForRetry()
    {
        using var folder = new TestFolder();
        var a = folder.Write("a.txt", "a");
        var b = folder.Write("b.txt", "b");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var engine = new Renaming(new WindowsFileAccess(), store);
        engine.Apply([Request(a, "new-a.txt"), Request(b, "new-b.txt")], folder.Root, false, null);
        File.WriteAllText(a, "another file");
        var result = engine.Undo(store.LoadJournal()!.Id);
        Assert.Equal(1, result.Changed);
        Assert.Single(result.Issues);
        Assert.Equal("another file", File.ReadAllText(a));
        Assert.Equal("b", File.ReadAllText(b));
        Assert.Single(store.LoadJournal()!.Entries);
        File.Delete(a);
        File.AppendAllText(Path.Combine(folder.Root, "new-a.txt"), "changed");
        var refused = engine.Undo(store.LoadJournal()!.Id);
        Assert.Equal(0, refused.Changed);
        Assert.Single(refused.Issues);
        Assert.Single(store.LoadJournal()!.Entries);
    }

    [Fact]
    public void PendingJournalReconcilesPartiallyCompletedWorkAfterRestart()
    {
        using var folder = new TestFolder();
        var first = folder.Write("first.txt", "one");
        var second = folder.Write("second.txt", "two");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var access = new WindowsFileAccess();
        var entries = new[] { first, second }.Select(path =>
        {
            using var lease = access.Open(path);
            return new RenameEntry(path, Path.Combine(folder.Root, "new-" + Path.GetFileName(path)), lease.Stamp);
        }).ToArray();
        var journal = new RenameJournal(1, Guid.NewGuid(), DateTime.UtcNow, folder.Root, false, entries);
        store.SaveJournal(journal);
        File.Move(first, entries[0].NewPath);
        var result = new Renaming(access, new RenameStore(Path.Combine(folder.Root, "state"))).Undo(journal.Id);
        Assert.Equal(1, result.Changed);
        Assert.Empty(result.Issues);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Null(store.LoadJournal());
    }

    [Fact]
    public void CancellationAfterOneMoveLeavesDurableUndoForPartialWork()
    {
        using var folder = new TestFolder();
        var first = folder.Write("first.txt", "one");
        var second = folder.Write("second.txt", "two");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        using var source = new CancellationTokenSource();
        var result = new Renaming(new CancellingAccess(source, 4), store)
            .Apply([Request(first, "new-first.txt"), Request(second, "new-second.txt")], folder.Root, false, null, source.Token);
        Assert.Equal(1, result.Changed);
        Assert.Single(result.Issues);
        Assert.NotNull(store.LoadJournal());
        var undo = new Renaming(new WindowsFileAccess(), store).Undo(store.LoadJournal()!.Id);
        Assert.Equal(1, undo.Changed);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void JournalWriteFailureAndConcurrentHistoryChangesPreventAnyMoves()
    {
        using var folder = new TestFolder();
        var path = folder.Write("a.txt", "a");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        store.SaveJournal(null);
        var engine = new Renaming(new WindowsFileAccess(), store);
        using (var locked = new FileStream(store.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => engine.Apply([Request(path, "b.txt")], folder.Root, false, null));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.True(File.Exists(path));
        Assert.Throws<IOException>(() => engine.Apply([Request(path, "b.txt")], folder.Root, false, Guid.NewGuid()));
        using (store.Lock())
            Assert.Throws<IOException>(() => engine.Apply([Request(path, "b.txt")], folder.Root, false, null));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void UndoCanReconcileSuccessfulMovesAfterFinalJournalWriteFails()
    {
        using var folder = new TestFolder();
        var path = folder.Write("old.txt", "content");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var engine = new Renaming(new WindowsFileAccess(), store);
        engine.Apply([Request(path, "new.txt")], folder.Root, false, null);
        var id = store.LoadJournal()!.Id;
        using (var locked = new FileStream(store.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => engine.Undo(id));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.True(File.Exists(path));
        Assert.NotNull(store.LoadJournal());
        var retry = engine.Undo(id);
        Assert.Equal(0, retry.Changed);
        Assert.Empty(retry.Issues);
        Assert.Null(store.LoadJournal());
    }

    [Fact]
    public void UndoRefusesReplacementEvenWithMatchingSizeAndTimestamp()
    {
        using var folder = new TestFolder();
        var path = folder.Write("old.txt", "same");
        var store = new RenameStore(Path.Combine(folder.Root, "state"));
        var engine = new Renaming(new WindowsFileAccess(), store);
        engine.Apply([Request(path, "new.txt")], folder.Root, false, null);
        var journal = store.LoadJournal()!;
        var entry = Assert.Single(journal.Entries);
        var replacement = folder.Write("replacement.tmp", "same");
        File.SetLastWriteTimeUtc(replacement, entry.Stamp.LastWriteUtc);
        File.Delete(entry.NewPath);
        File.Move(replacement, entry.NewPath);
        var result = engine.Undo(journal.Id);
        Assert.Equal(0, result.Changed);
        Assert.Contains("replaced", Assert.Single(result.Issues).Message);
        Assert.False(File.Exists(path));
        Assert.Equal("same", File.ReadAllText(entry.NewPath));
        Assert.Single(store.LoadJournal()!.Entries);
    }

    private static RenameRequest Request(string path, string name)
    {
        var info = new FileInfo(path);
        return new(path, name, info.Length, info.LastWriteTimeUtc);
    }

    private sealed class CancellingAccess(CancellationTokenSource source, int call) : IFileAccess
    {
        private int count;
        public FileLease Open(string path)
        {
            var lease = new WindowsFileAccess().Open(path);
            if (++count == call) source.Cancel();
            return lease;
        }
    }
}
