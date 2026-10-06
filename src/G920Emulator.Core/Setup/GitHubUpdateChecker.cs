using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace G920Emulator.Core.Setup;

public sealed record GitHubReleaseInfo(
    string Tag,
    string VersionLabel,
    string HtmlUrl,
    string? ZipUrl,
    bool IsNewer);

/// <summary>
/// Reads the latest published GitHub release. Failures are silent (offline / rate limit).
/// </summary>
public static class GitHubUpdateChecker
{
    public const string ReleasesLatestApi =
        "https://api.github.com/repos/camborambo/G920Emulator/releases/latest";
    public const string ReleasesPageUrl =
        "https://github.com/camborambo/G920Emulator/releases/latest";
    public const string ReleaseZipName = "G920Emulator-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();

    public static HttpClient Client => Http;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("G920Emulator", AppVersion.Display));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<GitHubReleaseInfo?> TryGetLatestAsync(string currentVersion, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesLatestApi, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<LatestReleaseDto>(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            var tag = dto?.TagName?.Trim();
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var label = StripPrefix(tag);
            var page = string.IsNullOrWhiteSpace(dto!.HtmlUrl) ? ReleasesPageUrl : dto.HtmlUrl.Trim();
            return new GitHubReleaseInfo(tag, label, page, PickZipUrl(dto.Assets), IsNewer(label, currentVersion));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true);
        var buffer = new byte[80 * 1024];
        long copied = 0;
        while (true)
        {
            var n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            copied += n;
            if (total is > 0)
                progress?.Report(copied / (double)total);
        }
        progress?.Report(1);
    }

    private static string? PickZipUrl(LatestAssetDto[]? assets)
    {
        if (assets is null || assets.Length == 0)
            return null;
        foreach (var asset in assets)
        {
            if (string.Equals(asset.Name, ReleaseZipName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return asset.BrowserDownloadUrl.Trim();
        }
        foreach (var asset in assets)
        {
            var name = asset.Name ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && name.Contains("G920Emulator", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return asset.BrowserDownloadUrl.Trim();
        }
        return null;
    }

    public static bool IsNewer(string latest, string current)
    {
        if (!TryParse(latest, out var a) || !TryParse(current, out var b))
            return !string.Equals(StripPrefix(latest), StripPrefix(current), StringComparison.OrdinalIgnoreCase);
        return a > b;
    }

    private static bool TryParse(string value, out Version version)
    {
        var text = StripPrefix(value);
        var dash = text.IndexOf('-');
        if (dash > 0)
            text = text[..dash];
        return Version.TryParse(text, out version!);
    }

    private static string StripPrefix(string value)
    {
        value = value.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        return value.Trim();
    }

    private sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public LatestAssetDto[]? Assets { get; set; }
    }

    private sealed class LatestAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
