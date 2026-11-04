namespace WatchTower.Portal.Actions;

/// <summary>
/// A coalescing, awaitable wake-up signal: any number of <see cref="Signal"/> calls before the
/// consumer wakes collapse into one. Lets a worker sleep for its poll interval yet react
/// immediately when a portal command asks it to run now.
/// </summary>
public class AsyncTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending; signals coalesce.
        }
    }

    /// <summary>Consumes a pending signal without waiting.</summary>
    public bool TryConsume() => _signal.Wait(0);

    /// <summary>
    /// Waits until the trigger is signalled (consuming the signal) or <paramref name="delay"/>
    /// elapses on <paramref name="timeProvider"/>, whichever comes first.
    /// </summary>
    /// <returns><c>true</c> when woken by a signal, <c>false</c> when the delay elapsed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<bool> WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken ct)
    {
        if (TryConsume())
            return true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signalled = _signal.WaitAsync(cts.Token);
        var elapsed = Task.Delay(delay, timeProvider, cts.Token);

        await Task.WhenAny(signalled, elapsed);
        await cts.CancelAsync();

        try
        {
            // The semaphore wait may have completed just before it was cancelled; observing its
            // outcome (instead of trusting WhenAny) guarantees a signal is never lost.
            await signalled;
            return true;
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            try
            {
                await elapsed;
            }
            catch (OperationCanceledException)
            {
                // expected when the signal won
            }
        }
    }
}