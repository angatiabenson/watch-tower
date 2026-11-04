using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.Alerting;

/// <summary>Per-key lifecycle locks: exclusive per key, and gone once nobody holds or waits for them.</summary>
public class KeyedAsyncLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly KeyedAsyncLock _locks = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ALockIsRemovedWhenItsLastHolderReleasesIt()
    {
        for (var i = 0; i < 1000; i++)
        {
            using var held = await _locks.LockAsync($"key{i}", Ct).Bounded();
            _locks.Count.Should().Be(1);
        }

        _locks.Count.Should().Be(0);
    }

    [Fact]
    public async Task AWaiterGetsTheLockWhenItIsReleased_AndKeepsTheEntryUntilItLeaves()
    {
        var first = await _locks.LockAsync("disk:C:\\", Ct).Bounded();
        var second = _locks.LockAsync("DISK:c:\\", Ct); // same key, case-insensitive
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        var held = await second.Bounded();
        _locks.Count.Should().Be(1, "the waiter now holds it");
        first.Dispose(); // a second release is ignored
        _locks.Count.Should().Be(1);

        var third = _locks.LockAsync("disk:C:\\", Ct);
        third.IsCompleted.Should().BeFalse("the waiter's entry is still the key's lock");
        held.Dispose();
        (await third.Bounded()).Dispose();
        _locks.Count.Should().Be(0);
    }

    [Fact]
    public async Task AWaiterThatGivesUp_LeavesNothingBehind()
    {
        var held = await _locks.LockAsync("svc:W3SVC", Ct).Bounded();
        using var cts = new CancellationTokenSource();
        var waiting = _locks.LockAsync("svc:W3SVC", cts.Token);
        await cts.CancelAsync();
        var act = () => waiting.Bounded();
        await act.Should().ThrowAsync<OperationCanceledException>();
        _locks.Count.Should().Be(1, "the holder still has it");

        held.Dispose();
        _locks.Count.Should().Be(0);

        // Already cancelled before waiting: nothing is left behind either.
        var cancelled = () => _locks.LockAsync("svc:W3SVC", cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        _locks.Count.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentChurn_IsExclusivePerKey_AndLeavesNoLocks()
    {
        const int keys = 8;
        var inside = new int[keys];
        var entries = new int[keys];
        var violations = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var workers = Enumerable.Range(0, 32).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            for (var i = 0; i < 500; i++)
            {
                var k = random.Next(keys);
                // Sometimes give up waiting, to exercise removal by a waiter that leaves.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                if (random.Next(10) == 0)
                    timeout.Cancel();
                IDisposable held;
                try
                {
                    held = await _locks.LockAsync($"key{k}", timeout.Token);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    continue;
                }

                using (held)
                {
                    if (Interlocked.Increment(ref inside[k]) != 1)
                        Interlocked.Increment(ref violations);
                    Interlocked.Increment(ref entries[k]);
                    if (random.Next(3) == 0)
                        await Task.Yield();
                    Interlocked.Decrement(ref inside[k]);
                }
            }
        }, cts.Token)).ToList();

        await Task.WhenAll(workers).Bounded();

        violations.Should().Be(0, "two callers never hold the same key's lock at once");
        entries.Sum().Should().BeGreaterThan(0);
        _locks.Count.Should().Be(0);
    }

    // ── Through the alert manager ──────────────────────────────────────────

    [Fact]
    public async Task ResolvingKeysWithoutAlerts_LeavesNoLocksBehind()
    {
        // The reviewer's probe: healthy resources resolved on every check, never alerted.
        var alerts = TestAlerts.Create(Mock.Of<IMetricsStore>(), new FakeTimeProvider(), new RecordingDispatcher());
        for (var i = 0; i < 1000; i++)
            (await alerts.ResolveAsync(AlertKey.Network("transient" + i), ct: Ct).Bounded()).Should().BeFalse();

        alerts.KeyLockCount.Should().Be(0);
        alerts.GetOpenAlerts().Should().BeEmpty();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentRaisesAndResolves_LeaveNoLocksBehind_AndNoAlertHalfClosed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var dispatcher = new RecordingDispatcher();
        var alerts = TestAlerts.Create(Mock.Of<IMetricsStore>(), time, dispatcher);

        var workers = Enumerable.Range(0, 16).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            for (var i = 0; i < 100; i++)
            {
                var key = AlertKey.Disk($"{random.Next(6)}:\\");
                if (random.Next(2) == 0)
                    await alerts.RaiseAsync(key, AlertSeverity.Critical, "Disk full",
                        new Incident(time.GetUtcNow().UtcDateTime, IncidentType.DiskCritical, "full"), Ct);
                else
                    await alerts.ResolveAsync(key, ct: Ct);
            }
        }, Ct)).ToList();
        await Task.WhenAll(workers).Bounded();

        alerts.KeyLockCount.Should().Be(0);
        alerts.InFlightCount.Should().Be(0);

        // The clock stood still: what exceeded the channel's burst goes out as its budget refills.
        for (var i = 0; i < 20 && alerts.GetPendingDeliveries().Count > 0; i++)
        {
            time.Advance(NotificationDeliveryWorker.Tick);
            await alerts.ProcessPendingAsync(Ct).Bounded();
        }
        alerts.GetPendingDeliveries().Should().BeEmpty("every notice was delivered");

        // Each key's notices alternate opened / resolved: none was sent twice or out of order.
        foreach (var notices in dispatcher.Sent.GroupBy(n => n.AlertKey, StringComparer.OrdinalIgnoreCase))
        {
            var kinds = notices.Select(n => n.Kind).ToList();
            for (var i = 0; i < kinds.Count; i++)
                kinds[i].Should().Be(i % 2 == 0 ? AlertNotificationKind.Opened : AlertNotificationKind.Resolved,
                    $"{notices.Key}: {string.Join(",", kinds)}");
        }
    }
}