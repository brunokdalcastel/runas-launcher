namespace RunAsLauncher;

/// <summary>Janela deslizante por chave (SID): no máximo <c>limit</c> requisições por <c>window</c>.</summary>
internal sealed class RateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.OrdinalIgnoreCase);

    public RateLimiter(int limit, TimeSpan window, TimeProvider? time = null)
    {
        _limit = limit;
        _window = window;
        _time = time ?? TimeProvider.System;
    }

    public bool TryAcquire(string key)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_hits)
        {
            if (!_hits.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _hits[key] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() >= _window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= _limit)
            {
                return false;
            }

            queue.Enqueue(now);

            // Evita crescimento sem limite: descarta chaves ociosas de vez em quando.
            if (_hits.Count > 1024)
            {
                foreach (var stale in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() >= _window).Select(kv => kv.Key).ToList())
                {
                    _hits.Remove(stale);
                }
            }

            return true;
        }
    }
}
