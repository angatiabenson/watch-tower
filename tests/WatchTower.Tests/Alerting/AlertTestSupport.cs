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

/// <summary>Captures notifications instead of delivering them.</summary>
internal sealed class RecordingDispatcher : INotificationDispatcher
{
    private readonly List<AlertNotification> _sent = [];

    /// <summary>What <see cref="DispatchAsync"/> reports as the number of channels used.</summary>
    public int Channels { get; set; } = 1;

    public IReadOnlyList<AlertNotification> Sent
    {
        get
        {
            lock (_sent)
                return _sent.ToList();
        }
    }

    public Task<int> DispatchAsync(AlertNotification notification, CancellationToken ct)
    {
        lock (_sent)
            _sent.Add(notification);
        return Task.FromResult(Channels);
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
        IAlertStateStore? state = null) =>
        new(
            state ?? new InMemoryAlertStateStore(),
            metricsStore,
            portal ?? new NullPortalEventPublisher(),
            dispatcher,
            new TestOptionsMonitor<AlertingOptions>(options ?? new AlertingOptions()),
            time,
            NullLogger<AlertManager>.Instance,
            "TEST-SRV");

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
            dispatcher, monitor, time, NullLogger<AlertManager>.Instance, "TEST-SRV");
    }
}