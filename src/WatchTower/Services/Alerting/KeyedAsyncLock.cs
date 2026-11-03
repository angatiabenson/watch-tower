namespace WatchTower.Services.Alerting;

/// <summary>
/// Async mutual exclusion per key. A key's entry exists only while some caller holds or waits for
/// its lock: the first caller creates it and the last one to leave (by releasing, or by giving up
/// waiting) removes it, so keys that come and go (resources checked once and never alerted, removed
/// disks) do not accumulate.
/// </summary>
internal sealed class KeyedAsyncLock(IEqualityComparer<string> comparer)
{
    private sealed class Entry
    {
        // No wait handle is ever requested, so there is nothing to dispose.
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holders and waiters (guarded by <see cref="_entries"/>).</summary>
        public int References;
    }

    private readonly Dictionary<string, Entry> _entries = new(comparer);

    /// <summary>Keys currently held or waited for.</summary>
    public int Count
    {
        get
        {
            lock (_entries)
                return _entries.Count;
        }
    }

    /// <summary>Waits for the lock of <paramref name="key"/>; disposing the result releases it.</summary>
    public async Task<IDisposable> LockAsync(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            // Counted before waiting: the entry cannot be removed while this caller may still use it.
            if (!_entries.TryGetValue(key, out var existing))
                _entries[key] = existing = new Entry();
            entry = existing;
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (_entries)
        {
            // The entry in the dictionary is always this one: a new one for the key is only
            // created after this one was removed, i.e. once nobody references it any more.
            if (--entry.References == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            // Released before leaving: a waiter still counts as a reference, so the entry it was
            // handed to stays in place until it leaves too.
            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }
}