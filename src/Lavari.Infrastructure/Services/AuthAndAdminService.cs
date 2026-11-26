using Lavari.Application.Abstractions;
using Lavari.Application.Caching;
using Lavari.Application.Services;
using Lavari.Contracts.Admin;
using Lavari.Contracts.Caching;
using Lavari.Contracts.Common;
using Lavari.Contracts.Security;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private readonly LavariDbContext _db;
    private readonly IPasswordHasher _hasher;

    public AuthService(LavariDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<UserSessionDto> AuthenticateAsync(string email, string password, string editingSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            throw new ValidationServiceException("Email and password are required.");
        }

        var normalized = email.Trim().ToUpperInvariant();
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null || !user.IsActive || user.IsSystemPrincipal || !_hasher.VerifyPassword(password, user.PasswordHash))
        {
            throw new UnauthorizedServiceException(
                "Invalid email or password.",
                "Check your credentials or contact an administrator if your account is disabled.");
        }

        return BuildSession(user, editingSessionId);
    }

    public async Task<UserSessionDto?> LoadCurrentSessionAsync(Guid userId, string editingSessionId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
        {
            return null;
        }

        return BuildSession(user, editingSessionId);
    }

    public async Task<IReadOnlyDictionary<string, AccessLevel>> LoadEffectivePermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
        {
            return PermissionRegistry.ComputeEffectiveGrants(false, []);
        }

        return ComputeGrants(user);
    }

    private static UserSessionDto BuildSession(UserEntity user, string editingSessionId)
    {
        var grants = ComputeGrants(user);
        var activeRoles = user.UserRoles
            .Where(ur => !ur.IsDeleted && !ur.Role.IsDeleted)
            .Select(ur => ur.Role)
            .ToList();

        return new UserSessionDto
        {
            UserId = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            IsFullAccess = activeRoles.Any(r => r.IsFullAccess),
            EditingSessionId = editingSessionId,
            Roles = activeRoles.Select(r => r.Name).OrderBy(x => x).ToArray(),
            EffectivePermissions = grants.ToDictionary(kv => kv.Key, kv => (int)kv.Value, StringComparer.Ordinal)
        };
    }

    private static IReadOnlyDictionary<string, AccessLevel> ComputeGrants(UserEntity user)
    {
        var snapshots = user.UserRoles
            .Where(ur => !ur.IsDeleted && !ur.Role.IsDeleted)
            .Select(ur => new RoleGrantSnapshot(
                ur.Role.Id,
                ur.Role.Name,
                ur.Role.IsFullAccess,
                ur.Role.Permissions
                    .Where(p => !p.IsDeleted)
                    .ToDictionary(p => p.ModuleKey, p => p.AccessLevel, StringComparer.Ordinal)));

        return PermissionRegistry.ComputeEffectiveGrants(user.IsActive, snapshots);
    }
}

public sealed class AccountAppearanceService(
    LavariDbContext db,
    ICurrentActorContext actor,
    IPermissionEvaluator permissions) : IAccountAppearanceService
{
    public async Task<AppearanceDto> GetAsync(CancellationToken ct = default)
    {
        permissions.Require(ModuleKeys.AccountAppearance, AccessLevel.Read);
        var user = await CurrentUserAsync(ct);
        return Map(user);
    }

    public async Task<AppearanceDto> UpdateAsync(UpdateAppearanceRequest request, CancellationToken ct = default)
    {
        permissions.Require(ModuleKeys.AccountAppearance, AccessLevel.Alter);
        if (request.Mode is not ("system" or "light" or "dark") ||
            request.Color is not ("purple" or "blue" or "green" or "coral" or "neutral" or "lavender" or "rose"))
        {
            throw new ValidationServiceException("Choose a supported theme and color palette.");
        }

        var user = await CurrentUserAsync(ct);
        if (!string.Equals(user.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Your account changed elsewhere. Reload and try again.", ErrorCodes.ConcurrencyConflict);
        }

        user.AppearanceMode = request.Mode;
        user.AppearanceColor = request.Color;
        await db.SaveChangesAsync(ct);
        return Map(user);
    }

    private async Task<UserEntity> CurrentUserAsync(CancellationToken ct)
    {
        var id = actor.ActorId ?? throw new UnauthorizedServiceException();
        return await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive && !u.IsSystemPrincipal, ct)
            ?? throw new UnauthorizedServiceException();
    }

    private static AppearanceDto Map(UserEntity user) => new()
    {
        Mode = user.AppearanceMode,
        Color = user.AppearanceColor switch
        {
            "lavender" => "purple",
            "rose" => "coral",
            _ => user.AppearanceColor
        },
        ConcurrencyToken = user.ConcurrencyToken
    };
}

public sealed class AdminService : IAdminService
{
    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IPasswordHasher _hasher;
    private readonly IAppCacheService _cache;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public AdminService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IPasswordHasher hasher,
        IAppCacheService cache,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _hasher = hasher;
        _cache = cache;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<PagedResult<UserAdminDto>> ListUsersAsync(PagedRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Users, request.IncludeDeleted ? AccessLevel.Delete : AccessLevel.Read);

        IQueryable<UserEntity> query = request.IncludeDeleted ? _db.Users.IgnoreQueryFilters() : _db.Users;
        query = query.AsNoTracking().Include(u => u.UserRoles).ThenInclude(ur => ur.Role);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(u => u.Email.ToLower().Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = request.NormalizedPage;
        var pageSize = request.NormalizedPageSize;
        var rows = await query.OrderBy(u => u.DisplayName).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return new PagedResult<UserAdminDto>
        {
            Items = rows.Select(MapUser).ToArray(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<UserAdminDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Users, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            throw new ValidationServiceException("Valid email, display name and a password of at least 8 characters are required.");
        }

        var normalized = request.Email.Trim().ToUpperInvariant();
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            throw new ConflictServiceException("A user with this email already exists.");
        }

        var roles = await LoadAndValidateAssignedRolesAsync(request.RoleIds, ct);

        var user = new UserEntity
        {
            Id = _ids.NewId(),
            Email = request.Email.Trim(),
            NormalizedEmail = normalized,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = _hasher.HashPassword(request.Password),
            IsActive = request.IsActive
        };
        _db.Users.Add(user);
        foreach (var role in roles)
        {
            _db.UserRoles.Add(new UserRoleEntity { Id = _ids.NewId(), UserId = user.Id, RoleId = role.Id });
        }

        await _db.SaveChangesAsync(ct);
        var loaded = await _db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(ur => ur.Role).FirstAsync(u => u.Id == user.Id, ct);
        return MapUser(loaded);
    }

    public async Task<UserAdminDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Users, AccessLevel.Alter);
        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundServiceException("User not found.");

        if (!string.Equals(user.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("The user record was modified by another administrator.");
        }

        var requestedRoles = await LoadAndValidateAssignedRolesAsync(request.RoleIds, ct);

        var currentlyFullAccess = user.IsActive && user.UserRoles.Any(ur => !ur.IsDeleted && ur.Role.IsFullAccess);
        var willBeFullAccess = request.IsActive && requestedRoles.Any(r => r.IsFullAccess);
        if (currentlyFullAccess && !willBeFullAccess)
        {
            await EnsureAnotherFullAccessAdminExistsAsync(userId, ct);
        }

        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? throw new ValidationServiceException("Display name is required.")
            : request.DisplayName.Trim();
        user.IsActive = request.IsActive;
        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (request.NewPassword.Length < 8)
            {
                throw new ValidationServiceException("New password must be at least 8 characters.");
            }
            user.PasswordHash = _hasher.HashPassword(request.NewPassword);
        }

        var now = _timeProvider.GetUtcNow();
        var actorIdStr = _actor.ActorId?.ToString("D") ?? "system";
        var targetRoleIds = requestedRoles.Select(r => r.Id).ToHashSet();

        foreach (var existing in user.UserRoles)
        {
            if (!targetRoleIds.Contains(existing.RoleId) && !existing.IsDeleted)
            {
                existing.DeletedAtUtc = now;
                existing.DeletedBy = actorIdStr;
            }
        }

        var existingAll = await _db.UserRoles.IgnoreQueryFilters().Where(ur => ur.UserId == userId).ToListAsync(ct);
        foreach (var roleId in targetRoleIds)
        {
            var match = existingAll.FirstOrDefault(x => x.RoleId == roleId);
            if (match is null)
            {
                _db.UserRoles.Add(new UserRoleEntity { Id = _ids.NewId(), UserId = userId, RoleId = roleId });
            }
            else if (match.IsDeleted)
            {
                match.DeletedAtUtc = null;
                match.DeletedBy = null;
            }
        }

        await _db.SaveChangesAsync(ct);
        var reloaded = await _db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(ur => ur.Role).FirstAsync(u => u.Id == userId, ct);
        return MapUser(reloaded);
    }

    public async Task SoftDeleteUserAsync(Guid userId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Users, AccessLevel.Delete);
        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundServiceException("User not found.");

        if (!string.Equals(user.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("The user was modified by another administrator.");
        }

        if (user.IsActive && user.UserRoles.Any(ur => !ur.IsDeleted && ur.Role.IsFullAccess))
        {
            await EnsureAnotherFullAccessAdminExistsAsync(userId, ct);
        }

        user.IsActive = false;
        user.DeletedAtUtc = _timeProvider.GetUtcNow();
        user.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    public async Task RestoreUserAsync(Guid userId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Users, AccessLevel.Delete);
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundServiceException("User not found.");

        if (!string.Equals(user.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("The user was modified by another administrator.");
        }

        user.DeletedAtUtc = null;
        user.DeletedBy = null;
        user.IsActive = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.RolesAndPermissions, AccessLevel.Read);
        var roles = await _db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(MapRole).ToArray();
    }

    public async Task<RoleDto> CreateRoleAsync(UpsertRoleRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.RolesAndPermissions, AccessLevel.Alter);
        ValidateRoleRequestAndPreventEscalation(request);

        var name = request.Name.Trim();
        if (await _db.Roles.IgnoreQueryFilters().AnyAsync(r => r.Name == name, ct))
        {
            throw new ConflictServiceException($"Role '{name}' already exists.");
        }

        var role = new RoleEntity
        {
            Id = _ids.NewId(),
            Name = name,
            Description = request.Description.Trim(),
            IsFullAccess = request.IsFullAccess
        };
        _db.Roles.Add(role);

        if (!request.IsFullAccess)
        {
            foreach (var (mod, lvl) in request.ModulePermissions)
            {
                if (PermissionRegistry.IsRegistered(mod) && lvl > 0)
                {
                    _db.RolePermissions.Add(new RolePermissionEntity
                    {
                        Id = _ids.NewId(),
                        RoleId = role.Id,
                        ModuleKey = mod,
                        AccessLevel = (AccessLevel)lvl
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        var loaded = await _db.Roles.AsNoTracking().Include(r => r.Permissions).FirstAsync(r => r.Id == role.Id, ct);
        return MapRole(loaded);
    }

    public async Task<RoleDto> UpdateRoleAsync(Guid roleId, UpsertRoleRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.RolesAndPermissions, AccessLevel.Alter);
        ValidateRoleRequestAndPreventEscalation(request);

        var role = await _db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new NotFoundServiceException("Role not found.");

        if (!string.Equals(role.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("The role was modified by another administrator.");
        }

        if (role.IsFullAccess && !request.IsFullAccess)
        {
            throw new ConflictServiceException(
                "Cannot downgrade the Full Access role directly.",
                ErrorCodes.LastFullAccessAdminProtected);
        }

        role.Name = request.Name.Trim();
        role.Description = request.Description.Trim();
        role.IsFullAccess = request.IsFullAccess;

        var now = _timeProvider.GetUtcNow();
        var actorId = _actor.ActorId?.ToString("D") ?? "system";
        var allPerms = await _db.RolePermissions.IgnoreQueryFilters().Where(p => p.RoleId == roleId).ToListAsync(ct);

        foreach (var module in ModuleKeys.All)
        {
            var requested = request.IsFullAccess
                ? AccessLevel.None
                : (request.ModulePermissions.TryGetValue(module, out var raw) ? (AccessLevel)Math.Clamp(raw, 0, 3) : AccessLevel.None);

            var existing = allPerms.FirstOrDefault(p => p.ModuleKey == module);
            if (requested == AccessLevel.None)
            {
                if (existing is not null && !existing.IsDeleted)
                {
                    existing.DeletedAtUtc = now;
                    existing.DeletedBy = actorId;
                }
            }
            else if (existing is null)
            {
                _db.RolePermissions.Add(new RolePermissionEntity
                {
                    Id = _ids.NewId(),
                    RoleId = roleId,
                    ModuleKey = module,
                    AccessLevel = requested
                });
            }
            else
            {
                existing.DeletedAtUtc = null;
                existing.DeletedBy = null;
                existing.AccessLevel = requested;
            }
        }

        await _db.SaveChangesAsync(ct);
        var loaded = await _db.Roles.AsNoTracking().Include(r => r.Permissions).FirstAsync(r => r.Id == roleId, ct);
        return MapRole(loaded);
    }

    public async Task SoftDeleteRoleAsync(Guid roleId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.RolesAndPermissions, AccessLevel.Delete);
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new NotFoundServiceException("Role not found.");

        if (!string.Equals(role.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("The role was modified by another administrator.");
        }

        if (role.IsFullAccess)
        {
            throw new ConflictServiceException("Cannot delete the Full Access role.", ErrorCodes.LastFullAccessAdminProtected);
        }

        role.DeletedAtUtc = _timeProvider.GetUtcNow();
        role.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApplicationVariableDto>> ListVariablesAsync(CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ApplicationVariables, AccessLevel.Read);
        return await _cache.GetOrCreateAsync(
            CacheKeys.AllAppVariables(),
            async token =>
            {
                var rows = await _db.ApplicationVariables.AsNoTracking().OrderBy(x => x.Key).ToListAsync(token);
                return (IReadOnlyList<ApplicationVariableDto>)rows.Select(MapVariable).ToArray();
            },
            cancellationToken: ct);
    }

    public async Task<string> GetVariableValueAsync(string key, string fallback = "", CancellationToken ct = default)
    {
        var cached = await _cache.GetOrCreateAsync(
            CacheKeys.AppVariableByKey(key),
            async token =>
            {
                var row = await _db.ApplicationVariables.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, token);
                return row?.Value ?? fallback;
            },
            cancellationToken: ct);
        return string.IsNullOrEmpty(cached) ? fallback : cached;
    }

    public async Task<ApplicationVariableDto> UpsertVariableAsync(UpsertApplicationVariableRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ApplicationVariables, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw new ValidationServiceException("Variable key is required.");
        }

        var key = request.Key.Trim();
        var valueType = request.ValueType;
        ValidateTypedVariableValue(valueType, request.Value);

        var existing = await _db.ApplicationVariables.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (existing is null)
        {
            existing = new ApplicationVariableEntity
            {
                Id = _ids.NewId(),
                Key = key,
                ValueType = valueType,
                Value = request.Value,
                Description = request.Description.Trim(),
                ValidationRule = request.ValidationRule
            };
            _db.ApplicationVariables.Add(existing);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.ConcurrencyToken) &&
                !string.Equals(existing.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
            {
                throw new ConflictServiceException("This application variable was modified by another user.");
            }
            existing.ValueType = valueType;
            existing.Value = request.Value;
            existing.Description = request.Description.Trim();
            existing.ValidationRule = request.ValidationRule;
        }

        await _db.SaveChangesAsync(ct);

        // Post-commit cache invalidation for record and collection scopes!
        _cache.InvalidateScopes([
            CacheScope.ForRecord(ModuleKeys.ApplicationVariables, key),
            CacheScope.ForCollection(ModuleKeys.ApplicationVariables, "all")
        ]);

        return MapVariable(existing);
    }

    public async Task<IReadOnlyList<ErrorCodeDefinitionDto>> ListErrorCodesAsync(CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.OperationalErrors, AccessLevel.Read);
        return await _db.ErrorCodeDefinitions.AsNoTracking().OrderBy(x => x.Code)
            .Select(x => new ErrorCodeDefinitionDto
            {
                Id = x.Id,
                Code = x.Code,
                TechnicalDefinition = x.TechnicalDefinition,
                UserMessage = x.UserMessage,
                PossibleCause = x.PossibleCause,
                DefaultHttpStatus = x.DefaultHttpStatus,
                ConcurrencyToken = x.ConcurrencyToken
            })
            .ToListAsync(ct);
    }

    public async Task<PagedResult<OperationalErrorOccurrenceDto>> ListErrorOccurrencesAsync(PagedRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.OperationalErrors, AccessLevel.Read);
        var query = _db.OperationalErrorOccurrences.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.ErrorCode.ToLower().Contains(term) || x.CorrelationId.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = request.NormalizedPage;
        var pageSize = request.NormalizedPageSize;
        var items = await query.OrderByDescending(x => x.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new OperationalErrorOccurrenceDto
            {
                Id = x.Id,
                ErrorCode = x.ErrorCode,
                OccurredAtUtc = x.OccurredAtUtc,
                CorrelationId = x.CorrelationId,
                ActorId = x.ActorId,
                RequestPath = x.RequestPath,
                SanitizedTechnicalDetail = x.SanitizedTechnicalDetail,
                StackTrace = x.StackTrace
            })
            .ToListAsync(ct);

        return new PagedResult<OperationalErrorOccurrenceDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task LogErrorOccurrenceAsync(string errorCode, string correlationId, string? actorId, string? requestPath, string sanitizedDetail, string? stackTrace, CancellationToken ct = default)
    {
        _db.OperationalErrorOccurrences.Add(new OperationalErrorOccurrenceEntity
        {
            Id = _ids.NewId(),
            ErrorCode = errorCode,
            OccurredAtUtc = _timeProvider.GetUtcNow(),
            CorrelationId = correlationId,
            ActorId = actorId,
            RequestPath = requestPath,
            SanitizedTechnicalDetail = sanitizedDetail,
            StackTrace = stackTrace
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<AuditEntryDto>> ListAuditEntriesAsync(string? entityType, string? entityId, PagedRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.RolesAndPermissions, AccessLevel.Read);
        if (!_actor.IsFullAccess)
        {
            throw new ForbiddenServiceException("Full Access is required to view the cross-module audit trail.");
        }

        var query = _db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(x => x.EntityType == entityType);
        }
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            query = query.Where(x => x.EntityId == entityId);
        }

        var total = await query.CountAsync(ct);
        var page = request.NormalizedPage;
        var pageSize = request.NormalizedPageSize;
        var items = await query.OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AuditEntryDto
            {
                Id = x.Id,
                EntityType = x.EntityType,
                EntityId = x.EntityId,
                Operation = x.Operation,
                OccurredAtUtc = x.OccurredAtUtc,
                ActorId = x.ActorId,
                ActorDisplayName = x.ActorDisplayName,
                CorrelationId = x.CorrelationId,
                TransactionId = x.TransactionId,
                SchemaVersion = x.SchemaVersion,
                BeforeJson = x.BeforeJson,
                AfterJson = x.AfterJson
            })
            .ToListAsync(ct);

        return new PagedResult<AuditEntryDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    private async Task<List<RoleEntity>> LoadAndValidateAssignedRolesAsync(IReadOnlyList<Guid> roleIds, CancellationToken ct)
    {
        var distinct = roleIds.Distinct().ToList();
        var roles = await _db.Roles.Include(r => r.Permissions).Where(r => distinct.Contains(r.Id)).ToListAsync(ct);
        if (roles.Count != distinct.Count)
        {
            throw new ValidationServiceException("One or more selected roles do not exist.");
        }

        // Prevent privilege escalation: caller cannot assign Full Access or grants exceeding their own.
        foreach (var role in roles)
        {
            if (role.IsFullAccess && !_actor.IsFullAccess)
            {
                throw new ForbiddenServiceException(
                    "You cannot assign the Full Access role because you do not hold Full Access.",
                    ErrorCodes.RoleEscalationBlocked);
            }

            foreach (var perm in role.Permissions.Where(p => !p.IsDeleted))
            {
                if (!_permissions.Has(perm.ModuleKey, perm.AccessLevel))
                {
                    throw new ForbiddenServiceException(
                        $"You cannot assign a role granting {perm.AccessLevel} on '{perm.ModuleKey}' without holding that grant yourself.",
                        ErrorCodes.RoleEscalationBlocked);
                }
            }
        }

        return roles;
    }

    private void ValidateRoleRequestAndPreventEscalation(UpsertRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Role name is required.");
        }

        if (request.IsFullAccess && !_actor.IsFullAccess)
        {
            throw new ForbiddenServiceException(
                "Only a Full Access administrator can create or edit a Full Access role.",
                ErrorCodes.RoleEscalationBlocked);
        }

        foreach (var (module, levelInt) in request.ModulePermissions)
        {
            if (!PermissionRegistry.IsRegistered(module))
            {
                throw new ValidationServiceException($"Module '{module}' is not registered in the permission registry.");
            }
            var level = (AccessLevel)Math.Clamp(levelInt, 0, 3);
            if (level > AccessLevel.None && !_permissions.Has(module, level))
            {
                throw new ForbiddenServiceException(
                    $"You cannot grant {level} on '{module}' because you do not possess that level.",
                    ErrorCodes.RoleEscalationBlocked);
            }
        }
    }

    private async Task EnsureAnotherFullAccessAdminExistsAsync(Guid excludingUserId, CancellationToken ct)
    {
        var otherCount = await _db.Users
            .Where(u => u.Id != excludingUserId && u.IsActive && !u.IsSystemPrincipal)
            .Where(u => u.UserRoles.Any(ur => ur.DeletedAtUtc == null && ur.Role.DeletedAtUtc == null && ur.Role.IsFullAccess))
            .CountAsync(ct);

        if (otherCount == 0)
        {
            throw new ConflictServiceException(
                "Cannot disable, delete or demote the last active Full Access administrator.",
                ErrorCodes.LastFullAccessAdminProtected);
        }
    }

    private static void ValidateTypedVariableValue(ApplicationVariableValueType valueType, string value)
    {
        switch (valueType)
        {
            case ApplicationVariableValueType.Int:
                if (!int.TryParse(value, out _))
                {
                    throw new ValidationServiceException("Variable value must be a valid integer.");
                }
                break;
            case ApplicationVariableValueType.Bool:
                if (!bool.TryParse(value, out _))
                {
                    throw new ValidationServiceException("Variable value must be 'true' or 'false'.");
                }
                break;
            case ApplicationVariableValueType.Email:
                if (!value.Contains('@', StringComparison.Ordinal) || value.Contains(' ', StringComparison.Ordinal))
                {
                    throw new ValidationServiceException("Variable value must be a valid email address.");
                }
                break;
            case ApplicationVariableValueType.Url:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ValidationServiceException("Variable value must be a valid http/https URL.");
                }
                break;
        }
    }

    private static UserAdminDto MapUser(UserEntity u) => new()
    {
        Id = u.Id,
        Email = u.Email,
        DisplayName = u.DisplayName,
        IsActive = u.IsActive,
        IsSystemPrincipal = u.IsSystemPrincipal,
        RoleIds = u.UserRoles.Where(ur => !ur.IsDeleted && !ur.Role.IsDeleted).Select(ur => ur.RoleId).ToArray(),
        RoleNames = u.UserRoles.Where(ur => !ur.IsDeleted && !ur.Role.IsDeleted).Select(ur => ur.Role.Name).OrderBy(x => x).ToArray(),
        ConcurrencyToken = u.ConcurrencyToken,
        IsDeleted = u.IsDeleted
    };

    private static RoleDto MapRole(RoleEntity r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Description = r.Description,
        IsFullAccess = r.IsFullAccess,
        ModulePermissions = r.Permissions.Where(p => !p.IsDeleted)
            .ToDictionary(p => p.ModuleKey, p => (int)p.AccessLevel, StringComparer.Ordinal),
        ConcurrencyToken = r.ConcurrencyToken
    };

    private static ApplicationVariableDto MapVariable(ApplicationVariableEntity v) => new()
    {
        Id = v.Id,
        Key = v.Key,
        ValueType = v.ValueType,
        Value = v.Value,
        Description = v.Description,
        ValidationRule = v.ValidationRule,
        ConcurrencyToken = v.ConcurrencyToken,
        UpdatedAtUtc = v.UpdatedAtUtc
    };
}
