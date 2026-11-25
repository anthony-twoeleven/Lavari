using Lavari.Application.Abstractions;
using Lavari.Application.Security;
using Lavari.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lavari.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LavariDbContext>
{
    public LavariDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LavariDbContext>();
        optionsBuilder.UseSqlite("Data Source=lavari-design.db");
        return new LavariDbContext(
            optionsBuilder.Options,
            TimeProvider.System,
            SystemActorContext.DesignTime,
            new TimeOrderedIdGenerator(TimeProvider.System));
    }
}

public sealed class SystemActorContext : ICurrentActorContext
{
    public static readonly SystemActorContext DesignTime = new("design-time", "EF Design Time");
    public static readonly SystemActorContext Seeder = new("system-seeder", "System Seeder");
    public static readonly SystemActorContext Scheduler = new("system-scheduler", "Background Scheduler");
    public static readonly SystemActorContext LlmService = new("system-llm-extractor", "LLM Extraction Service");

    private static readonly IReadOnlyDictionary<string, AccessLevel> FullSystemGrants =
        PermissionRegistry.ComputeEffectiveGrants(
            true,
            [new RoleGrantSnapshot(Guid.Empty, "System", true, new Dictionary<string, AccessLevel>())]);

    public SystemActorContext(string actorName, string displayName, IReadOnlyDictionary<string, AccessLevel>? grants = null)
    {
        ActorDisplayName = displayName;
        CorrelationId = $"{actorName}-{Guid.NewGuid():N}";
        EditingSessionId = actorName;
        EffectivePermissions = grants ?? FullSystemGrants;
        IsFullAccess = grants is null;
    }

    public bool IsAuthenticated => true;
    public bool IsSystemPrincipal => true;
    public Guid? ActorId => Guid.Empty;
    public string ActorDisplayName { get; }
    public string CorrelationId { get; }
    public string EditingSessionId { get; }
    public bool IsFullAccess { get; }
    public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions { get; }
}
