using System.IO;
using FolderSize.App;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RecyclingIntegrationTests
{
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
