using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace FolderSize.Core;

public sealed record FileStamp(long Length, DateTime LastWriteUtc, string Identity);
public sealed record DuplicateFile(string Path, FileStamp Stamp);
public sealed record DuplicateGroup(IReadOnlyList<DuplicateFile> Files)
{
    public long FileSize => Files[0].Stamp.Length;
    public long ExtraBytes => FileSize * (Files.Count - 1);
}
public sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<FileIssue> Issues);

public sealed class FileLease(Stream stream, FileStamp stamp) : IDisposable
{
    public Stream Stream { get; } = stream;
    public FileStamp Stamp { get; } = stamp;
    public void Dispose() => Stream.Dispose();
}

public interface IFileAccess
{
    FileLease Open(string path);
}

public interface IRecycler
{
    void Recycle(string path);
}

public sealed class DuplicateFinder(IFileAccess access)
{
    private const int SampleSize = 4096;

    public DuplicateResult Find(ScanResult scan, IProgress<WorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<FileIssue>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var buckets = new Dictionary<(long Length, string Hash), List<DuplicateFile>>();
        var groups = new List<DuplicateGroup>();
        long processed = 0, bytes = 0;
        var timer = Stopwatch.StartNew();
        foreach (var candidates in scan.Root.Files().GroupBy(file => file.Size).Where(group => group.Skip(1).Any()))
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = candidate.FullPath;
                try
                {
                    using var lease = access.Open(path);
                    if (lease.Stamp.Length != candidate.Size || lease.Stamp.LastWriteUtc != candidate.LastWriteUtc)
                        throw new IOException("File changed since the scan; scan again.");
                    if (!identities.Add(lease.Stamp.Identity))
                    {
                        issues.Add(new(path, "Skipped hard link to an already examined file."));
                        continue;
                    }
                    var hash = SampleHash(lease.Stream, lease.Stamp.Length, cancellationToken);
                    var key = (lease.Stamp.Length, hash);
                    if (!buckets.TryGetValue(key, out var matches)) buckets[key] = matches = [];
                    matches.Add(new(path, lease.Stamp));
                    processed++;
                    bytes += lease.Stamp.Length;
                }
                catch (Exception ex) when (FileErrors.IsExpected(ex))
                {
                    issues.Add(new(path, ex.Message));
                }
                if (timer.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new(processed, bytes, "Sampling candidates (file sizes examined)"));
                    timer.Restart();
                }
            }
        }
        var fullBuckets = new List<List<DuplicateFile>>();
        processed = bytes = 0;
        foreach (var samples in buckets.Values.Where(items => items.Count > 1))
        {
            if (samples[0].Stamp.Length <= SampleSize * 3)
            {
                fullBuckets.Add(samples);
                continue;
            }
            var hashes = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
            foreach (var candidate in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var lease = OpenVerified(access, candidate);
                    var hash = Hash(lease.Stream, cancellationToken);
                    if (!hashes.TryGetValue(hash, out var matches)) hashes[hash] = matches = [];
                    matches.Add(candidate);
                    processed++;
                    bytes += lease.Stamp.Length;
                }
                catch (Exception ex) when (FileErrors.IsExpected(ex))
                {
                    issues.Add(new(candidate.Path, ex.Message));
                }
                if (timer.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new(processed, bytes, "Hashing sample matches"));
                    timer.Restart();
                }
            }
            fullBuckets.AddRange(hashes.Values.Where(items => items.Count > 1));
        }
        foreach (var bucket in fullBuckets)
        {
            var partitions = new List<List<DuplicateFile>>();
            foreach (var candidate in bucket)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var candidateLease = OpenVerified(access, candidate);
                    List<DuplicateFile>? match = null;
                    foreach (var partition in partitions)
                    {
                        using var reference = OpenVerified(access, partition[0]);
                        if (Equal(candidateLease.Stream, reference.Stream, cancellationToken))
                        {
                            match = partition;
                            break;
                        }
                    }
                    if (match is null) partitions.Add([candidate]);
                    else match.Add(candidate);
                }
                catch (Exception ex) when (FileErrors.IsExpected(ex))
                {
                    issues.Add(new(candidate.Path, ex.Message));
                }
            }
            groups.AddRange(partitions.Where(items => items.Count > 1).Select(items => new DuplicateGroup(items)));
            progress?.Report(new(processed, bytes, "Verifying exact matches"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(groups.OrderByDescending(group => group.ExtraBytes).ToArray(), issues);
    }

    internal static FileLease OpenVerified(IFileAccess access, DuplicateFile file)
    {
        var lease = access.Open(file.Path);
        if (lease.Stamp == file.Stamp) return lease;
        lease.Dispose();
        throw new IOException($"File changed or was replaced: {file.Path}");
    }

    private static string SampleHash(Stream stream, long length, CancellationToken token)
    {
        if (length <= SampleSize * 3) return Hash(stream, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(SampleSize);
        try
        {
            long[] offsets = [0, (length - SampleSize) / 2, length - SampleSize];
            foreach (var offset in offsets)
            {
                token.ThrowIfCancellationRequested();
                stream.Position = offset;
                stream.ReadExactly(buffer, 0, SampleSize);
                hash.AppendData(buffer, 0, SampleSize);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static bool Equal(Stream first, Stream second, CancellationToken token)
    {
        first.Position = second.Position = 0;
        var a = ArrayPool<byte>.Shared.Rent(65536);
        var b = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var readA = ReadBlock(first, a);
                var readB = ReadBlock(second, b);
                if (readA != readB || !a.AsSpan(0, readA).SequenceEqual(b.AsSpan(0, readB))) return false;
                if (readA == 0) return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        return count;
    }
}

public sealed record RecycleResult(IReadOnlyList<string> Recycled, IReadOnlyList<FileIssue> Issues);

public sealed class DuplicateRecycling(IFileAccess access, IRecycler recycler)
{
    public RecycleResult Recycle(IReadOnlyList<DuplicateGroup> groups, IReadOnlySet<string> selected,
        CancellationToken cancellationToken = default)
    {
        var known = groups.SelectMany(group => group.Files).Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Any(path => !known.Contains(path)))
            throw new ArgumentException("Selection includes files outside the duplicate results.", nameof(selected));
        if (groups.Any(group => group.Files.All(file => selected.Contains(file.Path))))
            throw new InvalidOperationException("Keep at least one file in every duplicate group.");

        var recycled = new List<string>();
        var issues = new List<FileIssue>();
        foreach (var group in groups.Where(group => group.Files.Any(file => selected.Contains(file.Path))))
        {
            var retained = group.Files.First(file => !selected.Contains(file.Path));
            foreach (var candidate in group.Files.Where(file => selected.Contains(file.Path)))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    issues.Add(new(candidate.Path, "Recycling cancelled; remaining files were not processed."));
                    return new(recycled, issues);
                }
                try
                {
                    using var keeper = DuplicateFinder.OpenVerified(access, retained);
                    using (var target = DuplicateFinder.OpenVerified(access, candidate))
                    {
                        if (target.Stamp.Identity == keeper.Stamp.Identity ||
                            !DuplicateFinder.Equal(keeper.Stream, target.Stream, cancellationToken))
                            throw new IOException("Files are no longer independent exact duplicates.");
                    }
                    // The retained copy stays locked against writes/deletion while the Shell recycles the target.
                    recycler.Recycle(candidate.Path);
                    recycled.Add(candidate.Path);
                }
                catch (OperationCanceledException)
                {
                    issues.Add(new(candidate.Path, "Recycling cancelled; remaining files were not processed."));
                    return new(recycled, issues);
                }
                catch (Exception ex) when (FileErrors.IsExpected(ex))
                {
                    issues.Add(new(candidate.Path, ex.Message));
                    break;
                }
            }
        }
        return new(recycled, issues);
    }
}
