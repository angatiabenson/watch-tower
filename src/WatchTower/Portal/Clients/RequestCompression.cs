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
/// request. An explicit refusal of the encoding (HTTPS 415, gRPC UNIMPLEMENTED) switches it off for
/// this process until <c>CompressRequests</c> or <c>BaseUrl</c> changes (a restart starts over too).
/// An ambiguous refusal (HTTPS 400 that the same body uncompressed then passes) only suspends it for
/// <see cref="SuspendFor"/>, after which it is probed again; only <see cref="MaxAmbiguousRefusals"/>
/// such refusals in a row, without the portal ever accepting a compressed request, switch it off.
/// </summary>
/// <remarks>
/// Every request takes a <see cref="Ticket"/> when it decides whether to compress, and reports its
/// outcome with it. A ticket from before a configuration change (a new BaseUrl or setting) is
/// stale: its outcome is ignored, so an old portal's answer can never switch compression off, or
/// mark it proven, for the new one. "Proven" records that the portal accepted a compressed request
/// under the current configuration: from then on a 400 is about the content, not the encoding.
/// </remarks>
internal sealed class RequestCompression : IDisposable
{
    /// <summary>Logged once each time compression is switched off.</summary>
    public const string FallbackWarning = "Portal does not accept compressed requests; sending uncompressed";

    /// <summary>Gzip level for request bodies: Optimal makes a typical metrics batch about 20% smaller than Fastest for ~50 µs more CPU, once per flush.</summary>
    public const CompressionLevel Level = CompressionLevel.Optimal;

    /// <summary>How long an ambiguous refusal suspends compression before it is probed again.</summary>
    public static readonly TimeSpan SuspendFor = TimeSpan.FromHours(1);

    /// <summary>Ambiguous refusals in a row (none accepted compressed in between) that switch it off.</summary>
    public const int MaxAmbiguousRefusals = 3;

    /// <summary>
    /// The configuration generation a request decided under, whether it compresses, and the portal
    /// BaseUrl of that same configuration: the request is sent to <see cref="BaseUrl"/>, so the
    /// destination and the compression state always come from one snapshot.
    /// </summary>
    public readonly record struct Ticket(long Generation, bool Gzip, string BaseUrl);

    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IDisposable? _changeSubscription;
    private readonly object _lock = new();

    private (string BaseUrl, bool Gzip) _key;
    private long _generation;
    private bool _disabled;
    private bool _proven;
    private int _ambiguousRefusals;
    private long? _suspendedSince; // monotonic timestamp of the last ambiguous refusal

    public RequestCompression(IOptionsMonitor<PortalOptions> options, ILogger logger, TimeProvider? time = null)
    {
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _key = KeyOf(options.CurrentValue);
        // A change callback may run late, after a newer change: always sync to the current value,
        // never to the (possibly obsolete) one the callback was handed.
        _changeSubscription = options.OnChange((_, _) => Sync());
    }

    /// <summary>
    /// Decides whether the next request is sent compressed, and to which portal. The options are
    /// read inside the lock, so a delayed caller can never sync the state back to an older
    /// configuration.
    /// </summary>
    public Ticket Begin()
    {
        lock (_lock)
        {
            var options = _options.CurrentValue;
            SyncLocked(options);
            var suspended = _suspendedSince is { } since && _time.GetElapsedTime(since) < SuspendFor;
            return new Ticket(_generation, options.GzipRequests && !_disabled && !suspended, options.BaseUrl ?? "");
        }
    }

    /// <summary>Whether the portal has accepted a compressed request under the ticket's configuration.</summary>
    public bool IsProven(Ticket ticket)
    {
        lock (_lock)
        {
            SyncLocked(_options.CurrentValue);
            return ticket.Generation == _generation && _proven;
        }
    }

    /// <summary>Records that the portal accepted a compressed request (ignored for a stale ticket).</summary>
    public void MarkAccepted(Ticket ticket)
    {
        lock (_lock)
        {
            SyncLocked(_options.CurrentValue);
            if (ticket.Generation != _generation)
                return;
            _proven = true;
            _ambiguousRefusals = 0;
            _suspendedSince = null;
        }
    }

    /// <summary>
    /// The portal explicitly refused the encoding: switches compression off and logs
    /// <see cref="FallbackWarning"/>, once (ignored for a stale ticket).
    /// </summary>
    /// <param name="ticket">The refused request's ticket.</param>
    /// <param name="reason">What the portal answered, for the log.</param>
    public void Disable(Ticket ticket, string reason)
    {
        lock (_lock)
        {
            SyncLocked(_options.CurrentValue);
            if (ticket.Generation != _generation || _disabled)
                return;
            _disabled = true;
            _proven = false;
        }
        LogOff(reason);
    }

    /// <summary>
    /// The portal refused a compressed request in a way that may or may not be about the encoding
    /// (an HTTPS 400 that the uncompressed body then passed). Unless compression has been proven
    /// under this configuration (checked atomically, here), it is suspended for
    /// <see cref="SuspendFor"/>; the <see cref="MaxAmbiguousRefusals"/>th refusal in a row switches it
    /// off. Ignored for a stale ticket.
    /// </summary>
    public void RefusedAmbiguously(Ticket ticket, string reason)
    {
        bool off;
        lock (_lock)
        {
            SyncLocked(_options.CurrentValue);
            if (ticket.Generation != _generation || _disabled || _proven)
                return;
            _ambiguousRefusals++;
            off = _ambiguousRefusals >= MaxAmbiguousRefusals;
            if (off)
                _disabled = true;
            else
                _suspendedSince = _time.GetTimestamp();
        }

        if (off)
            LogOff($"{reason}, {MaxAmbiguousRefusals} times in a row");
        else
        {
            _logger.LogWarning(
                "Portal refused a compressed request ({Reason}) but accepted it uncompressed; sending uncompressed for " +
                "{Minutes:0} min, then trying compression again", reason, SuspendFor.TotalMinutes);
        }
    }

    private void LogOff(string reason) =>
        _logger.LogWarning(FallbackWarning + " (the portal answered {Reason} to a compressed request); " +
                           "compression stays off until the service restarts or {Section}:CompressRequests or " +
                           "BaseUrl changes", reason, PortalOptions.SectionName);

    private void Sync()
    {
        lock (_lock)
            SyncLocked(_options.CurrentValue);
    }

    /// <summary>
    /// A changed portal URL or compression setting starts over: a new generation (outstanding
    /// tickets go stale), compression on, not proven, no suspension.
    /// </summary>
    private void SyncLocked(PortalOptions options)
    {
        var key = KeyOf(options);
        if (key == _key)
            return;
        _key = key;
        _generation++;
        _disabled = false;
        _proven = false;
        _ambiguousRefusals = 0;
        _suspendedSince = null;
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