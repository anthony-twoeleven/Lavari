using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Integrations;

// Rechecks the initiating user and least-privilege service principal at provider and save boundaries.
public sealed class SupplierAiAuthorization(LavariDbContext db, IAuthService auth)
{
    public async Task<ICurrentActorContext> AuthorizeAsync(
        Guid initiatingUserId, Guid? clientId, Guid? jobId, string correlationId, CancellationToken ct = default)
    {
        if (clientId.HasValue && !jobId.HasValue)
            throw new ValidationServiceException("Select a job for the selected client.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
            throw new ValidationServiceException("A valid operation reference is required.");

        var userActive = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == initiatingUserId && u.IsActive && !u.IsSystemPrincipal, ct);
        if (!userActive) throw Denied();
        var userGrants = await auth.LoadEffectivePermissionsAsync(initiatingUserId, ct);
        Require(userGrants, ModuleKeys.SupplierCapture, AccessLevel.Alter);
        Require(userGrants, ModuleKeys.LlmAdministration, AccessLevel.Read);
        if (jobId.HasValue)
        {
            Require(userGrants, ModuleKeys.Jobs, AccessLevel.Read);
            Require(userGrants, ModuleKeys.Clients, AccessLevel.Read);
            var job = await db.Jobs.AsNoTracking()
                .Where(j => j.Id == jobId.Value && db.Clients.Any(c => c.Id == j.ClientId) &&
                    db.JobGroups.Any(g => g.Id == j.JobGroupId && g.ClientId == j.ClientId))
                .Select(j => new { j.ClientId })
                .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundServiceException("Job not found.");
            if (clientId.HasValue && clientId.Value != job.ClientId)
                throw new ValidationServiceException("The selected job does not belong to the selected client.");
        }

        return await AuthorizeServicePrincipalAsync(correlationId, ct);
    }

    public async Task<ICurrentActorContext> AuthorizeInitiatingUserAsync(
        Guid initiatingUserId, Guid? clientId, Guid? jobId, string correlationId, CancellationToken ct = default)
    {
        await AuthorizeAsync(initiatingUserId, clientId, jobId, correlationId, ct);
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == initiatingUserId && u.IsActive && !u.IsSystemPrincipal)
            .Select(u => new { u.DisplayName })
            .SingleOrDefaultAsync(ct)
            ?? throw Denied();
        var grants = await auth.LoadEffectivePermissionsAsync(initiatingUserId, ct);
        return new InitiatingUserActorContext(initiatingUserId, user.DisplayName, correlationId, grants);
    }

    public async Task<ICurrentActorContext> AuthorizeServicePrincipalAsync(string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
            throw new ValidationServiceException("A valid operation reference is required.");
        var normalized = DatabaseSeeder.LlmPrincipalEmail.ToUpperInvariant();
        var principal = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized && u.IsActive && u.IsSystemPrincipal)
            .Select(u => new { u.Id, u.DisplayName })
            .SingleOrDefaultAsync(ct);
        if (principal is null) throw Denied();
        var serviceGrants = await auth.LoadEffectivePermissionsAsync(principal.Id, ct);
        Require(serviceGrants, ModuleKeys.SupplierCapture, AccessLevel.Alter);
        Require(serviceGrants, ModuleKeys.LlmAdministration, AccessLevel.Read);
        if (serviceGrants.Any(grant =>
            grant.Value > (grant.Key == ModuleKeys.SupplierCapture ? AccessLevel.Alter :
                grant.Key == ModuleKeys.LlmAdministration ? AccessLevel.Read : AccessLevel.None)))
            throw Denied();

        return new LlmActorContext(principal.Id, principal.DisplayName, correlationId, serviceGrants);
    }

    private static void Require(IReadOnlyDictionary<string, AccessLevel> grants, string module, AccessLevel level)
    {
        if (!PermissionRegistry.Satisfies(grants, module, level)) throw Denied();
    }

    private static ForbiddenServiceException Denied() => new(
        "AI extraction is not authorized.",
        possibleCause: "Check the initiating user's and extraction service's permissions.");

    private sealed class LlmActorContext(
        Guid id, string name, string correlationId, IReadOnlyDictionary<string, AccessLevel> grants) : ICurrentActorContext
    {
        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => true;
        public Guid? ActorId => id;
        public string ActorDisplayName => name;
        public string CorrelationId => correlationId;
        public string EditingSessionId => "llm-extraction";
        public bool IsFullAccess => false;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions => grants;
    }

    private sealed class InitiatingUserActorContext(
        Guid id, string name, string correlationId, IReadOnlyDictionary<string, AccessLevel> grants) : ICurrentActorContext
    {
        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => false;
        public Guid? ActorId => id;
        public string ActorDisplayName => name;
        public string CorrelationId => correlationId;
        public string EditingSessionId => "supplier-extraction";
        public bool IsFullAccess => false;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions => grants;
    }
}
