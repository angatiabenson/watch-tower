using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;

namespace WatchTower.Portal.Clients;

/// <summary>
/// Whether a portal client compresses its request bodies right now: on when
/// <see cref="PortalOptions.CompressRequests"/> is <c>Gzip</c>, until the portal refuses a compressed
/// request. Then it stays off for this process until <c>CompressRequests</c> or <c>BaseUrl</c>
/// changes (a restart starts over too).
/// </summary>
/// <remarks>
/// "Proven" records that the portal accepted a compressed request since then: from that point an
/// ambiguous refusal (HTTPS 400) is no longer blamed on the encoding and is not probed again.
/// </remarks>
internal sealed class RequestCompression : IDisposable
{
    /// <summary>Logged once each time compression is switched off.</summary>
    public const string FallbackWarning = "Portal does not accept compressed requests; sending uncompressed";

    /// <summary>Gzip level for request bodies: Optimal makes a typical metrics batch about 20% smaller than Fastest for ~50 µs more CPU, once per flush.</summary>
    public const CompressionLevel Level = CompressionLevel.Optimal;

    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger _logger;
    private readonly IDisposable? _changeSubscription;
    private readonly object _lock = new();

    private (string BaseUrl, bool Gzip) _key;
    private bool _disabled;
    private bool _proven;

    public RequestCompression(IOptionsMonitor<PortalOptions> options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _key = KeyOf(options.CurrentValue);
        _changeSubscription = options.OnChange((value, _) => Sync(value));
    }

    /// <summary>Whether the next compressible request is sent compressed.</summary>
    public bool Enabled
    {
        get
        {
            var options = _options.CurrentValue;
            lock (_lock)
            {
                SyncLocked(options);
                return options.GzipRequests && !_disabled;
            }
        }
    }

    /// <summary>Whether the portal has accepted a compressed request since compression was last (re)enabled.</summary>
    public bool Proven
    {
        get
        {
            lock (_lock)
            {
                SyncLocked(_options.CurrentValue);
                return _proven;
            }
        }
    }

    /// <summary>Records that the portal accepted a compressed request.</summary>
    public void MarkAccepted()
    {
        lock (_lock)
        {
            SyncLocked(_options.CurrentValue);
            _proven = true;
        }
    }

    /// <summary>Switches compression off and logs <see cref="FallbackWarning"/>, once.</summary>
    /// <param name="reason">What the portal answered, for the log.</param>
    public void Disable(string reason)
    {
        lock (_lock)
        {
            SyncLocked(_options.CurrentValue);
            if (_disabled)
                return;
            _disabled = true;
            _proven = false;
        }
        _logger.LogWarning(FallbackWarning + " (the portal answered {Reason} to a compressed request); " +
                           "compression stays off until the service restarts or {Section}:CompressRequests or " +
                           "BaseUrl changes", reason, PortalOptions.SectionName);
    }

    private void Sync(PortalOptions options)
    {
        lock (_lock)
            SyncLocked(options);
    }

    /// <summary>A changed portal URL or compression setting starts over: compression on, not proven.</summary>
    private void SyncLocked(PortalOptions options)
    {
        var key = KeyOf(options);
        if (key == _key)
            return;
        _key = key;
        _disabled = false;
        _proven = false;
    }

    private static (string, bool) KeyOf(PortalOptions options) =>
        ((options.BaseUrl ?? "").Trim().TrimEnd('/'), options.GzipRequests);

    public void Dispose() => _changeSubscription?.Dispose();
}

/// <summary>
/// A JSON request body written through gzip straight onto the connection: no length is known in
/// advance, so it is sent with chunked transfer encoding like the uncompressed <c>JsonContent</c>.
/// </summary>
internal sealed class GzipJsonContent<T> : HttpContent
{
    private readonly T _value;
    private readonly JsonSerializerOptions _serializerOptions;

    public GzipJsonContent(T value, JsonSerializerOptions serializerOptions)
    {
        _value = value;
        _serializerOptions = serializerOptions;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        Headers.ContentEncoding.Add("gzip");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(stream, RequestCompression.Level, leaveOpen: true);
        await JsonSerializer.SerializeAsync(gzip, _value, _serializerOptions, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}