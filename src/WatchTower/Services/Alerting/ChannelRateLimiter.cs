namespace WatchTower.Services.Alerting;

/// <summary>
/// Bounds the delivery attempts made to each notification channel, across every alert key, so a
/// channel that is down costs a fixed number of attempts however many notices are waiting for it.
/// It only says whether a channel may take one more attempt now; which notice that attempt
/// carries is decided by <see cref="AlertManager"/>'s scheduler (the channel's highest-priority
/// eligible notice).
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
/// A critical early probe: while the circuit is open, a new critical opening or recovery queued
/// for the channel (<see cref="NoteCritical"/>) lets one probe through before the backoff has
/// passed, at most once per critical-probe cooldown per channel. It shares the single probe lease,
/// so it never runs next to another probe.
/// </para>
/// <para>
/// Worst case for a channel that keeps failing: <see cref="MaxConcurrent"/> attempts in flight
/// when it first fails, then one probe after 1, 3, 7, 15 and 31 minutes — at most
/// <see cref="MaxConcurrent"/> + 5 = 9 attempts in its first hour — and one probe every 30
/// minutes (2 an hour) after that. Critical early probes add at most one per cooldown (30 an hour
/// at the default 2 minutes), and only while new critical notices keep being queued for it.
/// </para>
/// <para>
/// Time: within a process every delay is measured on the monotonic clock
/// (<see cref="TimeProvider.GetTimestamp"/>), so a wall-clock change neither shortens nor extends
/// a backoff. Only the circuit's UTC probe time is persisted (for a restart); a restored one is
/// clamped to now + the circuit's backoff, so a clock that went back cannot extend it.
/// </para>
/// Not thread-safe: <see cref="AlertManager"/> only uses it under its gate.
/// </summary>
internal sealed class ChannelRateLimiter(TimeProvider time, Func<TimeSpan> criticalProbeCooldown)
{
    public ChannelRateLimiter(TimeProvider time, TimeSpan criticalProbeCooldown)
        : this(time, () => criticalProbeCooldown)
    {
    }

    /// <summary>Attempts a channel may take at once (token bucket capacity).</summary>
    internal const int Burst = 20;

    /// <summary>One more attempt is allowed per channel this often (at most 1,800 an hour).</summary>
    internal static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(2);

    /// <summary>Most attempts in flight per channel at the same time.</summary>
    internal const int MaxConcurrent = 4;

    /// <summary>
    /// Attempts to a channel that keeps failing within its first hour, without critical early
    /// probes (see the remarks).
    /// </summary>
    internal const int WorstCaseFirstHourAttempts = MaxConcurrent + 5;

    /// <summary>Critical early probes a channel that keeps failing takes per hour, at most.</summary>
    internal int MaxCriticalProbesPerHour => (int)Math.Ceiling(TimeSpan.FromHours(1) / criticalProbeCooldown());

    private sealed class Health(long now)
    {
        public int Failures;

        /// <summary>When the circuit lets its next probe through (monotonic deadline).</summary>
        public MonotonicDeadline RetryAt;

        /// <summary>The same, as persisted (UTC).</summary>
        public DateTime RetryAtUtc;

        public bool Probing;
        public int Active;
        public double Tokens = Burst;
        public long RefilledAt = now;

        /// <summary>A new critical notice is queued for the channel while its circuit is open.</summary>
        public bool CriticalWaiting;

        /// <summary>When the last critical early probe was let through (monotonic), if any.</summary>
        public long? LastEarlyProbe;
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
    /// A new critical opening or recovery was queued for <paramref name="channel"/>: if its circuit
    /// is open, it may be probed early (once its critical-probe cooldown has passed).
    /// </summary>
    public void NoteCritical(string channel)
    {
        if (_channels.GetValueOrDefault(channel) is { Failures: > 0 } health)
            health.CriticalWaiting = true;
    }

    /// <summary>
    /// Whether <paramref name="channel"/> may take an attempt now (no side effect apart from
    /// refilling its bucket): its circuit is closed, or its probe (regular or critical early) is
    /// due and not in flight, and it has an attempt slot and a token left.
    /// <paramref name="earlyProbe"/>: the attempt would be a critical early probe. It tests the
    /// channel, so the notice it carries goes even if its own retry deadline on the channel has not
    /// passed (the cooldown bounds these attempts).
    /// </summary>
    public bool CanAcquire(string channel, out bool earlyProbe)
    {
        var admitted = Admission(channel, out _, out earlyProbe) is not null;
        earlyProbe &= admitted;
        return admitted;
    }

    /// <summary>
    /// Takes the permission to attempt <paramref name="channel"/> now, or null if its circuit is
    /// open, its probe is in flight, or it has no attempt left right now.
    /// </summary>
    public Lease? TryAcquire(string channel)
    {
        if (Admission(channel, out var probe, out var early) is not { } health)
            return null;

        health.Tokens -= 1;
        health.Active++;
        if (probe)
        {
            health.Probing = true;
            health.CriticalWaiting = false; // this probe answers the critical notice's question too
            if (early)
                health.LastEarlyProbe = time.GetTimestamp();
        }
        return new Lease(channel, health.Failures, probe);
    }

    private Health? Admission(string channel, out bool probe, out bool early)
    {
        var health = Get(channel);
        Refill(health);
        probe = health.Failures > 0;
        early = false;
        if (probe)
        {
            if (health.Probing)
                return null;
            if (!health.RetryAt.HasPassed(time))
            {
                early = health.CriticalWaiting &&
                        (health.LastEarlyProbe is not { } last || time.GetElapsedTime(last) >= criticalProbeCooldown());
                if (!early)
                    return null;
            }
        }
        if (health.Active >= MaxConcurrent || health.Tokens < 1)
            return null;
        return health;
    }

    /// <summary>
    /// Records the outcome of a leased attempt: <c>true</c> delivered, <c>false</c> failed, null
    /// none (cancelled, or not reported). Returns true when the circuit changed (to be persisted).
    /// </summary>
    public bool Complete(Lease lease, bool? success)
    {
        if (lease.Completed)
            return false;
        lease.Completed = true;

        var health = Get(lease.Channel);
        health.Active = Math.Max(0, health.Active - 1);
        if (lease.Probe)
            health.Probing = false;
        return success is { } ok && Record(health, ok, lease.FailuresAtGrant);
    }

    /// <summary>An outcome reported for a channel that was not leased (a dispatcher sent it anyway).</summary>
    public bool RecordUnleased(string channel, bool success)
    {
        var health = Get(channel);
        return Record(health, success, health.Failures);
    }

    private bool Record(Health health, bool success, int failuresAtGrant)
    {
        if (success)
        {
            if (health.Failures == 0)
                return false;
            health.Failures = 0;
            health.RetryAt = default;
            health.RetryAtUtc = default;
            health.CriticalWaiting = false;
            health.LastEarlyProbe = null;
            return true;
        }

        // An attempt granted before a failure already counted is part of the same failure.
        if (health.Failures != failuresAtGrant)
            return false;
        health.Failures++;
        var backoff = NotificationRetryPolicy.Backoff(health.Failures);
        health.RetryAt = MonotonicDeadline.After(time, backoff);
        health.RetryAtUtc = time.GetUtcNow().UtcDateTime + backoff;
        return true;
    }

    /// <summary>The open circuits, as persisted.</summary>
    public Dictionary<string, ChannelCircuit> Snapshot() =>
        _channels.Where(c => c.Value.Failures > 0)
            .ToDictionary(c => c.Key, c => new ChannelCircuit(c.Value.Failures, c.Value.RetryAtUtc), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Restores persisted circuits. A probe time further away than the circuit's own backoff (the
    /// clock went back since it was written) is brought forward to now + that backoff.
    /// </summary>
    public void Restore(IReadOnlyDictionary<string, ChannelCircuit> circuits)
    {
        var utcNow = time.GetUtcNow().UtcDateTime;
        foreach (var (channel, circuit) in circuits)
        {
            if (circuit.Failures <= 0)
                continue;
            var health = Get(channel);
            health.Failures = circuit.Failures;
            var remaining = MonotonicDeadline.Clamp(circuit.RetryAt - utcNow, NotificationRetryPolicy.Backoff(circuit.Failures));
            health.RetryAt = MonotonicDeadline.After(time, remaining);
            health.RetryAtUtc = utcNow + remaining;
        }
    }

    private Health Get(string channel)
    {
        if (!_channels.TryGetValue(channel, out var health))
            _channels[channel] = health = new Health(time.GetTimestamp());
        return health;
    }

    private void Refill(Health health)
    {
        var now = time.GetTimestamp();
        var elapsed = time.GetElapsedTime(health.RefilledAt, now);
        if (elapsed <= TimeSpan.Zero)
            return;
        health.Tokens = Math.Min(Burst, health.Tokens + elapsed / RefillInterval);
        health.RefilledAt = now;
    }
}

/// <summary>A channel's persisted circuit: consecutive failures and when it may be probed again (UTC).</summary>
public sealed record ChannelCircuit(int Failures, DateTime RetryAt);

/// <summary>
/// A deadline on the monotonic clock (<see cref="TimeProvider.GetTimestamp"/>): a start and a
/// delay. The default value has passed already.
/// </summary>
internal readonly record struct MonotonicDeadline(long Start, TimeSpan Delay)
{
    public static MonotonicDeadline After(TimeProvider time, TimeSpan delay) => new(time.GetTimestamp(), delay);

    public bool HasPassed(TimeProvider time) => Delay <= TimeSpan.Zero || time.GetElapsedTime(Start) >= Delay;

    /// <summary>
    /// What is left of a persisted UTC wait, bounded by <paramref name="max"/>: a clock that went
    /// back since it was written cannot make it longer than the policy allows, nor negative.
    /// </summary>
    public static TimeSpan Clamp(TimeSpan remaining, TimeSpan max) =>
        remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining > max ? max : remaining;
}