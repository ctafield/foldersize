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
    bool SupportsConcurrentReads => false;
}

public interface IRecycler
{
    void Recycle(string path);
}

public sealed class DuplicateFinder(IFileAccess access)
{
    private const int SampleSize = 4096;
    private int Concurrency => access.SupportsConcurrentReads ? Math.Min(Environment.ProcessorCount, 8) : 1;

    private readonly record struct HashInput(string Path, long Length, DateTime LastWriteUtc, FileStamp? ExpectedStamp);
    private readonly record struct HashResult(DuplicateFile? File, string? Hash, FileIssue? Issue);

    public DuplicateResult Find(ScanResult scan, IProgress<WorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<FileIssue>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var buckets = new Dictionary<(long Length, string Hash), List<DuplicateFile>>();
        var groups = new List<DuplicateGroup>();
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = scan.Root.Files().GroupBy(file => file.Size).Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(file => new HashInput(file.FullPath, file.Size, file.LastWriteUtc, null)).ToArray();
        var sampled = HashCandidates(candidates, sample: true, "Sampling candidates (file sizes examined)",
            progress, cancellationToken);
        foreach (var result in sampled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Issue is { } issue)
            {
                issues.Add(issue);
                continue;
            }
            if (result.File is not { } file || result.Hash is not { } hash)
                throw new InvalidOperationException("A hash worker did not return a result.");
            // Reduce in discovery order so parallel reads never change which hard-link path is kept.
            if (!identities.Add(file.Stamp.Identity))
            {
                issues.Add(new(file.Path, "Skipped hard link to an already examined file."));
                continue;
            }
            var key = (file.Stamp.Length, hash);
            if (!buckets.TryGetValue(key, out var matches)) buckets[key] = matches = [];
            matches.Add(file);
        }
        var fullBuckets = new List<List<DuplicateFile>>();
        var fullCandidates = new List<HashInput>();
        foreach (var samples in buckets.Values.Where(items => items.Count > 1))
        {
            if (samples[0].Stamp.Length <= SampleSize * 3)
            {
                fullBuckets.Add(samples);
                continue;
            }
            foreach (var candidate in samples)
                fullCandidates.Add(new(candidate.Path, candidate.Stamp.Length, candidate.Stamp.LastWriteUtc, candidate.Stamp));
        }
        var hashed = HashCandidates(fullCandidates, sample: false, "Hashing sample matches", progress, cancellationToken);
        var fullHashes = new Dictionary<(long Length, string Hash), List<DuplicateFile>>();
        foreach (var result in hashed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Issue is { } issue)
            {
                issues.Add(issue);
                continue;
            }
            if (result.File is not { } file || result.Hash is not { } hash)
                throw new InvalidOperationException("A hash worker did not return a result.");
            var key = (file.Stamp.Length, hash);
            if (!fullHashes.TryGetValue(key, out var matches)) fullHashes[key] = matches = [];
            matches.Add(file);
        }
        fullBuckets.AddRange(fullHashes.Values.Where(items => items.Count > 1));
        progress?.Report(new(0, 0, "Verifying exact matches"));
        var verified = new DuplicateResult[fullBuckets.Count];
        Parallel.For(0, fullBuckets.Count, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Concurrency
        }, index => verified[index] = VerifyBucket(fullBuckets[index], cancellationToken));
        foreach (var result in verified)
        {
            groups.AddRange(result.Groups);
            issues.AddRange(result.Issues);
        }
        progress?.Report(new(fullBuckets.Sum(bucket => (long)bucket.Count),
            fullBuckets.Sum(bucket => bucket[0].Stamp.Length * bucket.Count), "Verifying exact matches"));
        cancellationToken.ThrowIfCancellationRequested();
        return new(groups.OrderByDescending(group => group.ExtraBytes).ToArray(), issues);
    }

    private HashResult[] HashCandidates(IReadOnlyList<HashInput> candidates, bool sample, string phase,
        IProgress<WorkProgress>? progress, CancellationToken token)
    {
        var results = new HashResult[candidates.Count];
        var gate = new object();
        var timer = Stopwatch.StartNew();
        long processed = 0, bytes = 0;
        Parallel.For(0, candidates.Count, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Concurrency
        }, index =>
        {
            var candidate = candidates[index];
            try
            {
                using var lease = access.Open(candidate.Path);
                if (candidate.ExpectedStamp is { } expected && lease.Stamp != expected)
                    throw new IOException($"File changed or was replaced: {candidate.Path}");
                if (lease.Stamp.Length != candidate.Length || lease.Stamp.LastWriteUtc != candidate.LastWriteUtc)
                    throw new IOException("File changed since the scan; scan again.");
                var hash = sample ? SampleHash(lease.Stream, lease.Stamp.Length, token) : Hash(lease.Stream, token);
                results[index] = new(new(candidate.Path, lease.Stamp), hash, null);
                if (progress is not null)
                {
                    lock (gate)
                    {
                        processed++;
                        bytes += lease.Stamp.Length;
                        if (timer.ElapsedMilliseconds >= 100)
                        {
                            progress.Report(new(processed, bytes, phase));
                            timer.Restart();
                        }
                    }
                }
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex))
            {
                results[index] = new(null, null, new(candidate.Path, ex.Message));
            }
        });
        progress?.Report(new(processed, bytes, phase));
        return results;
    }

    private DuplicateResult VerifyBucket(List<DuplicateFile> bucket, CancellationToken token)
    {
        var groups = new List<DuplicateGroup>();
        var issues = new List<FileIssue>();
        var remaining = bucket;
        while (remaining.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var keeper = remaining[0];
            var unmatched = new List<DuplicateFile>();
            byte[]? referenceBuffer = null, candidateBuffer = null;
            try
            {
                using var reference = OpenVerified(access, keeper);
                // Keep the reference lease locked while reusing its small-file snapshot.
                if (keeper.Stamp.Length <= SampleSize * 3)
                {
                    referenceBuffer = ArrayPool<byte>.Shared.Rent(65536);
                    candidateBuffer = ArrayPool<byte>.Shared.Rent(65536);
                    reference.Stream.Position = 0;
                    if (ReadBlock(reference.Stream, referenceBuffer, token) != keeper.Stamp.Length)
                        throw new IOException($"File changed or was replaced: {keeper.Path}");
                }
                var matches = new List<DuplicateFile> { keeper };
                for (var index = 1; index < remaining.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = remaining[index];
                    try
                    {
                        using var lease = OpenVerified(access, candidate);
                        bool equal;
                        if (referenceBuffer is not null && candidateBuffer is not null)
                        {
                            lease.Stream.Position = 0;
                            var read = ReadBlock(lease.Stream, candidateBuffer, token);
                            equal = read == keeper.Stamp.Length &&
                                referenceBuffer.AsSpan(0, read).SequenceEqual(candidateBuffer.AsSpan(0, read));
                        }
                        else equal = Equal(reference.Stream, lease.Stream, token);
                        if (equal) matches.Add(candidate);
                        else unmatched.Add(candidate);
                    }
                    catch (Exception ex) when (FileErrors.IsExpected(ex))
                    {
                        issues.Add(new(candidate.Path, ex.Message));
                    }
                }
                if (matches.Count > 1) groups.Add(new(matches));
            }
            catch (Exception ex) when (FileErrors.IsExpected(ex))
            {
                issues.Add(new(keeper.Path, ex.Message));
                unmatched.AddRange(remaining.GetRange(1, remaining.Count - 1));
            }
            finally
            {
                if (referenceBuffer is not null) ArrayPool<byte>.Shared.Return(referenceBuffer);
                if (candidateBuffer is not null) ArrayPool<byte>.Shared.Return(candidateBuffer);
            }
            remaining = unmatched;
        }
        return new(groups, issues);
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
                var readA = ReadBlock(first, a, token);
                var readB = ReadBlock(second, b, token);
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

    private static int ReadBlock(Stream stream, byte[] buffer, CancellationToken token)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
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
