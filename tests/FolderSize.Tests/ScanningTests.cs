using System.IO;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class ScanningTests
{
    [Theory]
    [InlineData(true, 12, 3)]
    [InlineData(false, 5, 2)]
    public void TotalsRespectRecursion(bool recursive, long bytes, long files)
    {
        using var folder = new TestFolder();
        folder.Write("root", "12345");
        folder.Write("empty", "");
        folder.Write(@"nested\child", "1234567");
        var result = new FolderScanner().Scan(folder.Root, recursive);
        Assert.Equal(bytes, result.Root.Size);
        Assert.Equal(files, result.FileCount);
        Assert.Empty(result.Issues);
        var nested = Assert.Single(result.Root.Children, node => node.IsDirectory);
        Assert.Equal(recursive, nested.IsScanned);
        Assert.Equal(recursive ? 7 : 0, nested.Size);
    }

    [Fact]
    public void EmptyFolderProducesCompleteEmptyResult()
    {
        using var folder = new TestFolder();
        var result = new FolderScanner().Scan(folder.Root, true);
        Assert.True(result.Root.IsScanned);
        Assert.Empty(result.Root.Children);
        Assert.Equal(0, result.Root.Size);
    }

    [Fact]
    public void MissingRootAndCancellationAreNotSuccessResults()
    {
        using var folder = new TestFolder();
        Assert.Throws<DirectoryNotFoundException>(() => new FolderScanner().Scan(Path.Combine(folder.Root, "missing"), true));
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new FolderScanner().Scan(folder.Root, true, cancellationToken: source.Token));
    }

    [Fact]
    public void JunctionIsSkippedAndReported()
    {
        using var folder = new TestFolder();
        folder.Write(@"target\file", "1234");
        var link = Path.Combine(folder.Root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{Path.Combine(folder.Root, "target")}\"",
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var scan = new FolderScanner().Scan(folder.Root, true);
            Assert.Equal(4, scan.Root.Size);
            Assert.Contains(scan.Issues, issue => issue.Path == link);
            Assert.True(scan.Root.HasErrors);
            Assert.Throws<IOException>(() => new WindowsFileAccess().Open(Path.Combine(link, "file")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void UnreadableChildDoesNotHideOtherFiles()
    {
        using var folder = new TestFolder();
        folder.Write("good", "123");
        var denied = Path.Combine(folder.Root, "denied");
        Directory.CreateDirectory(denied);
        var info = new DirectoryInfo(denied);
        var security = System.IO.FileSystemAclExtensions.GetAccessControl(info);
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(rule);
        System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
        try
        {
            var result = new FolderScanner().Scan(folder.Root, true);
            Assert.Equal(3, result.Root.Size);
            Assert.Contains(result.Issues, issue => issue.Path == denied);
            Assert.True(result.Root.HasErrors);
        }
        finally
        {
            security.RemoveAccessRuleSpecific(rule);
            System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
        }
    }
}
