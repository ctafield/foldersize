using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RecycleBinExclusionTests
{
    [Theory]
    [InlineData(@"C:\$Recycle.Bin", true)]
    [InlineData(@"C:\$Recycle.Bin\", true)]
    [InlineData(@"d:\$recycle.bin\S-1-5-21\folder\file.txt", true)]
    [InlineData(@"\\?\C:\$Recycle.Bin\file.txt", true)]
    [InlineData(@"C:\$Recycle.Bin-backup\file.txt", false)]
    [InlineData(@"C:\$Recycle.Bin.txt", false)]
    [InlineData(@"C:\work\$Recycle.Bin\file.txt", false)]
    [InlineData(@"C:\work\file.txt", false)]
    public void RecognisesOnlyRootRecycleBinTrees(string path, bool excluded) =>
        Assert.Equal(excluded, RecycleBinPaths.IsWithin(path));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyFoldersExcludeTheWholeBinTreeAndPreserveOrdinaryNames(bool containsFile)
    {
        var root = Directory(@"C:\");
        var bin = Directory("$Recycle.Bin", root);
        var user = Directory("S-1-5-21", bin);
        Directory("empty", user);
        if (containsFile) File("$Rfile", user, 1024);
        var ordinary = Directory("work", root);
        var nested = Directory("$Recycle.Bin", ordinary);
        Directory("child", nested);
        File("keep.txt", ordinary, 1);
        var lookalike = Directory("$Recycle.Bin-backup", root);
        var scan = Snapshot(root);

        var matches = EmptyFolderFinder.Find(scan);

        Assert.Equal(new[] { lookalike, nested }, matches.Select(match => match.Node));
        Assert.Equal(1, Assert.Single(matches, match => match.Node == nested).EmptySubfolderCount);
        Assert.Contains(bin, root.Children);
        Assert.Equal(containsFile ? 2 : 1, root.Files().Count());
    }

    [Fact]
    public void LargeFilesExcludeBinFilesWithoutChangingTheScanOrThreshold()
    {
        var root = Directory(@"C:\");
        var bin = Directory("$Recycle.Bin", root);
        File("$Rroot", bin, 4096);
        File("$Rnested", Directory("S-1-5-21", bin), 8192);
        var ordinary = Directory("work", root);
        var nested = File("ordinary.txt", Directory("$Recycle.Bin", ordinary), 3072);
        var lookalike = File("backup.txt", Directory("$Recycle.Bin-backup", root), 2048);
        var equal = File("equal.txt", ordinary, 1024);
        File("below.txt", ordinary, 1023);
        var scan = Snapshot(root);

        var matches = MainViewModel.FilterLargeFiles(scan, 1024);

        Assert.Equal(new[] { nested, lookalike, equal }, matches.Select(row => row.Node));
        Assert.Equal(6, root.Files().Count());
        Assert.Empty(EmptyFolderFinder.Find(scan));
    }

    [Theory]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21")]
    public void SearchesStartedInsideTheBinProduceNeitherFoldersNorFiles(string path)
    {
        var root = Directory(path);
        Directory("empty", root);
        File("$Rfile", root, 2048);
        var scan = Snapshot(root);

        Assert.Empty(EmptyFolderFinder.Find(scan));
        Assert.Empty(MainViewModel.FilterLargeFiles(scan, 0));
        Assert.Single(root.Files());
    }

    private static ScanNode Directory(string name, ScanNode? parent = null)
    {
        var node = new ScanNode(name, parent, true) { IsScanned = true };
        parent?.Children.Add(node);
        return node;
    }

    private static ScanNode File(string name, ScanNode parent, long size)
    {
        var node = new ScanNode(name, parent, false) { Size = size };
        parent.Children.Add(node);
        return node;
    }

    private static ScanResult Snapshot(ScanNode root)
    {
        var files = root.Files().ToArray();
        root.Size = files.Sum(file => file.Size);
        return new(root, files.Length, []);
    }
}
