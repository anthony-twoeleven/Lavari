using System.Collections.Frozen;
using Lavari.Application.Abstractions;
using Lavari.Domain.Security;

namespace Lavari.Api.Security;

public sealed class HttpCurrentActorContext : ICurrentActorContext, ICurrentActorContextScope
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

    public IDisposable Use(ICurrentActorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsAuthenticated || !context.ActorId.HasValue ||
            (context.IsSystemPrincipal && context.IsFullAccess))
        {
            throw new InvalidOperationException("Only a validated, least-privilege actor context can be applied.");
        }

        var previous = new ActorSnapshot(
            IsAuthenticated, IsSystemPrincipal, ActorId, ActorDisplayName, CorrelationId,
            EditingSessionId, IsFullAccess, EffectivePermissions);
        Apply(context);
        return new RestoreScope(this, previous);
    }

    private void Apply(ICurrentActorContext context)
    {
        IsAuthenticated = context.IsAuthenticated;
        IsSystemPrincipal = context.IsSystemPrincipal;
        ActorId = context.ActorId;
        ActorDisplayName = context.ActorDisplayName;
        CorrelationId = context.CorrelationId;
        EditingSessionId = context.EditingSessionId;
        IsFullAccess = context.IsFullAccess;
        EffectivePermissions = context.EffectivePermissions;
    }

    private sealed record ActorSnapshot(
        bool IsAuthenticated,
        bool IsSystemPrincipal,
        Guid? ActorId,
        string ActorDisplayName,
        string CorrelationId,
        string EditingSessionId,
        bool IsFullAccess,
        IReadOnlyDictionary<string, AccessLevel> EffectivePermissions);

    private sealed class RestoreScope(HttpCurrentActorContext owner, ActorSnapshot previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            owner.IsAuthenticated = previous.IsAuthenticated;
            owner.IsSystemPrincipal = previous.IsSystemPrincipal;
            owner.ActorId = previous.ActorId;
            owner.ActorDisplayName = previous.ActorDisplayName;
            owner.CorrelationId = previous.CorrelationId;
            owner.EditingSessionId = previous.EditingSessionId;
            owner.IsFullAccess = previous.IsFullAccess;
            owner.EffectivePermissions = previous.EffectivePermissions;
            _disposed = true;
        }
    }
}
