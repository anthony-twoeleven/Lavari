using BenchmarkDotNet.Attributes;
using Lavari.Application.Formatting;
using Lavari.Contracts.Caching;
using Lavari.Domain.Images;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Caching;
using Microsoft.Extensions.Options;

namespace Lavari.Benchmarks;

[MemoryDiagnoser]
public class CoreBenchmarks
{
    private readonly FormattingService _formatting = new();
    private readonly IReadOnlyList<RoleGrantSnapshot> _roles;
    private readonly IReadOnlyDictionary<string, AccessLevel> _effectiveGrants;
    private readonly AppCacheService _cache;
    private readonly CacheKey _hitKey;

    public CoreBenchmarks()
    {
        _roles =
        [
            new RoleGrantSnapshot(Guid.NewGuid(), "Viewer", false, new Dictionary<string, AccessLevel> { [ModuleKeys.Jobs] = AccessLevel.Read }),
            new RoleGrantSnapshot(Guid.NewGuid(), "Designer", false, new Dictionary<string, AccessLevel> { [ModuleKeys.Jobs] = AccessLevel.Alter, [ModuleKeys.Quotes] = AccessLevel.Alter })
        ];
        _effectiveGrants = PermissionRegistry.ComputeEffectiveGrants(true, _roles);
        _cache = new AppCacheService(Options.Create(new AppCacheOptions()), TimeProvider.System);
        _hitKey = CacheKey.Create("clients", "by_id", 1, null, "01950000000070008000000000000001");
        _cache.GetOrCreateAsync(_hitKey, static _ => Task.FromResult("cached-client")).GetAwaiter().GetResult();
    }

    [Benchmark]
    public MoneyCents MoneyCalculationAndGst()
    {
        var subtotal = new MoneyCents(8_092_778L, "AUD");
        var gst = subtotal.ApplyBasisPoints(1000);
        return subtotal.Add(gst);
    }

    [Benchmark]
    public string FormatMoneyAud()
        => _formatting.FormatMoney(new MoneyCents(8_902_056L, "AUD"), "en-AU");

    [Benchmark]
    public bool EvaluatePermission()
        => PermissionRegistry.Satisfies(_effectiveGrants, ModuleKeys.Jobs, AccessLevel.Alter);

    [Benchmark]
    public ImageDimensions Calculate1080pResize()
        => ImageResizePolicy.CalculateMax1080pSize(4000, 3000);

    [Benchmark]
    public Task<string> CacheHitLookup()
        => _cache.GetOrCreateAsync(_hitKey, static _ => Task.FromResult("miss"));
}
