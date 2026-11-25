using System.Collections.Frozen;
using Lavari.Application.Abstractions;
using Lavari.Domain.Security;

namespace Lavari.Api.Security;

public sealed class HttpCurrentActorContext : ICurrentActorContext
{
    private static readonly FrozenDictionary<string, AccessLevel> EmptyGrants =
        PermissionRegistry.ComputeEffectiveGrants(false, []);

    public bool IsAuthenticated { get; private set; }
    public bool IsSystemPrincipal { get; private set; }
    public Guid? ActorId { get; private set; }
    public string ActorDisplayName { get; private set; } = "Anonymous";
    public string CorrelationId { get; private set; } = Guid.NewGuid().ToString("N");
    public string EditingSessionId { get; private set; } = string.Empty;
    public bool IsFullAccess { get; private set; }
    public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions { get; private set; } = EmptyGrants;

    public void Initialize(
        string correlationId,
        bool isAuthenticated,
        Guid? userId,
        string displayName,
        string editingSessionId,
        bool isFullAccess,
        IReadOnlyDictionary<string, AccessLevel> effectivePermissions)
    {
        CorrelationId = correlationId;
        IsAuthenticated = isAuthenticated;
        IsSystemPrincipal = false;
        ActorId = userId;
        ActorDisplayName = displayName;
        EditingSessionId = editingSessionId;
        IsFullAccess = isFullAccess;
        EffectivePermissions = effectivePermissions;
    }

    public void SetSystemPrincipal(string displayName)
    {
        IsAuthenticated = true;
        IsSystemPrincipal = true;
        ActorId = Guid.Empty;
        ActorDisplayName = displayName;
        EditingSessionId = "system";
        IsFullAccess = true;
        EffectivePermissions = PermissionRegistry.ComputeEffectiveGrants(
            true,
            [new RoleGrantSnapshot(Guid.Empty, "System", true, new Dictionary<string, AccessLevel>())]);
    }
}
