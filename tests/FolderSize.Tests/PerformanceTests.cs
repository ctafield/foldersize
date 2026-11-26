using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FolderSize.App;
using FolderSize.Core;
using Xunit.Abstractions;

namespace FolderSize.Tests;

public sealed class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public Task HundredThousandFilesRemainVirtualizedAndCancellable() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        await Task.Run(() =>
        {
            for (var i = 0; i < 100000; i++) folder.Write(i.ToString("D6"), "x");
        });
        var window = new MainWindow();
        try
        {
            window.Show();
            window.Model.FolderPath = folder.Root;
            var maximumTickGap = TimeSpan.Zero;
            var lastTick = Stopwatch.GetTimestamp();
            var ticks = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                var gap = Stopwatch.GetElapsedTime(lastTick, now);
                if (gap > maximumTickGap) maximumTickGap = gap;
                lastTick = now;
                ticks++;
            };
            timer.Start();
            var watch = Stopwatch.StartNew();
            await window.Model.ScanAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            watch.Stop();
            timer.Stop();
            Assert.Equal(100000, window.Model.Rows.Count);
            Assert.Equal(100000, window.Model.Current?.Size);
            var grid = (DataGrid)window.FindName("UsageGrid");
            var realized = CountRows(grid);
            Assert.InRange(realized, 1, 200);
            var tiles = TreemapLayout.Create(window.Model.Current!.Children, 800, 600);
            Assert.InRange(tiles.Count, 1, 1201);
            Assert.Equal(100000, tiles.Sum(tile => tile.Item.Size));
            Assert.True(ticks > 0);
            Assert.True(maximumTickGap < TimeSpan.FromSeconds(1),
                $"UI dispatcher was blocked for {maximumTickGap}.");
            var elapsed = watch.Elapsed;
            using var cancellation = new CancellationTokenSource();
            Stopwatch? cancelWatch = null;
            var progress = new CallbackProgress(value =>
            {
                if (value.Phase != "Scanning" || cancelWatch is not null) return;
                cancelWatch = Stopwatch.StartNew();
                cancellation.Cancel();
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() =>
                new FolderScanner().Scan(folder.Root, true, progress, cancellation.Token)));
            Assert.NotNull(cancelWatch);
            cancelWatch.Stop();
            Assert.True(cancelWatch.Elapsed < TimeSpan.FromSeconds(1), $"Cancellation took {cancelWatch.Elapsed}.");
            using var process = Process.GetCurrentProcess();
            output.WriteLine($"100,000-file scan + UI: {elapsed.TotalSeconds:F3} s");
            output.WriteLine($"Realized rows: {realized}; treemap tiles: {tiles.Count}");
            output.WriteLine($"UI ticks: {ticks}; maximum 20ms timer gap: {maximumTickGap.TotalMilliseconds:F1} ms");
            output.WriteLine($"In-flight traversal cancellation: {cancelWatch.Elapsed.TotalMilliseconds:F1} ms");
            output.WriteLine($"Testhost peak working set (includes fixture + WPF): {process.PeakWorkingSet64 / 1048576d:F1} MiB");
        }
        finally { window.Close(); }
    });

    private sealed class CallbackProgress(Action<WorkProgress> callback) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value) => callback(value);
    }

    private static int CountRows(System.Windows.DependencyObject root)
    {
        var result = root is DataGridRow ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            result += CountRows(VisualTreeHelper.GetChild(root, i));
        return result;
    }
}
