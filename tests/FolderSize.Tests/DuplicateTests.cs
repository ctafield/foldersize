using System.IO;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class DuplicateTests
{
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

    [Fact]
    public void EmptyDuplicatesHaveNoExtraBytes()
    {
        using var folder = new TestFolder();
        folder.Write("a", "");
        folder.Write("b", "");
        Assert.Equal(0, Assert.Single(Find(folder).Groups).ExtraBytes);
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

    private sealed class CountingAccess : IFileAccess
    {
        public Dictionary<string, long> ReadBytes { get; } = [];
        public FileLease Open(string path)
        {
            var lease = new WindowsFileAccess().Open(path);
            ReadBytes.TryAdd(path, 0);
            return new(new CountingStream(lease.Stream, count => ReadBytes[path] += count), lease.Stamp);
        }
    }

    private sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
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
            if (disposing) inner.Dispose();
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
