using System.Collections.Frozen;

namespace Lavari.Domain.Security;

public enum AccessLevel
{
    None = 0,
    Read = 1,
    Alter = 2,
    Delete = 3
}

public static class ModuleKeys
{
    public const string Clients = "clients";
    public const string JobGroups = "job_groups";
    public const string Jobs = "jobs";
    public const string JobDates = "job_dates";
    public const string TimeTracking = "time_tracking";
    public const string ProductCatalog = "product_catalog";
    public const string JobLineItems = "job_line_items";
    public const string ImageLibrary = "image_library";
    public const string MoodBoards = "mood_boards";
    public const string Quotes = "quotes";
    public const string Invoices = "invoices";
    public const string Procurement = "procurement";
    public const string Receipts = "receipts";
    public const string SupplierCapture = "supplier_capture";
    public const string GmailConnections = "gmail_connections";
    public const string FinancialForecasting = "financial_forecasting";
    public const string LlmAdministration = "llm_administration";
    public const string Users = "users";
    public const string RolesAndPermissions = "roles_and_permissions";
    public const string ApplicationVariables = "application_variables";
    public const string OperationalErrors = "operational_errors";
    public const string AccountAppearance = "account_appearance";

    public static readonly FrozenSet<string> All = new[]
    {
        Clients,
        JobGroups,
        Jobs,
        JobDates,
        TimeTracking,
        ProductCatalog,
        JobLineItems,
        ImageLibrary,
        MoodBoards,
        Quotes,
        Invoices,
        Procurement,
        Receipts,
        SupplierCapture,
        GmailConnections,
        FinancialForecasting,
        LlmAdministration,
        Users,
        RolesAndPermissions,
        ApplicationVariables,
        OperationalErrors,
        AccountAppearance
    }.ToFrozenSet(StringComparer.Ordinal);
}

public sealed record ModuleDescriptor(string Key, string DisplayName, string Category);

public sealed record RoleGrantSnapshot(
    Guid RoleId,
    string RoleName,
    bool IsFullAccess,
    IReadOnlyDictionary<string, AccessLevel> ModuleGrants);

public static class PermissionRegistry
{
    public static readonly IReadOnlyList<ModuleDescriptor> Modules =
    [
        new(ModuleKeys.Clients, "Clients", "Core"),
        new(ModuleKeys.JobGroups, "Job Groups", "Core"),
        new(ModuleKeys.Jobs, "Jobs", "Core"),
        new(ModuleKeys.JobDates, "Job Dates & Milestones", "Jobs"),
        new(ModuleKeys.TimeTracking, "Time Tracking", "Jobs"),
        new(ModuleKeys.ProductCatalog, "Product Catalog", "Sourcing"),
        new(ModuleKeys.JobLineItems, "Job Line Items", "Sourcing"),
        new(ModuleKeys.ImageLibrary, "Image Library", "Media"),
        new(ModuleKeys.MoodBoards, "Mood Boards", "Media"),
        new(ModuleKeys.Quotes, "Quotes", "Finance"),
        new(ModuleKeys.Invoices, "Invoices", "Finance"),
        new(ModuleKeys.Procurement, "Procurement", "Finance"),
        new(ModuleKeys.Receipts, "Receipts", "Finance"),
        new(ModuleKeys.FinancialForecasting, "Financial Forecasting", "Finance"),
        new(ModuleKeys.SupplierCapture, "Supplier Capture", "Integrations"),
        new(ModuleKeys.GmailConnections, "Gmail Connections", "Integrations"),
        new(ModuleKeys.LlmAdministration, "LLM Administration & Use", "Integrations"),
        new(ModuleKeys.Users, "Users", "Administration"),
        new(ModuleKeys.RolesAndPermissions, "Roles & Permissions", "Administration"),
        new(ModuleKeys.ApplicationVariables, "Application Variables", "Administration"),
        new(ModuleKeys.OperationalErrors, "Operational Errors", "Administration"),
        new(ModuleKeys.AccountAppearance, "Account Appearance", "Account")
    ];

    public static bool IsRegistered(string? moduleKey)
        => moduleKey is not null && ModuleKeys.All.Contains(moduleKey);

    public static FrozenDictionary<string, AccessLevel> ComputeEffectiveGrants(
        bool isActiveUser,
        IEnumerable<RoleGrantSnapshot> roles)
    {
        var result = new Dictionary<string, AccessLevel>(ModuleKeys.All.Count, StringComparer.Ordinal);
        foreach (var module in ModuleKeys.All)
        {
            result[module] = AccessLevel.None;
        }

        if (!isActiveUser)
        {
            return result.ToFrozenDictionary(StringComparer.Ordinal);
        }

        foreach (var role in roles)
        {
            if (role.IsFullAccess)
            {
                foreach (var module in ModuleKeys.All)
                {
                    result[module] = AccessLevel.Delete;
                }
                return result.ToFrozenDictionary(StringComparer.Ordinal);
            }

            foreach (var (module, level) in role.ModuleGrants)
            {
                if (!ModuleKeys.All.Contains(module))
                {
                    continue;
                }

                if (level > result[module])
                {
                    result[module] = level;
                }
            }
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static bool Satisfies(
        IReadOnlyDictionary<string, AccessLevel> effectiveGrants,
        string moduleKey,
        AccessLevel requiredLevel)
    {
        if (requiredLevel == AccessLevel.None)
        {
            return true;
        }

        if (!IsRegistered(moduleKey))
        {
            return false;
        }

        return effectiveGrants.TryGetValue(moduleKey, out var granted) && granted >= requiredLevel;
    }
}
