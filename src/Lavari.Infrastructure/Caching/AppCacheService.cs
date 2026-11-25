using System.Collections.Concurrent;
using Lavari.Application.Abstractions;
using Lavari.Contracts.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Lavari.Infrastructure.Caching;

public sealed class AppCacheOptions
{
    public long SizeLimit { get; set; } = 10_000;
    public int DefaultAbsoluteExpirationMinutes { get; set; } = 15;
    public int DefaultSlidingExpirationMinutes { get; set; } = 5;
    public int MaxTrackedScopes { get; set; } = 4_096;
}

/// <summary>
/// Self-contained in-memory cache service owning a dedicated size-limited MemoryCache instance,
/// typed key management, scope-based invalidation, global clear, and stale-fill generation fencing.
/// </summary>
public sealed class AppCacheService : IAppCacheService, IDisposable
{
    private readonly AppCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, ScopeToken> _scopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _keyGenerations = new(StringComparer.Ordinal);
    private readonly Lock _clearLock = new();
    private CancellationTokenSource _globalResetSource = new();

    private long _globalGeneration;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _invalidations;

    public AppCacheService(IOptions<AppCacheOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;

        var memOptions = new MemoryCacheOptions
        {
            SizeLimit = Math.Max(100, _options.SizeLimit),
            Clock = new TimeProviderSystemClock(timeProvider),
            TrackStatistics = false
        };
        _cache = new MemoryCache(memOptions);
    }

    public async Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CacheEntryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (_cache.TryGetValue(key.Value, out T? existing) && existing is not null)
        {
            Interlocked.Increment(ref _hits);
            return existing;
        }

        Interlocked.Increment(ref _misses);

        // Capture generation tokens BEFORE calling the factory to fence stale in-flight fills.
        var startGlobalGen = Volatile.Read(ref _globalGeneration);
        var startKeyGen = _keyGenerations.GetValueOrDefault(key.Value);
        var startScopeGens = CaptureScopeGenerations(key.Scopes);

        var value = await factory(cancellationToken).ConfigureAwait(false);

        if (value is null)
        {
            return value;
        }

        // If any relevant scope, key, or global reset occurred while the factory was in flight,
        // return the freshly loaded value to this caller without repopulating the cache with stale data.
        if (IsFenced(key, startGlobalGen, startKeyGen, startScopeGens))
        {
            return value;
        }

        var effectivePolicy = policy ?? new CacheEntryPolicy
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(Math.Max(1, _options.DefaultAbsoluteExpirationMinutes)),
            SlidingExpiration = TimeSpan.FromMinutes(Math.Max(1, _options.DefaultSlidingExpirationMinutes)),
            SizeUnits = 1
        };

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, effectivePolicy.SizeUnits),
            AbsoluteExpirationRelativeToNow = effectivePolicy.AbsoluteExpiration,
            SlidingExpiration = effectivePolicy.SlidingExpiration is { } sliding && sliding < effectivePolicy.AbsoluteExpiration
                ? sliding
                : null
        };

        entryOptions.AddExpirationToken(new CancellationChangeToken(_globalResetSource.Token));

        foreach (var scope in key.Scopes)
        {
            var token = GetOrCreateScopeToken(scope.Value);
            entryOptions.AddExpirationToken(new CancellationChangeToken(token.Token));
        }

        entryOptions.RegisterPostEvictionCallback((_, _, reason, _) =>
        {
            if (reason is EvictionReason.Capacity or EvictionReason.Expired or EvictionReason.TokenExpired)
            {
                Interlocked.Increment(ref _evictions);
            }
        });

        // Final fence check right before insert.
        if (!IsFenced(key, startGlobalGen, startKeyGen, startScopeGens))
        {
            _cache.Set(key.Value, value, entryOptions);
        }

        return value;
    }

    public void Remove(CacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _keyGenerations.AddOrUpdate(key.Value, 1, static (_, current) => current + 1);
        _cache.Remove(key.Value);
        Interlocked.Increment(ref _invalidations);
    }

    public void InvalidateScope(CacheScope scope)
    {
        if (_scopes.TryRemove(scope.Value, out var existing))
        {
            existing.CancelAndIncrement();
            existing.Dispose();
        }
        else
        {
            // Bump global generation slightly or track tombstone so in-flight fills for uncached scope are fenced.
            var created = _scopes.GetOrAdd(scope.Value, static _ => new ScopeToken());
            created.CancelAndIncrement();
            _scopes.TryRemove(scope.Value, out _);
            created.Dispose();
        }

        Interlocked.Increment(ref _invalidations);
    }

    public void InvalidateScopes(IEnumerable<CacheScope> scopes)
    {
        foreach (var scope in scopes)
        {
            InvalidateScope(scope);
        }
    }

    public void ClearAll()
    {
        CancellationTokenSource oldGlobal;
        lock (_clearLock)
        {
            Interlocked.Increment(ref _globalGeneration);
            oldGlobal = _globalResetSource;
            _globalResetSource = new CancellationTokenSource();
        }

        try
        {
            oldGlobal.Cancel();
        }
        finally
        {
            oldGlobal.Dispose();
        }

        foreach (var key in _scopes.Keys.ToArray())
        {
            if (_scopes.TryRemove(key, out var token))
            {
                token.CancelAndIncrement();
                token.Dispose();
            }
        }

        _keyGenerations.Clear();
        _cache.Compact(1.0);
        Interlocked.Increment(ref _invalidations);
    }

    public CacheStatisticsSnapshot GetStatistics() => new()
    {
        Hits = Volatile.Read(ref _hits),
        Misses = Volatile.Read(ref _misses),
        Evictions = Volatile.Read(ref _evictions),
        Invalidations = Volatile.Read(ref _invalidations),
        CurrentEntryCount = _cache.Count
    };

    private ScopeToken GetOrCreateScopeToken(string scopeValue)
    {
        if (_scopes.Count > _options.MaxTrackedScopes)
        {
            // Bound scope bookkeeping so memory cannot grow unboundedly.
            ClearAll();
        }

        return _scopes.GetOrAdd(scopeValue, static _ => new ScopeToken());
    }

    private (string Scope, ScopeToken Token, long Gen)[] CaptureScopeGenerations(IReadOnlyList<CacheScope> scopes)
    {
        var result = new (string, ScopeToken, long)[scopes.Count];
        for (var i = 0; i < scopes.Count; i++)
        {
            var token = GetOrCreateScopeToken(scopes[i].Value);
            result[i] = (scopes[i].Value, token, token.Generation);
        }
        return result;
    }

    private bool IsFenced(
        CacheKey key,
        long startGlobalGen,
        long startKeyGen,
        (string Scope, ScopeToken Token, long Gen)[] startScopeGens)
    {
        if (Volatile.Read(ref _globalGeneration) != startGlobalGen)
        {
            return true;
        }

        if (_keyGenerations.GetValueOrDefault(key.Value) != startKeyGen)
        {
            return true;
        }

        foreach (var (_, token, gen) in startScopeGens)
        {
            if (token.IsCancelled || token.Generation != gen)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _cache.Dispose();
        _globalResetSource.Dispose();
        foreach (var token in _scopes.Values)
        {
            token.Dispose();
        }
        _scopes.Clear();
    }

    private sealed class ScopeToken : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private long _generation;

        public CancellationToken Token => _cts.Token;
        public bool IsCancelled => _cts.IsCancellationRequested;
        public long Generation => Volatile.Read(ref _generation);

        public void CancelAndIncrement()
        {
            Interlocked.Increment(ref _generation);
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    private sealed class TimeProviderSystemClock(TimeProvider timeProvider) : ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
}
