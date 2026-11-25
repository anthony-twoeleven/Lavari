using System.Security.Cryptography;
using System.Text;

namespace Lavari.Contracts.Caching;

/// <summary>
/// Strongly-typed, bounded, collision-safe cache key with explicit namespace, schema version, module, and dependency scopes.
/// </summary>
public sealed record CacheKey
{
    private const string AppNamespace = "lavari";
    private const int MaxKeyLength = 256;

    public string Value { get; }
    public string Module { get; }
    public IReadOnlyList<CacheScope> Scopes { get; }

    private CacheKey(string value, string module, IReadOnlyList<CacheScope> scopes)
    {
        Value = value;
        Module = module;
        Scopes = scopes;
    }

    public static CacheKey Create(
        string module,
        string operation,
        int schemaVersion,
        IEnumerable<CacheScope>? scopes = null,
        params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Schema version must be positive.");
        }

        var normalizedModule = NormalizeToken(module);
        var normalizedOp = NormalizeToken(operation);

        var sb = new StringBuilder(128);
        sb.Append(AppNamespace)
          .Append(":v").Append(schemaVersion)
          .Append(':').Append(normalizedModule)
          .Append(':').Append(normalizedOp);

        foreach (var segment in segments)
        {
            var encoded = EncodeSegment(segment);
            sb.Append(':').Append(encoded);
        }

        var rawKey = sb.ToString();
        if (rawKey.Length > MaxKeyLength)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
            rawKey = $"{AppNamespace}:v{schemaVersion}:{normalizedModule}:{normalizedOp}:h:{hash}";
        }

        var scopeList = new List<CacheScope> { CacheScope.ForModule(normalizedModule) };
        if (scopes is not null)
        {
            foreach (var s in scopes)
            {
                if (!scopeList.Contains(s))
                {
                    scopeList.Add(s);
                }
            }
        }

        return new CacheKey(rawKey, normalizedModule, scopeList.AsReadOnly());
    }

    private static string NormalizeToken(string token)
    {
        var cleaned = token.Trim().ToLowerInvariant();
        foreach (var ch in cleaned)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '_' and not '-' and not '.')
            {
                throw new ArgumentException($"Cache token '{token}' contains invalid characters.", nameof(token));
            }
        }
        return cleaned;
    }

    private static string EncodeSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return "_";
        }

        // Unambiguous percent-encoding of ':' and '%' prevents boundary collisions like ("a:b", "c") vs ("a", "b:c").
        var trimmed = segment.Trim();
        var encoded = trimmed.Replace("%", "%25", StringComparison.Ordinal)
                             .Replace(":", "%3A", StringComparison.Ordinal);
        return encoded.Length <= 64
            ? encoded
            : "sha256_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)))[..32];
    }
}

public readonly record struct CacheScope
{
    public string Value { get; }

    private CacheScope(string value) => Value = value;

    public static CacheScope ForModule(string module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        return new CacheScope($"mod:{module.Trim().ToLowerInvariant()}");
    }

    public static CacheScope ForRecord(string module, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        return new CacheScope($"rec:{module.Trim().ToLowerInvariant()}:{id:N}");
    }

    public static CacheScope ForRecord(string module, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new CacheScope($"rec:{module.Trim().ToLowerInvariant()}:{key.Trim().ToLowerInvariant()}");
    }

    public static CacheScope ForCollection(string module, string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        return new CacheScope($"col:{module.Trim().ToLowerInvariant()}:{collectionName.Trim().ToLowerInvariant()}");
    }
}

public sealed record CacheEntryPolicy
{
    public TimeSpan AbsoluteExpiration { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan? SlidingExpiration { get; init; } = TimeSpan.FromMinutes(5);
    public long SizeUnits { get; init; } = 1;
}

public sealed record CacheStatisticsSnapshot
{
    public required long Hits { get; init; }
    public required long Misses { get; init; }
    public required long Evictions { get; init; }
    public required long Invalidations { get; init; }
    public required long CurrentEntryCount { get; init; }
}
