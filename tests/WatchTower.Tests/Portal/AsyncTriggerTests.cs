using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using WatchTower.Portal.Actions;

namespace WatchTower.Tests.Portal;

public class AsyncTriggerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    [Fact]
    public async Task WaitAsync_SignalWakesWaiterBeforeDelay()
    {
        var trigger = new MetricSnapshotTrigger();
        var wait = trigger.WaitAsync(TimeSpan.FromHours(1), _time, CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        trigger.Signal();

        (await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task WaitAsync_DelayElapses_ReturnsFalse()
    {
        var trigger = new ConfigSyncTrigger();
        var wait = trigger.WaitAsync(TimeSpan.FromSeconds(30), _time, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(30));

        (await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_PendingSignal_ReturnsImmediately_AndSignalsCoalesce()
    {
        var trigger = new MetricSnapshotTrigger();
        trigger.Signal();
        trigger.Signal();

        (await trigger.WaitAsync(TimeSpan.FromHours(1), _time, CancellationToken.None)).Should().BeTrue();
        trigger.TryConsume().Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_Cancelled_Throws()
    {
        var trigger = new ConfigSyncTrigger();
        using var cts = new CancellationTokenSource();
        var wait = trigger.WaitAsync(TimeSpan.FromHours(1), _time, cts.Token);

        await cts.CancelAsync();

        await wait.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Signal_AfterTimedOutWait_IsNotLost()
    {
        var trigger = new ConfigSyncTrigger();
        var wait = trigger.WaitAsync(TimeSpan.FromSeconds(1), _time, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        (await wait).Should().BeFalse();

        trigger.Signal();

        trigger.TryConsume().Should().BeTrue();
    }
}