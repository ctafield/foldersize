using System.IO;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RecyclingIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "RecycleIntegration")]
    public Task ContextRecyclingIgnoresCheckboxesAndDeletesOnlyTheClickedDuplicate(bool permanentlyDelete) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("one.txt", "same");
        folder.Write("two.txt", "same");
        folder.Write("three.txt", "same");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        await model.FindAsync();
        foreach (var row in model.DuplicateRows) row.Selected = true;
        var clicked = model.DuplicateRows.Last();
        var retained = model.DuplicateRows.Where(row => row != clicked).Select(row => row.Path).ToArray();
        await model.RecycleDuplicateAsync(clicked, permanentlyDelete);
        Assert.False(File.Exists(clicked.Path));
        Assert.All(retained, path => Assert.True(File.Exists(path)));
        Assert.Equal(2, model.DuplicateRows.Count);
        Assert.Empty(model.Issues);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "RecycleIntegration")]
    public Task ContextRecyclingRefusesToDeleteTheLastReadableDuplicate(bool permanentlyDelete) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("one.txt", "same");
        folder.Write("two.txt", "same");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        await model.FindAsync();
        var clicked = model.DuplicateRows.Last();
        File.Delete(model.DuplicateRows.First().Path);
        await model.RecycleDuplicateAsync(clicked, permanentlyDelete);
        Assert.True(File.Exists(clicked.Path));
        Assert.NotEmpty(model.Issues);
        Assert.Contains(permanentlyDelete ? "0 files permanently deleted" : "0 files confirmed recycled", model.Status);
    });

    [Theory]
    [InlineData(DriveType.Fixed, false)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, true)]
    [InlineData(DriveType.Unknown, false)]
    public void PermanentDeletionDefaultMatchesDriveType(DriveType driveType, bool expected)
    {
        Assert.Equal(expected, ShellRecycler.DefaultPermanentDeletion([driveType]));
    }

    [Fact]
    public void MixedNormalAndRemovableOrNetworkBatchesRequireExplicitOptIn()
    {
        Assert.False(ShellRecycler.DefaultPermanentDeletion([DriveType.Fixed, DriveType.Removable]));
        Assert.False(ShellRecycler.DefaultPermanentDeletion([DriveType.Network, DriveType.Fixed]));
        Assert.True(ShellRecycler.DefaultPermanentDeletion([DriveType.Removable, DriveType.Network]));
        Assert.False(ShellRecycler.DefaultPermanentDeletion([]));
        ShellRecycler.EnsureSupportedDrive(DriveType.Network, permanentlyDelete: true);
        Assert.Throws<IOException>(() => ShellRecycler.EnsureSupportedDrive(DriveType.Network));
    }

    [Theory]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Removable)]
    public void LocalFixedAndRemovableDrivesCanReachWindowsRecycling(DriveType driveType)
    {
        ShellRecycler.EnsureSupportedDrive(driveType);
    }

    [Theory]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    public void UnsupportedDrivesAreRefused(DriveType driveType)
    {
        var error = Assert.Throws<IOException>(() => ShellRecycler.EnsureSupportedDrive(driveType));
        Assert.Contains("not deleted", error.Message);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40u)]
    public void NonRecyclingShellCallbackIsVetoed(uint flags)
    {
        var sink = new RecycleSink();
        Assert.True(sink.PreDeleteItem(flags, null!) < 0);
        Assert.False(sink.Recycled);
        Assert.Contains("refused", sink.Error);
    }

    [Fact]
    public void RecyclingShellCallbackIsAllowedButRequiresConfirmation()
    {
        var sink = new RecycleSink();
        Assert.Equal(0, sink.PreDeleteItem(0x80, null!));
        Assert.False(sink.Recycled);
        sink.PostDeleteItem(0x80, null!, 0, null);
        Assert.False(sink.Recycled);
        Assert.Contains("not confirmed", sink.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x00270008)]
    public void PermanentDeletionSinkAllowsOnlyNonRecyclingDeletionAndRequiresShellSuccess(int result)
    {
        var sink = new RecycleSink(permanentlyDelete: true);
        Assert.Equal(0, sink.PreDeleteItem(0, null!));
        Assert.False(sink.Deleted);
        sink.PostDeleteItem(0, null!, result, null);
        Assert.True(sink.Deleted);
        Assert.False(sink.Recycled);
        var refused = new RecycleSink(permanentlyDelete: true);
        Assert.True(refused.PreDeleteItem(0x80, null!) < 0);
        Assert.Contains("refused", refused.Error);
    }

    [Theory]
    [InlineData(-2147467259)]
    [InlineData(-2147024891)]
    public void PermanentDeletionSinkDoesNotConfirmFailedOperations(int result)
    {
        var sink = new RecycleSink(permanentlyDelete: true);
        sink.PostDeleteItem(0, null!, result, null);
        Assert.False(sink.Deleted);
        Assert.Contains("not confirmed", sink.Error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0x00270003)]
    [InlineData(0x00270005)]
    public void PositiveShellStatusDoesNotConfirmDeletionIfTargetStillExists(int result)
    {
        using var folder = new TestFolder();
        var path = folder.Write("retained.txt", "retained");
        var sink = new RecycleSink(permanentlyDelete: true);
        sink.PostDeleteItem(0, null!, result, null);
        Assert.True(sink.Deleted);
        Assert.Throws<IOException>(() => ShellRecycler.EnsureRemoved(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "RecycleIntegration")]
    public Task RecyclesOnlyDisposableFixtureAndRefreshesAfterPartialFailure(bool permanentlyDelete) => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("keep-large", "identical large content");
        folder.Write("recycle-large", "identical large content");
        folder.Write("keep-small", "same");
        folder.Write("vanish-small", "same");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        await model.FindAsync();
        foreach (var row in model.DuplicateRows) row.Selected = false;
        var large = model.DuplicateRows.First(row => row.Path.EndsWith("recycle-large", StringComparison.Ordinal));
        var vanished = model.DuplicateRows.First(row => row.Path.EndsWith("vanish-small", StringComparison.Ordinal));
        large.Selected = vanished.Selected = true;
        File.Delete(vanished.Path);
        await model.RecycleAsync(permanentlyDelete);
        Assert.False(File.Exists(large.Path));
        Assert.True(File.Exists(Path.Combine(folder.Root, "keep-large")));
        Assert.True(File.Exists(Path.Combine(folder.Root, "keep-small")));
        Assert.Contains(permanentlyDelete ? "1 files permanently deleted" : "1 files confirmed recycled", model.Status);
        Assert.Contains("Results refreshed", model.Status);
        Assert.Contains(model.Issues, issue => issue.Path == vanished.Path);
        Assert.Equal(2, model.Rows.Count);
        Assert.Empty(model.DuplicateRows);
        Assert.True(model.CanFind);
    });
}
