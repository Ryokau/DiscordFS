using System.Collections.Concurrent;

namespace DiscordFS.Storage;

public class ChunkCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly object _evictionLock = new();
    private long _currentSizeBytes = 0;
    private readonly long _maxSizeBytes;

    public ChunkCache(int maxSizeMB = 256)
    {
        _maxSizeBytes = maxSizeMB * 1024L * 1024L;
    }

    public void Add(string key, byte[] data)
    {
        var entry = new CacheEntry
        {
            Data = data,
            LastAccess = DateTime.UtcNow,
            SizeBytes = data.Length
        };

        // Evict se necessário antes de adicionar
        EnsureSpace(data.Length);

        if (_cache.TryAdd(key, entry))
        {
            Interlocked.Add(ref _currentSizeBytes, data.Length);
        }
        else
        {
            // Atualizar entrada existente
            if (_cache.TryGetValue(key, out var existing))
            {
                Interlocked.Add(ref _currentSizeBytes, -existing.SizeBytes);
                _cache[key] = entry;
                Interlocked.Add(ref _currentSizeBytes, data.Length);
            }
        }
    }

    public byte[]? Get(string key)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            entry.LastAccess = DateTime.UtcNow;
            return entry.Data;
        }
        return null;
    }

    public bool TryGet(string key, out byte[]? data)
    {
        data = Get(key);
        return data != null;
    }

    public void Evict(string key)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentSizeBytes, -entry.SizeBytes);
        }
    }

    public void EvictByPrefix(string prefix)
    {
        var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keysToRemove)
        {
            Evict(key);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _currentSizeBytes = 0;
    }

    private void EnsureSpace(long requiredBytes)
    {
        if (_currentSizeBytes + requiredBytes <= _maxSizeBytes)
            return;

        lock (_evictionLock)
        {
            // Double-check após obter lock
            if (_currentSizeBytes + requiredBytes <= _maxSizeBytes)
                return;

            // Ordenar por último acesso (LRU)
            var entries = _cache
                .OrderBy(kv => kv.Value.LastAccess)
                .ToList();

            foreach (var entry in entries)
            {
                if (_currentSizeBytes + requiredBytes <= _maxSizeBytes)
                    break;

                Evict(entry.Key);
            }
        }
    }

    public long CurrentSizeBytes => _currentSizeBytes;
    public int Count => _cache.Count;

    private class CacheEntry
    {
        public required byte[] Data { get; set; }
        public DateTime LastAccess { get; set; }
        public int SizeBytes { get; set; }
    }
}
