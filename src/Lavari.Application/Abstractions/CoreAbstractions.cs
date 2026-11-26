using Lavari.Contracts.Caching;
using Lavari.Contracts.Common;
using Lavari.Domain.Money;
using Lavari.Domain.Security;

namespace Lavari.Application.Abstractions;

public interface IIdGenerator
{
    Guid NewId();
}

public interface ICurrentActorContext
{
    bool IsAuthenticated { get; }
    bool IsSystemPrincipal { get; }
    Guid? ActorId { get; }
    string ActorDisplayName { get; }
    string CorrelationId { get; }
    string EditingSessionId { get; }
    bool IsFullAccess { get; }
    IReadOnlyDictionary<string, AccessLevel> EffectivePermissions { get; }
}

public interface ICurrentActorContextScope
{
    IDisposable Use(ICurrentActorContext context);
}

public interface IPermissionEvaluator
{
    void RequireAuthenticated();
    void Require(string moduleKey, AccessLevel requiredLevel);
    bool Has(string moduleKey, AccessLevel requiredLevel);
}

public interface IFormattingService
{
    string FormatMoney(MoneyCents money, string? locale = null);
    MoneyCents ParseMoneyInput(string input, string currency, string? locale = null);
    FormattedDateTimeDto FormatDateTime(
        DateTimeOffset utcInstant,
        string jobIanaTimeZone,
        string? browserIanaTimeZone = null,
        string? locale = null);
    string FormatCalendarDate(DateOnly date, string? locale = null);
    string FormatDuration(long totalSeconds);
}

public interface IAppCacheService
{
    Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CacheEntryPolicy? policy = null,
        CancellationToken cancellationToken = default);

    void Remove(CacheKey key);
    void InvalidateScope(CacheScope scope);
    void InvalidateScopes(IEnumerable<CacheScope> scopes);
    void ClearAll();
    CacheStatisticsSnapshot GetStatistics();
}
