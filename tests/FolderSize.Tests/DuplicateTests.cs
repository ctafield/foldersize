using System.IO;
using System.Collections.Concurrent;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class DuplicateTests
{
    [Fact]
    public void SmallDuplicateGroupsDoNotReopenOrRereadTheReferenceForEveryCopy()
    {
        using var folder = new TestFolder();
        const string content = "matching contents";
        for (var i = 0; i < 32; i++) folder.Write(i.ToString(), content);
        var access = new CountingAccess();
        var result = new DuplicateFinder(access).Find(new FolderScanner().Scan(folder.Root, true));
        Assert.Equal(32, Assert.Single(result.Groups).Files.Count);
        Assert.Empty(result.Issues);
        Assert.All(access.OpenCounts.Values, count => Assert.Equal(2, count));
        Assert.Equal(2L * 32 * content.Length, access.ReadBytes.Values.Sum());
        Assert.Equal(0, access.ActiveLeases);
    }

    [Fact]
    public void ConcurrentReadsAreBoundedAndPreserveDiscoveryOrderAndHardLinkHandling()
    {
        using var folder = new TestFolder();
        for (var group = 0; group < 12; group++)
            for (var copy = 0; copy < 4; copy++)
                folder.Write($"{group:D2}-{copy}", $"contents-{group:D2}");
        Assert.True(TestFolder.CreateHardLink(Path.Combine(folder.Root, "hardlink"), Path.Combine(folder.Root, "00-0"), 0));
        var scan = new FolderScanner().Scan(folder.Root, true);
        var expected = new DuplicateFinder(new CountingAccess()).Find(scan);
        var access = new CountingAccess(concurrent: true);
        var actual = new DuplicateFinder(access).Find(scan);
        Assert.Equal(expected.Groups.SelectMany(group => group.Files).Select(file => file.Path),
            actual.Groups.SelectMany(group => group.Files).Select(file => file.Path));
        Assert.Equal(expected.Issues, actual.Issues);
        Assert.InRange(access.MaximumLeases, 1, 2 * Math.Min(Environment.ProcessorCount, 8));
        Assert.Equal(0, access.ActiveLeases);
    }

    [Fact]
    public void CancellationDuringConcurrentReadsReleasesEveryLease()
    {
        using var folder = new TestFolder();
        for (var i = 0; i < 32; i++) folder.Write(i.ToString(), "same");
        using var source = new CancellationTokenSource();
        var access = new CountingAccess(concurrent: true, onOpen: source.Cancel);
        var scan = new FolderScanner().Scan(folder.Root, true);
        Assert.Throws<OperationCanceledException>(() =>
            new DuplicateFinder(access).Find(scan, cancellationToken: source.Token));
        Assert.Equal(0, access.ActiveLeases);
    }

    [Fact]
    public void ExactVerificationPartitionsFilesChangedAfterHashingEvenWhenTheirStampsMatch()
    {
        using var folder = new TestFolder();
        var original = folder.Write("a", "same");
        var changed = new[] { folder.Write("b", "same"), folder.Write("c", "same") };
        var access = new CountingAccess(beforeOpen: (path, count) =>
        {
            if (count != 2 || path == original) return;
            var modified = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "diff");
            File.SetLastWriteTimeUtc(path, modified);
        });
        var result = new DuplicateFinder(access).Find(new FolderScanner().Scan(folder.Root, true));
        Assert.Equal(changed.Order(), Assert.Single(result.Groups).Files.Select(file => file.Path).Order());
        Assert.Empty(result.Issues);
        Assert.Equal(0, access.ActiveLeases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4192256)]
    [InlineData(8388607)]
    public void LargeSampleMismatchesAvoidFullFileReads(int differenceOffset)
    {
        using var folder = new TestFolder();
        var a = folder.Write("a", "");
        var b = folder.Write("b", "");
        const int length = 8 * 1024 * 1024;
        using (var stream = new FileStream(a, FileMode.Open, FileAccess.Write)) stream.SetLength(length);
        using (var stream = new FileStream(b, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(length);
            stream.Position = differenceOffset;
            stream.WriteByte(1);
        }
        var access = new CountingAccess();
        var result = new DuplicateFinder(access).Find(new FolderScanner().Scan(folder.Root, true));
        Assert.Empty(result.Groups);
        Assert.Empty(result.Issues);
        Assert.Equal(2, access.ReadBytes.Count);
        Assert.All(access.ReadBytes.Values, bytes => Assert.InRange(bytes, 1, 3 * 4096));
    }

    [Fact]
    public void MatchingSamplesStillRequireFullContentsAndPreserveRealDuplicates()
    {
        using var folder = new TestFolder();
        const int length = 1024 * 1024;
        var a = folder.Write("a", "");
        using (var stream = new FileStream(a, FileMode.Open, FileAccess.Write)) stream.SetLength(length);
        var b = Path.Combine(folder.Root, "b");
        var different = Path.Combine(folder.Root, "different");
        File.Copy(a, b);
        File.Copy(a, different);
        using (var stream = new FileStream(different, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 65536;
            stream.WriteByte(1);
        }
        var access = new CountingAccess();
        var result = new DuplicateFinder(access).Find(new FolderScanner().Scan(folder.Root, true));
        var group = Assert.Single(result.Groups);
        Assert.Equal(new[] { a, b }.Order(), group.Files.Select(file => file.Path).Order());
        Assert.Empty(result.Issues);
        Assert.All(access.ReadBytes.Values, bytes => Assert.True(bytes >= length + 3 * 4096));
    }

    [Fact]
    public void DetectsContentNotNamesAndSkipsHardLinks()
    {
        using var folder = new TestFolder();
        var original = folder.Write("one", "same");
        folder.Write(@"nested\different-name", "same");
        folder.Write("not-a-match", "diff");
        var hardLink = Path.Combine(folder.Root, "hardlink");
        Assert.True(TestFolder.CreateHardLink(hardLink, original, 0));
        var result = Find(folder);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(4, group.ExtraBytes);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("hard link"));
        Assert.Empty(Find(folder, false).Groups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroByteFilesAreExcludedBeforeReadingWithoutAffectingNonemptyDuplicates(bool includeNonempty)
    {
        using var folder = new TestFolder();
        var emptyPaths = new[] { folder.Write("a", ""), folder.Write("b", ""), folder.Write(@"nested\empty", "") };
        string[] matches = includeNonempty ? [folder.Write("c", "x"), folder.Write(@"nested\d", "x")] : [];
        var scan = new FolderScanner().Scan(folder.Root, true);
        var access = new CountingAccess(concurrent: true);
        var result = new DuplicateFinder(access).Find(scan);
        Assert.Equal(3, scan.Root.Files().Count(file => file.Size == 0));
        Assert.DoesNotContain(emptyPaths, path => access.OpenCounts.ContainsKey(path));
        Assert.Empty(result.Issues);
        if (includeNonempty)
        {
            var group = Assert.Single(result.Groups);
            Assert.Equal(matches.Order(), group.Files.Select(file => file.Path).Order());
            Assert.Equal(1, group.ExtraBytes);
        }
        else
        {
            Assert.Empty(result.Groups);
            Assert.Empty(access.OpenCounts);
        }
    }

    [Fact]
    public void MissingOrChangedCandidatesAreReported()
    {
        using var folder = new TestFolder();
        var a = folder.Write("a", "same");
        var b = folder.Write("b", "same");
        var scan = new FolderScanner().Scan(folder.Root, true);
        File.Delete(a);
        File.WriteAllText(b, "changed");
        var result = new DuplicateFinder(new WindowsFileAccess()).Find(scan);
        Assert.Empty(result.Groups);
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public void FinderHonorsCancellation()
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        var scan = new FolderScanner().Scan(folder.Root, true);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new DuplicateFinder(new WindowsFileAccess()).Find(scan, cancellationToken: source.Token));
    }

    [Fact]
    public void RejectsSelectingEveryCopyOrAnUnknownPath()
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        var groups = Find(folder).Groups;
        var recycler = new RecordingRecycler();
        var service = new DuplicateRecycling(new WindowsFileAccess(), recycler);
        Assert.Throws<InvalidOperationException>(() => service.Recycle(groups, Selected(groups[0].Files.Select(file => file.Path))));
        Assert.Throws<ArgumentException>(() => service.Recycle(groups, Selected(["unknown"])));
        Assert.Empty(recycler.Paths);
    }

    [Fact]
    public void FreshByteComparisonRejectsSameLengthSameTimestampModification()
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        var groups = Find(folder).Groups;
        var candidate = groups[0].Files[1];
        File.WriteAllText(candidate.Path, "diff");
        File.SetLastWriteTimeUtc(candidate.Path, candidate.Stamp.LastWriteUtc);
        var recycler = new RecordingRecycler();
        var result = new DuplicateRecycling(new WindowsFileAccess(), recycler).Recycle(groups, Selected([candidate.Path]));
        Assert.Single(result.Issues);
        Assert.Empty(recycler.Paths);
        Assert.True(File.Exists(candidate.Path));
    }

    [Fact]
    public void MissingRetainedCopyStopsTheGroup()
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        var groups = Find(folder).Groups;
        File.Delete(groups[0].Files[0].Path);
        var recycler = new RecordingRecycler();
        var result = new DuplicateRecycling(new WindowsFileAccess(), recycler)
            .Recycle(groups, Selected([groups[0].Files[1].Path]));
        Assert.Single(result.Issues);
        Assert.Empty(recycler.Paths);
    }

    [Fact]
    public void RecycleFailureDoesNotDeleteAndReportsPartialSuccess()
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        folder.Write("c", "same");
        var groups = Find(folder).Groups;
        var recycler = new RecordingRecycler { FailOnCall = 2 };
        var selected = groups[0].Files.Skip(1).Select(file => file.Path).ToArray();
        var result = new DuplicateRecycling(new WindowsFileAccess(), recycler).Recycle(groups, Selected(selected));
        Assert.Single(result.Recycled);
        Assert.Single(result.Issues);
        Assert.All(groups[0].Files, file => Assert.True(File.Exists(file.Path)));
    }

    [Fact]
    public void RetainedFileIsLockedDuringRecycle()
    {
        using var folder = new TestFolder();
        folder.Write("a", "same");
        folder.Write("b", "same");
        var groups = Find(folder).Groups;
        var retained = groups[0].Files[0];
        var recycler = new RecordingRecycler
        {
            BeforeRecycle = () => Assert.Throws<IOException>(() => File.WriteAllText(retained.Path, "diff"))
        };
        var result = new DuplicateRecycling(new WindowsFileAccess(), recycler)
            .Recycle(groups, Selected([groups[0].Files[1].Path]));
        Assert.Single(result.Recycled);
        Assert.Empty(result.Issues);
    }

    private static HashSet<string> Selected(IEnumerable<string> paths) => new(paths, StringComparer.OrdinalIgnoreCase);
    private static DuplicateResult Find(TestFolder folder, bool recursive = true) =>
        new DuplicateFinder(new WindowsFileAccess()).Find(new FolderScanner().Scan(folder.Root, recursive));

    private sealed class CountingAccess(bool concurrent = false, Action? onOpen = null,
        Action<string, int>? beforeOpen = null) : IFileAccess
    {
        private readonly object gate = new();
        public bool SupportsConcurrentReads => concurrent;
        public int ActiveLeases { get; private set; }
        public int MaximumLeases { get; private set; }
        public ConcurrentDictionary<string, long> ReadBytes { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> OpenCounts { get; } = new(StringComparer.Ordinal);
        public FileLease Open(string path)
        {
            var openCount = OpenCounts.AddOrUpdate(path, 1, (_, previous) => previous + 1);
            beforeOpen?.Invoke(path, openCount);
            var lease = new WindowsFileAccess().Open(path);
            ReadBytes.TryAdd(path, 0);
            lock (gate)
            {
                ActiveLeases++;
                MaximumLeases = Math.Max(MaximumLeases, ActiveLeases);
            }
            onOpen?.Invoke();
            return new(new CountingStream(lease.Stream,
                count => ReadBytes.AddOrUpdate(path, count, (_, previous) => previous + count),
                () => { lock (gate) ActiveLeases--; }), lease.Stamp);
        }
    }

    private sealed class CountingStream(Stream inner, Action<int> onRead, Action? onDispose = null) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            onRead(read);
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            onRead(read);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingRecycler : IRecycler
    {
        public List<string> Paths { get; } = [];
        public int FailOnCall { get; init; }
        public Action? BeforeRecycle { get; init; }
        private int calls;
        public void Recycle(string path)
        {
            if (++calls == FailOnCall) throw new IOException("Recycling unavailable.");
            BeforeRecycle?.Invoke();
            Paths.Add(path);
        }
    }
}
