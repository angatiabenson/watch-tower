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

    public Task<DispatchResult> DispatchAsync(AlertNotification notification, IReadOnlySet<string>? skipChannels, CancellationToken ct)
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
        return Task.FromResult(new DispatchResult(results));
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