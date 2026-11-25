using Lavari.Contracts.Caching;
using Lavari.Domain.Security;

namespace Lavari.Application.Caching;

/// <summary>
/// Centrally registered typed cache key builders and scope definitions.
/// </summary>
public static class CacheKeys
{
    public static CacheKey AppVariableByKey(string variableKey) =>
        CacheKey.Create(
            ModuleKeys.ApplicationVariables,
            "by_key",
            1,
            [CacheScope.ForRecord(ModuleKeys.ApplicationVariables, variableKey), CacheScope.ForCollection(ModuleKeys.ApplicationVariables, "all")],
            variableKey);

    public static CacheKey AllAppVariables() =>
        CacheKey.Create(
            ModuleKeys.ApplicationVariables,
            "all",
            1,
            [CacheScope.ForCollection(ModuleKeys.ApplicationVariables, "all")]);

    public static CacheKey ErrorCodeDefinition(string errorCode) =>
        CacheKey.Create(
            ModuleKeys.OperationalErrors,
            "code_def",
            1,
            [CacheScope.ForRecord(ModuleKeys.OperationalErrors, errorCode)],
            errorCode);

    public static CacheKey ProductById(Guid productId) =>
        CacheKey.Create(
            ModuleKeys.ProductCatalog,
            "by_id",
            1,
            [CacheScope.ForRecord(ModuleKeys.ProductCatalog, productId)],
            productId.ToString("N"));

    public static CacheKey ClientById(Guid clientId) =>
        CacheKey.Create(
            ModuleKeys.Clients,
            "by_id",
            1,
            [CacheScope.ForRecord(ModuleKeys.Clients, clientId)],
            clientId.ToString("N"));

    public static CacheKey JobById(Guid jobId) =>
        CacheKey.Create(
            ModuleKeys.Jobs,
            "by_id",
            1,
            [CacheScope.ForRecord(ModuleKeys.Jobs, jobId)],
            jobId.ToString("N"));
}
