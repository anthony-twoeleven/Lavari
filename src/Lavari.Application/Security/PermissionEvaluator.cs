using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;

namespace Lavari.Application.Security;

public sealed class PermissionEvaluator : IPermissionEvaluator
{
    private readonly ICurrentActorContext _actor;

    public PermissionEvaluator(ICurrentActorContext actor)
    {
        _actor = actor;
    }

    public void RequireAuthenticated()
    {
        if (!_actor.IsAuthenticated)
        {
            throw new UnauthorizedServiceException();
        }
    }

    public void Require(string moduleKey, AccessLevel requiredLevel)
    {
        RequireAuthenticated();
        if (!PermissionRegistry.IsRegistered(moduleKey))
        {
            throw new ForbiddenServiceException(
                "Access to an unregistered module was denied.",
                ErrorCodes.Forbidden,
                "Contact an administrator to verify module registration.");
        }

        if (!PermissionRegistry.Satisfies(_actor.EffectivePermissions, moduleKey, requiredLevel))
        {
            throw new ForbiddenServiceException(
                $"You do not have {requiredLevel} permission for '{moduleKey}'.",
                ErrorCodes.Forbidden,
                "Ask an administrator to grant the required role permission.");
        }
    }

    public bool Has(string moduleKey, AccessLevel requiredLevel)
    {
        if (!_actor.IsAuthenticated)
        {
            return false;
        }

        return PermissionRegistry.Satisfies(_actor.EffectivePermissions, moduleKey, requiredLevel);
    }
}

public sealed class TimeOrderedIdGenerator : IIdGenerator
{
    private readonly TimeProvider _timeProvider;

    public TimeOrderedIdGenerator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Guid NewId() => Guid.CreateVersion7(_timeProvider.GetUtcNow());
}
