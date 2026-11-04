namespace WatchTower.Services.Alerting;

/// <summary>
/// Bounds the delivery attempts made to each notification channel, across every alert key, so a
/// channel that is down costs a fixed number of attempts however many notices are waiting for it.
/// <para>
/// Each channel has a circuit. A failed attempt opens it for
/// <see cref="NotificationRetryPolicy.Backoff"/> of the channel's consecutive failures (1, 2, 4,
/// 8, 16, then every 30 minutes); while it is open the channel is not attempted at all, and once
/// the backoff has passed exactly one attempt (a probe) is let through at a time. A success closes
/// it. Attempts that were already in flight when the circuit opened count as the same failure.
/// While the circuit is closed, at most <see cref="MaxConcurrent"/> attempts are in flight per
/// channel and a token bucket (<see cref="Burst"/> attempts, one more every
/// <see cref="RefillInterval"/>) paces the draining of a backlog.
/// </para>
/// <para>
/// Worst case for a channel that keeps failing: <see cref="MaxConcurrent"/> attempts in flight
/// when it first fails, then one probe after 1, 3, 7, 15 and 31 minutes — at most
/// <see cref="MaxConcurrent"/> + 5 = 9 attempts in its first hour — and one probe every 30
/// minutes (2 an hour) after that. The circuit (failures and the next probe time) is persisted, so
/// a restart does not reset it.
/// </para>
/// Not thread-safe: <see cref="AlertManager"/> only uses it under its gate.
/// </summary>
internal sealed class ChannelRateLimiter
{
    /// <summary>Attempts a channel may take at once (token bucket capacity).</summary>
    internal const int Burst = 20;

    /// <summary>One more attempt is allowed per channel this often (at most 1,800 an hour).</summary>
    internal static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(2);

    /// <summary>Most attempts in flight per channel at the same time.</summary>
    internal const int MaxConcurrent = 4;

    /// <summary>Attempts to a channel that keeps failing within its first hour (see the remarks).</summary>
    internal const int WorstCaseFirstHourAttempts = MaxConcurrent + 5;

    private sealed class Health(DateTime now)
    {
        public int Failures;
        public DateTime RetryAt;
        public bool Probing;
        public int Active;
        public double Tokens = Burst;
        public DateTime RefilledAt = now;
    }

    /// <summary>Permission for one attempt to one channel, completed with its outcome.</summary>
    internal sealed class Lease(string channel, int failuresAtGrant, bool probe)
    {
        public string Channel { get; } = channel;
        public int FailuresAtGrant { get; } = failuresAtGrant;
        public bool Probe { get; } = probe;
        public bool Completed { get; set; }
    }

    private readonly Dictionary<string, Health> _channels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="channel"/> failed its last attempt (its circuit is open or probing).</summary>
    public bool IsFailing(string channel) => _channels.GetValueOrDefault(channel) is { Failures: > 0 };

    /// <summary>
    /// Takes the permission to attempt <paramref name="channel"/> now, or null if its circuit is
    /// open, its probe is in flight, or it has no attempt left right now.
    /// </summary>
    public Lease? TryAcquire(string channel, DateTime now)
    {
        var health = Get(channel, now);
        Refill(health, now);

        var probe = health.Failures > 0;
        if (probe && (now < health.RetryAt || health.Probing))
            return null;
        if (health.Active >= MaxConcurrent || health.Tokens < 1)
            return null;

        health.Tokens -= 1;
        health.Active++;
        health.Probing |= probe;
        return new Lease(channel, health.Failures, probe);
    }

    /// <summary>
    /// Records the outcome of a leased attempt: <c>true</c> delivered, <c>false</c> failed, null
    /// none (cancelled, or not reported). Returns true when the circuit changed (to be persisted).
    /// </summary>
    public bool Complete(Lease lease, bool? success, DateTime now)
    {
        if (lease.Completed)
            return false;
        lease.Completed = true;

        var health = Get(lease.Channel, now);
        health.Active = Math.Max(0, health.Active - 1);
        if (lease.Probe)
            health.Probing = false;
        return success is { } ok && Record(health, ok, lease.FailuresAtGrant, now);
    }

    /// <summary>An outcome reported for a channel that was not leased (a dispatcher sent it anyway).</summary>
    public bool RecordUnleased(string channel, bool success, DateTime now)
    {
        var health = Get(channel, now);
        return Record(health, success, health.Failures, now);
    }

    private static bool Record(Health health, bool success, int failuresAtGrant, DateTime now)
    {
        if (success)
        {
            if (health.Failures == 0)
                return false;
            health.Failures = 0;
            health.RetryAt = default;
            return true;
        }

        // An attempt granted before a failure already counted is part of the same failure.
        if (health.Failures != failuresAtGrant)
            return false;
        health.Failures++;
        health.RetryAt = now + NotificationRetryPolicy.Backoff(health.Failures);
        return true;
    }

    /// <summary>The open circuits, as persisted.</summary>
    public Dictionary<string, ChannelCircuit> Snapshot() =>
        _channels.Where(c => c.Value.Failures > 0)
            .ToDictionary(c => c.Key, c => new ChannelCircuit(c.Value.Failures, c.Value.RetryAt), StringComparer.OrdinalIgnoreCase);

    /// <summary>Restores persisted circuits (a probe time beyond the longest backoff is brought forward).</summary>
    public void Restore(IReadOnlyDictionary<string, ChannelCircuit> circuits, DateTime now)
    {
        foreach (var (channel, circuit) in circuits)
        {
            if (circuit.Failures <= 0)
                continue;
            var health = Get(channel, now);
            health.Failures = circuit.Failures;
            var latest = now + NotificationRetryPolicy.MaxDelay;
            health.RetryAt = circuit.RetryAt > latest ? latest : circuit.RetryAt;
        }
    }

    private Health Get(string channel, DateTime now)
    {
        if (!_channels.TryGetValue(channel, out var health))
            _channels[channel] = health = new Health(now);
        return health;
    }

    private static void Refill(Health health, DateTime now)
    {
        if (now <= health.RefilledAt)
        {
            health.RefilledAt = now < health.RefilledAt ? now : health.RefilledAt; // clock went back
            return;
        }
        health.Tokens = Math.Min(Burst, health.Tokens + (now - health.RefilledAt) / RefillInterval);
        health.RefilledAt = now;
    }
}

/// <summary>A channel's persisted circuit: consecutive failures and when it may be probed again (UTC).</summary>
public sealed record ChannelCircuit(int Failures, DateTime RetryAt);