using System.Windows.Threading;

namespace FolderSize.Tests;

internal static class Sta
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task Run(Func<Task> work)
    {
        // WPF's theme/BAML caches are shared across dispatcher threads.
        await Gate.WaitAsync();
        try { await RunCore(work); }
        finally { Gate.Release(); }
    }

    private static Task RunCore(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await work(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
