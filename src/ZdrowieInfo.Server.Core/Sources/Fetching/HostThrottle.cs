using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Sources.Extraction;

namespace ZdrowieInfo.Server.Sources.Fetching;

/// <summary>Jedno żądanie naraz do danej domeny i 2–3 s odstępu (host bez „www.”).</summary>
public sealed class HostThrottle(TimeProvider time, IOptions<SourcesOptions> options)
{
    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public DateTimeOffset NextAllowed = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string url, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(UrlNormalizer.HostKey(new Uri(url).Host), _ => new Gate());
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var wait = gate.NextAllowed - time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
        }
        catch
        {
            gate.Semaphore.Release();
            throw;
        }
        return new Releaser(() =>
        {
            var o = options.Value;
            var spread = (o.MaxDelayPerHost - o.MinDelayPerHost).TotalMilliseconds;
            gate.NextAllowed = time.GetUtcNow() + o.MinDelayPerHost + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Math.Max(0, spread));
            gate.Semaphore.Release();
        });
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }
}
