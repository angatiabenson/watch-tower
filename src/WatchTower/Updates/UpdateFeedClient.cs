using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using WatchTower.Hosting;

namespace WatchTower.Updates;

/// <summary>Finds the newest release on the configured feed.</summary>
public interface IUpdateFeedClient
{
    /// <summary>The newest release on the channel, or null when the feed has none.</summary>
    Task<UpdateManifest?> GetLatestAsync(UpdateOptions options, CancellationToken ct);
}

public sealed class UpdateFeedClient(IHttpClientFactory httpClientFactory, ILogger<UpdateFeedClient> logger) : IUpdateFeedClient
{
    public const string HttpClientName = "Updates";
    public const string ManifestAssetName = "watchtower-update.json";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<UpdateManifest?> GetLatestAsync(UpdateOptions options, CancellationToken ct)
    {
        var feed = RequireHttps(options.FeedUrl, "Updates:FeedUrl");
        var includePrerelease = string.Equals(options.Channel, "prerelease", StringComparison.OrdinalIgnoreCase);
        var http = CreateClient();

        var manifest = IsGitHubReleasesApi(feed)
            ? await FromGitHubAsync(http, feed, includePrerelease, ct)
            : await http.GetFromJsonAsync<UpdateManifest>(feed, Json, ct);

        if (manifest is null)
            return null;
        if (!SemanticVersion.TryParse(manifest.Version, out var version))
            throw new InvalidDataException($"The update feed returned an invalid version '{manifest.Version}'.");
        if (version.IsPrerelease && !includePrerelease)
            return null;
        return manifest;
    }

    private async Task<UpdateManifest?> FromGitHubAsync(HttpClient http, Uri feed, bool includePrerelease, CancellationToken ct)
    {
        var releases = await http.GetFromJsonAsync<List<GitHubRelease>>(
            feed.AbsoluteUri.TrimEnd('/') + "?per_page=30", Json, ct) ?? [];

        var candidate = releases
            .Where(r => !r.Draft && (includePrerelease || !r.Prerelease))
            .Select(r => (Release: r, Ok: SemanticVersion.TryParse(r.TagName, out var v), Version: v))
            .Where(x => x.Ok && x.Release.Assets.Any(a => a.Name == ManifestAssetName))
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

        if (candidate.Release is null)
        {
            logger.LogDebug("No release with a {Manifest} asset found at {Feed}", ManifestAssetName, feed);
            return null;
        }

        var asset = candidate.Release.Assets.First(a => a.Name == ManifestAssetName);
        var manifest = await http.GetFromJsonAsync<UpdateManifest>(
            RequireHttps(asset.BrowserDownloadUrl, "release asset URL"), Json, ct);

        return manifest is null ? null : manifest with
        {
            Notes = string.IsNullOrWhiteSpace(manifest.Notes) ? candidate.Release.Body : manifest.Notes,
            PublishedAt = manifest.PublishedAt ?? candidate.Release.PublishedAt,
        };
    }

    private HttpClient CreateClient()
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        // GitHub requires a User-Agent; identify the product only.
        http.DefaultRequestHeaders.UserAgent.Clear();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WatchTower-Agent", AgentInfo.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    internal static bool IsGitHubReleasesApi(Uri uri) =>
        uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/').EndsWith("/releases", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Feeds and packages must use HTTPS. Plain HTTP is accepted only for loopback addresses
    /// (a feed served on the same machine, e.g. for testing a self-hosted release).
    /// </summary>
    internal static Uri RequireHttps(string? url, string what)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return uri;
        throw new InvalidDataException($"{what} must be an https:// URL (was '{url}').");
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}