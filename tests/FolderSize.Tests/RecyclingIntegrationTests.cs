using System.IO;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RecyclingIntegrationTests
{
    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task ContextRecyclingIgnoresCheckboxesAndDeletesOnlyTheClickedDuplicate() => Sta.Run(async () =>
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
        await model.RecycleDuplicateAsync(clicked);
        Assert.False(File.Exists(clicked.Path));
        Assert.All(retained, path => Assert.True(File.Exists(path)));
        Assert.Equal(2, model.DuplicateRows.Count);
        Assert.Empty(model.Issues);
    });

    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task ContextRecyclingRefusesToDeleteTheLastReadableDuplicate() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        folder.Write("one.txt", "same");
        folder.Write("two.txt", "same");
        var model = new MainViewModel { FolderPath = folder.Root };
        await model.ScanAsync();
        await model.FindAsync();
        var clicked = model.DuplicateRows.Last();
        File.Delete(model.DuplicateRows.First().Path);
        await model.RecycleDuplicateAsync(clicked);
        Assert.True(File.Exists(clicked.Path));
        Assert.NotEmpty(model.Issues);
        Assert.Contains("0 files confirmed recycled", model.Status);
    });

    [Fact]
    public void NonRecyclingShellCallbackIsVetoed()
    {
        var sink = new RecycleSink();
        Assert.True(sink.PreDeleteItem(0, null!) < 0);
        Assert.False(sink.Recycled);
        Assert.Contains("refused", sink.Error);
    }

    [Fact]
    [Trait("Category", "RecycleIntegration")]
    public Task RecyclesOnlyDisposableFixtureAndRefreshesAfterPartialFailure() => Sta.Run(async () =>
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
        await model.RecycleAsync();
        Assert.False(File.Exists(large.Path));
        Assert.True(File.Exists(Path.Combine(folder.Root, "keep-large")));
        Assert.True(File.Exists(Path.Combine(folder.Root, "keep-small")));
        Assert.Contains("1 files confirmed recycled", model.Status);
        Assert.Contains("Results refreshed", model.Status);
        Assert.Contains(model.Issues, issue => issue.Path == vanished.Path);
        Assert.Equal(2, model.Rows.Count);
        Assert.Empty(model.DuplicateRows);
        Assert.True(model.CanFind);
    });
}
