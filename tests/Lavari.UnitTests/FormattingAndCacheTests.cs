using Lavari.Application.Formatting;
using Lavari.Contracts.Caching;
using Lavari.Domain.Money;
using Lavari.Infrastructure.Caching;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lavari.UnitTests;

public sealed class FormattingAndCacheTests
{
    [Fact]
    public void FormattingService_FormatsMoneyAndDualTimeZones()
    {
        var svc = new FormattingService();
        Assert.Equal("$1,250.50", svc.FormatMoney(new MoneyCents(125_050, "AUD"), "en-AU"));
        Assert.Equal("-$42.00", svc.FormatMoney(new MoneyCents(-4_200, "AUD"), "en-AU"));
        Assert.Equal(125_050L, svc.ParseMoneyInput("$1,250.50", "AUD", "en-AU").Cents);

        var utc = new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);

        // Different zones -> ZonesDiffer = true
        var diff = svc.FormatDateTime(utc, "Australia/Sydney", "America/New_York", "en-AU");
        Assert.True(diff.ZonesDiffer);
        Assert.True(diff.BrowserZoneAvailable);
        Assert.Contains("Australia/Sydney", diff.JobLocalFormatted);
        Assert.Contains("America/New_York", diff.BrowserLocalFormatted);

        // Distinct zones with same current offset (Sydney vs Melbourne in Nov, both UTC+11) remain distinct
        var sameOffsetDiffZone = svc.FormatDateTime(utc, "Australia/Sydney", "Australia/Melbourne", "en-AU");
        Assert.True(sameOffsetDiffZone.ZonesDiffer);

        // Unavailable browser zone
        var unavailable = svc.FormatDateTime(utc, "Australia/Sydney", null, "en-AU");
        Assert.False(unavailable.BrowserZoneAvailable);
        Assert.False(unavailable.ZonesDiffer);

        // Date-only remains a calendar date without UTC shift
        Assert.Equal("25 Nov 2026", svc.FormatCalendarDate(new DateOnly(2026, 11, 25), "en-AU"));
    }

    [Fact]
    public async Task AppCacheService_TypedKeysScopeInvalidationExpirationAndStaleFillFencing()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        using var cache = new AppCacheService(
            Options.Create(new AppCacheOptions { SizeLimit = 500, DefaultAbsoluteExpirationMinutes = 10 }),
            fakeTime);

        // Key collision safety: ("a:b", "c") != ("a", "b:c")
        var k1 = CacheKey.Create("clients", "get", 1, null, "a:b", "c");
        var k2 = CacheKey.Create("clients", "get", 1, null, "a", "b:c");
        Assert.NotEqual(k1.Value, k2.Value);

        // Scope invalidation
        var clientId = Guid.NewGuid();
        var recordScope = CacheScope.ForRecord("clients", clientId);
        var key = CacheKey.Create("clients", "by_id", 1, [recordScope], clientId.ToString("N"));

        int loads = 0;
        var v1 = await cache.GetOrCreateAsync(key, _ => Task.FromResult($"val-{++loads}"));
        var v2 = await cache.GetOrCreateAsync(key, _ => Task.FromResult($"val-{++loads}"));
        Assert.Equal("val-1", v1);
        Assert.Equal("val-1", v2);
        Assert.Equal(1, loads);

        cache.InvalidateScope(recordScope);
        var v3 = await cache.GetOrCreateAsync(key, _ => Task.FromResult($"val-{++loads}"));
        Assert.Equal("val-2", v3);
        Assert.Equal(2, loads);

        // Expiration with fake TimeProvider
        fakeTime.Advance(TimeSpan.FromMinutes(11));
        var v4 = await cache.GetOrCreateAsync(key, _ => Task.FromResult($"val-{++loads}"));
        Assert.Equal("val-3", v4);

        // Stale in-flight fill fencing: invalidation happens while factory is running
        cache.ClearAll();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<string>();

        var staleTask = cache.GetOrCreateAsync(key, async _ =>
        {
            started.SetResult();
            return await release.Task;
        });

        await started.Task;
        cache.InvalidateScope(recordScope);
        release.SetResult("stale-value");

        var returned = await staleTask;
        Assert.Equal("stale-value", returned);

        // Cache must NOT have been repopulated with "stale-value"
        var fresh = await cache.GetOrCreateAsync(key, _ => Task.FromResult("fresh-after-commit"));
        Assert.Equal("fresh-after-commit", fresh);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
