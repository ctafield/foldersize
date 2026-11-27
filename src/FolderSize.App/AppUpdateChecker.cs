using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSize.App;

public sealed record AppUpdateResult(Version InstalledVersion, Version? LatestVersion)
{
    public bool UpdateAvailable => LatestVersion is not null && LatestVersion > InstalledVersion;
}

public sealed class AppUpdateChecker(HttpClient? client = null)
{
    public static Uri ReleasesUri { get; } = new("https://github.com/ctafield/foldersize/releases");
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/ctafield/foldersize/releases/latest");
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 1024 * 1024
    };
    private readonly HttpClient http = client ?? SharedClient;

    public async Task<AppUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default)
    {
        var installed = ParseVersion(installedVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.UserAgent.ParseAdd($"FolderSize/{installed}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(installed, null);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<LatestRelease>(stream, cancellationToken: cancellationToken);
        if (release is null || release.Draft != false || release.Prerelease != false || string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidDataException("GitHub did not return a valid stable release.");
        return new(installed, ParseVersion(release.TagName));
    }

    private static Version ParseVersion(string value)
    {
        var numeric = value.Split('+')[0];
        if (numeric.StartsWith('v')) numeric = numeric[1..];
        if (!Version.TryParse(numeric, out var version) || version.Build < 0)
            throw new InvalidDataException($"Cannot compare version '{value}'. Expected a numeric release version.");
        return new(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    private sealed class LatestRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
        [JsonPropertyName("draft")]
        public bool? Draft { get; init; }
        [JsonPropertyName("prerelease")]
        public bool? Prerelease { get; init; }
    }
}
