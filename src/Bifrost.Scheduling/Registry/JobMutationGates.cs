#pragma warning disable SA1633, SA1600

namespace Bifrost.Scheduling.Registry;

internal sealed class JobMutationGates
{
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (this.sync)
            {
                return this.entries.Count;
            }
        }
    }

    internal async ValueTask<IDisposable> EnterAsync(string name, CancellationToken ct = default)
    {
        Entry entry;
        lock (this.sync)
        {
            if (!this.entries.TryGetValue(name, out entry!))
            {
                entry = new Entry();
                this.entries.Add(name, entry);
            }

            entry.References++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            this.ReleaseReference(name, entry);
            throw;
        }

        return new Lease(this, name, entry);
    }

    private void ReleaseReference(string name, Entry entry)
    {
        lock (this.sync)
        {
            if (--entry.References == 0)
            {
                this.entries.Remove(name);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal int References { get; set; }
    }

    private sealed class Lease(JobMutationGates owner, string name, Entry entry) : IDisposable
    {
        private JobMutationGates? owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref this.owner, null);
            if (current is null)
            {
                return;
            }

            entry.Gate.Release();
            current.ReleaseReference(name, entry);
        }
    }
}
