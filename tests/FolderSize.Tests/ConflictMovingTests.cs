using System.IO;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class ConflictMovingTests
{
    [Fact]
    public void CreatesFolderAndKeepsAllContentsWithOriginalNamesOrUniqueSuffixes()
    {
        using var folder = new TestFolder();
        var a = folder.Write(@"files\a\same.txt", "first");
        var b = folder.Write(@"files\b\same.txt", "second");
        var files = new FolderScanner().Scan(Path.Combine(folder.Root, "files"), true).Root.Files().ToArray();
        var destination = Path.Combine(folder.Root, "Conflicts");
        var mover = new ConflictMoving(new WindowsFileAccess());
        var first = mover.Move([files.Single(file => file.FullPath == a)], destination);
        Assert.Empty(first.Issues);
        folder.Write(@"Conflicts\same (1).txt", "existing");
        Directory.CreateDirectory(Path.Combine(destination, "same (2).txt"));
        var second = mover.Move([files.Single(file => file.FullPath == b)], destination);
        Assert.Empty(second.Issues);
        Assert.Equal(Path.Combine(destination, "same (3).txt"), Assert.Single(second.MovedPaths));
        Assert.Equal("first", File.ReadAllText(Path.Combine(destination, "same.txt")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destination, "same (1).txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "same (3).txt")));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void ChangedAndMissingFilesAreReportedWhileOtherFilesMove()
    {
        using var folder = new TestFolder();
        var changed = folder.Write(@"files\changed.txt", "original");
        var missing = folder.Write(@"files\missing.txt", "missing");
        folder.Write(@"files\good.txt", "good");
        var files = new FolderScanner().Scan(Path.Combine(folder.Root, "files"), true).Root.Files().ToArray();
        File.SetLastWriteTimeUtc(changed, File.GetLastWriteTimeUtc(changed).AddMinutes(1));
        File.Delete(missing);
        var result = new ConflictMoving(new WindowsFileAccess()).Move(files, Path.Combine(folder.Root, "Conflicts"));
        Assert.EndsWith("good.txt", Assert.Single(result.MovedPaths));
        Assert.Equal(2, result.Issues.Count);
        Assert.True(File.Exists(changed));
    }

    [Fact]
    public void CancellationAndSourceFolderDestinationDoNotMoveFiles()
    {
        using var folder = new TestFolder();
        var path = folder.Write(@"files\keep.txt", "keep");
        var files = new FolderScanner().Scan(Path.GetDirectoryName(path)!, true).Root.Files().ToArray();
        var mover = new ConflictMoving(new WindowsFileAccess());
        var destination = Path.Combine(folder.Root, "Conflicts");
        Assert.Throws<OperationCanceledException>(() => mover.Move(files, destination, new CancellationToken(true)));
        Assert.False(Directory.Exists(destination));
        Assert.Throws<ArgumentException>(() => mover.Move(files, Path.GetDirectoryName(path)!));
        Assert.True(File.Exists(path));
    }
}
