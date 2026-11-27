using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Threading;
using FolderSize.App;

namespace FolderSize.Tests;

public sealed class AppUpdateTests
{
    [Theory]
    [InlineData("1.0.0", "v1.0.1", true)]
    [InlineData("1.0.3+build", "v1.0.3.0", false)]
    [InlineData("1.0.3.0", "v1.0.3", false)]
    [InlineData("2.0.0", "v1.9.9", false)]
    [InlineData("2.0.0", "v10.0.0", true)]
    [InlineData("1.0.3", "v1.0.3.1", true)]
    public async Task ComparesNumericVersionsAndNormalisesBuildMetadata(string installed, string latest, bool available)
    {
        using var client = new HttpClient(new StubUpdateHandler((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/ctafield/foldersize/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(request.Headers.UserAgent, agent => agent.Product?.Name == "FolderSize");
            Assert.Contains(request.Headers.Accept, accept => accept.MediaType == "application/vnd.github+json");
            return Task.FromResult(TestUpdates.Release(latest));
        }));
        var result = await new AppUpdateChecker(client).CheckAsync(installed);
        Assert.Equal(available, result.UpdateAvailable);
        Assert.NotNull(result.LatestVersion);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"tag_name":"v1.2.3","draft":false}""")]
    [InlineData("""{"tag_name":"v1.2.3","draft":false,"prerelease":true}""")]
    [InlineData("""{"tag_name":"v1.2.3","draft":true,"prerelease":false}""")]
    [InlineData("""{"tag_name":"v1.2.3-beta","draft":false,"prerelease":false}""")]
    public async Task RejectsMissingUnstableOrUnsupportedReleaseMetadata(string json)
    {
        using var client = new HttpClient(new StubUpdateHandler((_, _) => Task.FromResult(TestUpdates.Json(json))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateChecker(client).CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task RejectsMalformedJsonAndInvalidInstalledVersions()
    {
        using var client = new HttpClient(new StubUpdateHandler((_, _) => Task.FromResult(TestUpdates.Json("{"))));
        var checker = new AppUpdateChecker(client);
        await Assert.ThrowsAsync<JsonException>(() => checker.CheckAsync("1.0.0"));
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.CheckAsync("not a version"));
    }

    [Fact]
    public async Task MissingReleaseIsDistinctFromAConnectionOrHttpFailure()
    {
        using var missing = new HttpClient(new StubUpdateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var result = await new AppUpdateChecker(missing).CheckAsync("1.0.0");
        Assert.Null(result.LatestVersion);
        Assert.False(result.UpdateAvailable);
        using var forbidden = new HttpClient(new StubUpdateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new AppUpdateChecker(forbidden).CheckAsync("1.0.0"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        using var offline = new HttpClient(new StubUpdateHandler((_, _) => throw new HttpRequestException("Offline")));
        await Assert.ThrowsAsync<HttpRequestException>(() => new AppUpdateChecker(offline).CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task CancellationAndTimeoutStopPendingRequests()
    {
        using var client = new HttpClient(new StubUpdateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return TestUpdates.Release("v1.0.0");
        })) { Timeout = TimeSpan.FromMilliseconds(50) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AppUpdateChecker(client).CheckAsync("1.0.0", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AppUpdateChecker(client).CheckAsync("1.0.0"));
    }

    [Fact]
    public Task SettingsChecksOncePerSessionAndCanRetryWithoutChangingPreferences() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var requests = 0;
        var launches = new List<ProcessStartInfo>();
        using var client = new HttpClient(new StubUpdateHandler((_, _) => Task.FromResult(
            ++requests == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : TestUpdates.Release("v9.0.0"))));
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json"));
        var window = new MainWindow(store, AppTheme.Light, updateChecker: new AppUpdateChecker(client),
            launchReleases: launches.Add) { Width = 820, Height = 640 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, requests);
            var settingsItem = (ListBoxItem)window.FindName("SettingsNavigationItem");
            settingsItem.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
            var status = (TextBlock)view.FindName("UpdateStatus");
            var saveStatus = (TextBlock)view.FindName("SaveStatus");
            var check = (Button)view.FindName("CheckUpdatesButton");
            var link = (Hyperlink)view.FindName("DownloadLink");
            Assert.Equal(1, requests);
            Assert.Contains("Could not check", status.Text);
            Assert.DoesNotContain("No newer", status.Text);
            Assert.DoesNotContain("\u2728", status.Text);
            Assert.True(check.IsEnabled);
            Assert.Equal(AppUpdateChecker.ReleasesUri, link.NavigateUri);
            Assert.Contains("Installed version", ((TextBlock)view.FindName("InstalledVersionText")).Text);
            Assert.Contains("Changes apply immediately", saveStatus.Text);
            ((ComboBox)view.FindName("ThemeBox")).SelectedItem = AppTheme.Dark;
            Assert.Equal(AppTheme.Dark, store.Load());
            Assert.Contains("Could not check", status.Text);
            ((ListBox)window.FindName("MainNavigation")).SelectedIndex = 0;
            settingsItem.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(view, ((ContentControl)window.FindName("SettingsContent")).Content);
            Assert.Equal(1, requests);
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, requests);
            Assert.StartsWith("\u2728 New version available: 9.0.0.", status.Text);
            Assert.Equal(AppTheme.Dark, store.Load());
            link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri, "") { RoutedEvent = Hyperlink.RequestNavigateEvent });
            var launch = Assert.Single(launches);
            Assert.Equal("https://github.com/ctafield/foldersize/releases", launch.FileName);
            Assert.True(launch.UseShellExecute);
            Assert.Empty(launch.Arguments);
            var scroll = Assert.IsType<ScrollViewer>(view.Content);
            scroll.ScrollToEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var linkText = Assert.IsType<TextBlock>(link.Parent);
            var linkBounds = linkText.TransformToAncestor(view).TransformBounds(new Rect(linkText.RenderSize));
            var saveBounds = saveStatus.TransformToAncestor(view).TransformBounds(new Rect(saveStatus.RenderSize));
            Assert.True(linkBounds.Top >= saveBounds.Bottom);
            Assert.True(linkBounds.Right <= view.ActualWidth);
            Assert.True(linkBounds.Bottom <= view.ActualHeight);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task PendingCheckDoesNotBlockSettingsOrOverlapAndWindowCloseCancelsIt() => Sta.Run(async () =>
    {
        using var folder = new TestFolder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new HttpClient(new StubUpdateHandler(async (_, token) =>
        {
            requests++;
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) cancelled.SetResult(); }
            return TestUpdates.Release("v1.0.0");
        }));
        var store = new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json"));
        var window = new MainWindow(store, AppTheme.Light, updateChecker: new AppUpdateChecker(client));
        try
        {
            window.Show();
            ((ListBoxItem)window.FindName("SettingsNavigationItem")).IsSelected = true;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var view = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
            var check = (Button)view.FindName("CheckUpdatesButton");
            Assert.False(check.IsEnabled);
            Assert.False(window.Model.Busy);
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, requests);
            ((ComboBox)view.FindName("ThemeBox")).SelectedItem = AppTheme.Dark;
            Assert.Equal(AppTheme.Dark, store.Load());
            window.Close();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Checking for updates...", ((TextBlock)view.FindName("UpdateStatus")).Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(HttpStatusCode.OK, "No newer stable version")]
    [InlineData(HttpStatusCode.NotFound, "No stable releases")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    public Task NonUpdateStatesHaveNoSparkleAndLinkLaunchErrorsStaySeparate(HttpStatusCode response, string message) =>
        Sta.Run(async () =>
        {
            using var folder = new TestFolder();
            using var client = new HttpClient(new StubUpdateHandler((_, _) => Task.FromResult(response == HttpStatusCode.OK
                ? TestUpdates.Release("v0.0.0") : new HttpResponseMessage(response))));
            var window = new MainWindow(new ThemeSettingsStore(Path.Combine(folder.Root, "settings.json")), AppTheme.Light,
                updateChecker: new AppUpdateChecker(client), launchReleases: _ => throw new InvalidOperationException("Browser unavailable"));
            try
            {
                window.Show();
                ((ListBoxItem)window.FindName("SettingsNavigationItem")).IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view = Assert.IsType<SettingsView>(((ContentControl)window.FindName("SettingsContent")).Content);
                var status = (TextBlock)view.FindName("UpdateStatus");
                Assert.Contains(message, status.Text);
                Assert.DoesNotContain("\u2728", status.Text);
                var previous = status.Text;
                var link = (Hyperlink)view.FindName("DownloadLink");
                link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri, "") { RoutedEvent = Hyperlink.RequestNavigateEvent });
                Assert.Contains("Browser unavailable", ((TextBlock)view.FindName("ReleaseLinkStatus")).Text);
                Assert.Equal(previous, status.Text);
                Assert.True(((Button)view.FindName("CheckUpdatesButton")).IsEnabled);
            }
            finally { window.Close(); }
        });
}

internal sealed class StubUpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}

internal static class TestUpdates
{
    public static AppUpdateChecker NoUpdates { get; } = new(new HttpClient(new StubUpdateHandler((_, _) =>
        Task.FromResult(Release("v1.0.0")))));

    public static HttpResponseMessage Release(string tag) =>
        Json(JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false }));

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
