using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Portal.Clients;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// Builds the agent's real <see cref="HttpsPortalClient"/> with the same HttpClient pipeline as the
/// service (named client "PortalClient" + <see cref="AgentAuthHandler"/>, see
/// <c>ServiceCollectionExtensions</c>); only the primary handler is swapped for a test one.
/// </summary>
public static class AgentClientHarness
{
    public const string AgentId = "SRV-WEB-01";
    public const string AgentToken = "q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp";

    /// <summary>The serializer options the agent puts on the wire (HttpsPortalClient.JsonOpts).</summary>
    public static JsonSerializerOptions WireOptions { get; } = (JsonSerializerOptions)(
        typeof(HttpsPortalClient).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        ?? throw new InvalidOperationException("HttpsPortalClient.JsonOpts not found: update the contract tests"));

    /// <summary>The client's private response wrapper for GET config (<c>{config, version}</c>).</summary>
    public static Type ConfigFetchResponseType { get; } =
        typeof(HttpsPortalClient).GetNestedType("ConfigFetchResponse", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HttpsPortalClient.ConfigFetchResponse not found: update the contract tests");

    public static PortalOptions Options(string baseUrl, string agentId = AgentId, string token = AgentToken) => new()
    {
        Enabled = true,
        Protocol = "Https",
        BaseUrl = baseUrl,
        AgentId = agentId,
        AgentToken = token,
        Tls = new TlsOptions { RequireTls = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) },
    };

    /// <summary>
    /// The service's own registration (<c>ServiceCollectionExtensions.AddPortalHttpClient</c>: timeout,
    /// credentials handler restricted to BaseUrl), with the TLS primary handler replaced by
    /// <paramref name="primaryHandler"/>.
    /// </summary>
    public static HttpsPortalClient CreateClient(
        PortalOptions options, Func<HttpMessageHandler> primaryHandler, ILogger<HttpsPortalClient>? logger = null,
        TimeProvider? time = null)
    {
        var monitor = new StaticOptionsMonitor<PortalOptions>(options);
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<PortalOptions>>(monitor);
        ServiceCollectionExtensions.AddPortalHttpClient(services, options.BaseUrl)
            .ConfigurePrimaryHttpMessageHandler(primaryHandler);
        var provider = services.BuildServiceProvider();
        return new HttpsPortalClient(
            provider.GetRequiredService<IHttpClientFactory>(), monitor, logger ?? NullLogger<HttpsPortalClient>.Instance, time);
    }
}

/// <summary>An <see cref="IOptionsMonitor{T}"/> with a fixed value.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>A request the agent sent, with its body read eagerly.</summary>
public sealed record RecordedRequest(
    string Method,
    Uri Uri,
    string? Body,
    string? ContentType,
    long? ContentLength,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The path as sent on the wire (percent-encoding preserved), without the query.</summary>
    public string RawPath => Uri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
}

/// <summary>Records every request and answers with <paramref name="respond"/> (default 200, no body).</summary>
public sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read the length before the body: buffering the content makes the length known, but on the
        // wire a body without a precomputed length is sent chunked.
        var length = request.Content?.Headers.ContentLength;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(
            request.Method.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.ToString(), length, headers);
        lock (_requests)
            _requests.Add(recorded);
        var response = respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        response.RequestMessage = request;
        return response;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}