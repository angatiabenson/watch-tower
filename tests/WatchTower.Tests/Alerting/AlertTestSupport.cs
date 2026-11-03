using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Tests.Alerting;

/// <summary>Options monitor with a fixed (but replaceable) value.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>In-memory <see cref="IAlertStateStore"/>.</summary>
internal sealed class InMemoryAlertStateStore : IAlertStateStore
{
    private readonly Dictionary<string, AlertState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AlertState> LoadAll()
    {
        lock (_states)
            return _states.Values.ToList();
    }

    public void Upsert(AlertState state)
    {
        lock (_states)
            _states[state.Key] = state;
    }

    private readonly Dictionary<string, PendingDelivery> _deliveries = new(StringComparer.Ordinal);

    public IReadOnlyList<PendingDelivery> LoadDeliveries()
    {
        lock (_deliveries)
            return _deliveries.Values.OrderBy(d => d.CreatedAt).ToList();
    }

    public void UpsertDelivery(PendingDelivery delivery)
    {
        lock (_deliveries)
            _deliveries[delivery.Id] = delivery;
    }

    public void DeleteDelivery(string id)
    {
        lock (_deliveries)
            _deliveries.Remove(id);
    }

    public string? GetValue(string key)
    {
        lock (_values)
            return _values.GetValueOrDefault(key);
    }

    public void SetValue(string key, string value)
    {
        lock (_values)
            _values[key] = value;
    }
}

/// <summary>
/// <see cref="IAlertStateStore"/> whose next delivery write matching a condition throws (a
/// transient database error), once; every other call goes to <see cref="Inner"/>.
/// </summary>
internal sealed class FlakyAlertStateStore(IAlertStateStore inner) : IAlertStateStore
{
    private Func<PendingDelivery, bool>? _failNext;

    public IAlertStateStore Inner { get; } = inner;

    /// <summary>How many writes were failed so far.</summary>
    public int FailedWrites { get; private set; }

    /// <summary>Fails the next <see cref="UpsertDelivery"/> whose delivery matches <paramref name="when"/>.</summary>
    public void FailNextDeliveryWrite(Func<PendingDelivery, bool> when) => _failNext = when;

    public IReadOnlyList<AlertState> LoadAll() => Inner.LoadAll();
    public void Upsert(AlertState state) => Inner.Upsert(state);
    public IReadOnlyList<PendingDelivery> LoadDeliveries() => Inner.LoadDeliveries();

    public void UpsertDelivery(PendingDelivery delivery)
    {
        if (_failNext is { } when && when(delivery))
        {
            _failNext = null;
            FailedWrites++;
            throw new IOException("Transient store write failure");
        }
        Inner.UpsertDelivery(delivery);
    }

    public void DeleteDelivery(string id) => Inner.DeleteDelivery(id);
    public string? GetValue(string key) => Inner.GetValue(key);
    public void SetValue(string key, string value) => Inner.SetValue(key, value);
}

/// <summary>
/// Portal publisher whose alert publications wait for the test to <see cref="Release"/> them, so a
/// test can change state (start a maintenance window) while a raise or close awaits the portal.
/// </summary>
internal sealed class GatedPortalPublisher : WatchTower.Portal.IPortalEventPublisher
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first alert publication starts.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Which publications wait (opened: <c>false</c>, resolved: <c>true</c>, events: null).</summary>
    public Func<bool?, bool> Gates { get; set; } = _ => true;

    public void Release() => _release.TrySetResult();

    public Task PublishIncidentAsync(WatchTower.Models.Incident incident, CancellationToken ct) => Task.CompletedTask;

    public Task PublishAlertEventAsync(WatchTower.Models.Incident incident, string alertKey, bool? resolved, CancellationToken ct)
    {
        if (!Gates(resolved))
            return Task.CompletedTask;
        Entered.TrySetResult();
        return _release.Task.WaitAsync(ct);
    }
}

/// <summary>Maintenance window a test can open and close.</summary>
internal sealed class TestMaintenance : IMaintenanceState
{
    public bool Active { get; set; }
    public MaintenanceWindow Current => new(Active, Active ? DateTimeOffset.UtcNow.AddHours(1) : null, Active ? "patching" : null);
}

/// <summary>
/// Captures notifications instead of delivering them. Channels are named <c>ch1</c>..<c>chN</c>;
/// those in <see cref="Failing"/> report a failed send.
/// </summary>
internal sealed class RecordingDispatcher : INotificationDispatcher
{
    private readonly List<AlertNotification> _sent = [];
    private readonly List<(string Channel, AlertNotification Notification)> _delivered = [];

    /// <summary>How many channels are configured.</summary>
    public int Channels { get; set; } = 1;

    /// <summary>Channel names whose sends fail.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every dispatch call (whether or not any channel delivered it).</summary>
    public IReadOnlyList<AlertNotification> Sent
    {
        get
        {
            lock (_sent)
                return _sent.ToList();
        }
    }

    /// <summary>Every successful channel delivery.</summary>
    public IReadOnlyList<(string Channel, AlertNotification Notification)> Delivered
    {
        get
        {
            lock (_sent)
                return _delivered.ToList();
        }
    }

    public Task<DispatchResult> DispatchAsync(
        AlertNotification notification, IReadOnlySet<string>? skipChannels, Action<ChannelDelivery>? onChannelCompleted, CancellationToken ct)
    {
        var results = DispatchCore(notification, skipChannels);
        foreach (var result in results)
            onChannelCompleted?.Invoke(result);
        return Task.FromResult(new DispatchResult(results));
    }

    private List<ChannelDelivery> DispatchCore(AlertNotification notification, IReadOnlySet<string>? skipChannels)
    {
        var results = new List<ChannelDelivery>();
        lock (_sent)
        {
            _sent.Add(notification);
            for (var i = 1; i <= Channels; i++)
            {
                var name = $"ch{i}";
                if (skipChannels?.Contains(name) == true)
                    continue;
                var ok = !Failing.Contains(name);
                results.Add(new ChannelDelivery(name, ok, ok ? "Delivered" : "Rejected with HTTP 503"));
                if (ok)
                    _delivered.Add((name, notification));
            }
        }
        return results;
    }
}

/// <summary>
/// Holds every dispatch in flight until the test completes it, so a test can interleave other
/// calls with a send deterministically. Calls are handed out in order by <see cref="NextAsync"/>.
/// </summary>
internal sealed class GatedDispatcher : INotificationDispatcher
{
    private readonly System.Threading.Channels.Channel<GatedCall> _calls =
        System.Threading.Channels.Channel.CreateUnbounded<GatedCall>();

    private readonly List<AlertNotification> _sent = [];

    /// <summary>Every dispatch call, in order.</summary>
    public IReadOnlyList<AlertNotification> Sent
    {
        get
        {
            lock (_sent)
                return _sent.ToList();
        }
    }

    public Task<DispatchResult> DispatchAsync(
        AlertNotification notification, IReadOnlySet<string>? skipChannels, Action<ChannelDelivery>? onChannelCompleted, CancellationToken ct)
    {
        var call = new GatedCall(notification, skipChannels, onChannelCompleted);
        lock (_sent)
            _sent.Add(notification);
        ct.Register(() => call.Result.TrySetCanceled(ct));
        _calls.Writer.TryWrite(call);
        return call.Result.Task;
    }

    /// <summary>The next dispatch call (fails the test rather than hanging when none comes).</summary>
    public async Task<GatedCall> NextAsync(CancellationToken ct) =>
        await _calls.Reader.ReadAsync(ct).AsTask().WaitAsync(BoundedWait.Limit, ct);

    /// <summary>Whether a dispatch call is waiting to be taken by <see cref="NextAsync"/>.</summary>
    public bool HasWaitingCall => _calls.Reader.Count > 0;
}

internal static class BoundedWait
{
    /// <summary>Generous bound for a step that should complete promptly: fails a test instead of hanging it.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static Task Bounded(this Task task) => task.WaitAsync(Limit, TestContext.Current.CancellationToken);

    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(Limit, TestContext.Current.CancellationToken);
}

/// <summary>One dispatch held by <see cref="GatedDispatcher"/>.</summary>
internal sealed class GatedCall(
    AlertNotification notification, IReadOnlySet<string>? skipChannels, Action<ChannelDelivery>? onChannelCompleted)
{
    public AlertNotification Notification { get; } = notification;
    public IReadOnlySet<string>? SkipChannels { get; } = skipChannels;

    public TaskCompletionSource<DispatchResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the send: every named channel delivered it.</summary>
    public void Deliver(params string[] channels) => Complete(channels.Select(c => new ChannelDelivery(c, true, "Delivered")));

    /// <summary>Completes the send: every named channel failed.</summary>
    public void Fail(params string[] channels) =>
        Complete(channels.Select(c => new ChannelDelivery(c, false, "Rejected with HTTP 503")));

    private void Complete(IEnumerable<ChannelDelivery> outcomes)
    {
        var list = outcomes.ToList();
        foreach (var outcome in list)
            onChannelCompleted?.Invoke(outcome);
        Result.SetResult(new DispatchResult(list));
    }
}

internal static class TestAlerts
{
    public static AlertManager Create(
        IMetricsStore metricsStore,
        TimeProvider time,
        INotificationDispatcher dispatcher,
        AlertingOptions? options = null,
        IPortalEventPublisher? portal = null,
        IAlertStateStore? state = null,
        IMaintenanceState? maintenance = null) =>
        new(
            state ?? new InMemoryAlertStateStore(),
            metricsStore,
            portal ?? new NullPortalEventPublisher(),
            dispatcher,
            new TestOptionsMonitor<AlertingOptions>(options ?? new AlertingOptions()),
            time,
            NullLogger<AlertManager>.Instance,
            maintenance,
            serverName: "TEST-SRV");

    /// <summary>An alert manager that delivers through the real email dispatcher (backed by mocks).</summary>
    public static AlertManager WithEmail(
        IMetricsStore metricsStore,
        TimeProvider time,
        IEmailService email,
        IReportBuilder reportBuilder,
        AlertingOptions? options = null,
        IPortalEventPublisher? portal = null)
    {
        var monitor = new TestOptionsMonitor<AlertingOptions>(options ?? new AlertingOptions());
        var dispatcher = new NotificationDispatcher(
            [new EmailNotificationChannel(email, reportBuilder, monitor)],
            new TestOptionsMonitor<NotificationOptions>(new NotificationOptions()),
            NullLogger<NotificationDispatcher>.Instance);
        return new AlertManager(new InMemoryAlertStateStore(), metricsStore, portal ?? new NullPortalEventPublisher(),
            dispatcher, monitor, time, NullLogger<AlertManager>.Instance, serverName: "TEST-SRV");
    }
}