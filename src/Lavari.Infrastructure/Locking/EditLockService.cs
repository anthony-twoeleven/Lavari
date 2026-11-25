using Lavari.Application.Abstractions;
using Lavari.Application.Locking;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Locking;

public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task NotifyRecordChangedAsync(string moduleKey, string recordType, Guid recordId, string eventType, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyUserAsync(Guid userId, string category, string title, string message, string? recordType, Guid? recordId, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class EditLockService : IEditLockService
{
    private static readonly SemaphoreSlim SerialGate = new(1, 1);

    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;
    private readonly IRealtimeNotifier _notifier;

    public EditLockService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        TimeProvider timeProvider,
        IIdGenerator ids,
        IRealtimeNotifier notifier)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _timeProvider = timeProvider;
        _ids = ids;
        _notifier = notifier;
    }

    public async Task<EditLockStatusDto> GetStatusAsync(string recordType, Guid recordId, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Read);
        await EnsureRecordExistsAsync(normalizedType, recordId, ct);

        await SerialGate.WaitAsync(ct);
        try
        {
            await ExpireAndAdvanceAsync(normalizedType, recordId, ct);
            return await BuildStatusAsync(normalizedType, recordId, ct);
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task<EditLockStatusDto> RequestModifyAsync(string recordType, Guid recordId, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Alter);
        await EnsureRecordExistsAsync(normalizedType, recordId, ct);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var sessionId = _actor.EditingSessionId;

        await SerialGate.WaitAsync(ct);
        try
        {
            await ExpireAndAdvanceAsync(normalizedType, recordId, ct);
            var now = _timeProvider.GetUtcNow();
            var leaseDuration = await GetPolicyDurationAsync("Lock.LeaseDurationSeconds", 300, ct);

            var current = await _db.EditLeases
                .FirstOrDefaultAsync(x => x.RecordType == normalizedType && x.RecordId == recordId &&
                                          (x.State == "Active" || x.State == "Reserved"), ct);

            if (current is null)
            {
                var nextFencing = await GetNextFencingVersionAsync(normalizedType, recordId, ct);
                var lease = new EditLeaseEntity
                {
                    Id = _ids.NewId(),
                    RecordType = normalizedType,
                    RecordId = recordId,
                    ActorId = actorId,
                    ActorDisplayName = _actor.ActorDisplayName,
                    EditingSessionId = sessionId,
                    State = "Active",
                    FencingVersion = nextFencing,
                    AcquiredAtUtc = now,
                    LastRenewedAtUtc = now,
                    ExpiresAtUtc = now.Add(leaseDuration)
                };
                _db.EditLeases.Add(lease);
                await _db.SaveChangesAsync(ct);
                await _notifier.NotifyRecordChangedAsync(ModuleForRecordType(normalizedType), normalizedType, recordId, "lock.acquired", ct);
                return await BuildStatusAsync(normalizedType, recordId, ct);
            }

            // Already held by this session -> renew and return
            if (current.State == "Active" && current.ActorId == actorId && current.EditingSessionId == sessionId)
            {
                current.LastRenewedAtUtc = now;
                current.ExpiresAtUtc = now.Add(leaseDuration);
                await _db.SaveChangesAsync(ct);
                return await BuildStatusAsync(normalizedType, recordId, ct);
            }

            // Reserved for this session -> promote to Active!
            if (current.State == "Reserved" && current.ActorId == actorId && current.EditingSessionId == sessionId)
            {
                return await ClaimInternalAsync(normalizedType, recordId, current, ct);
            }

            // Otherwise join FIFO queue (prevent duplicate entry for same session & record)
            var existingQueue = await _db.EditQueueEntries
                .FirstOrDefaultAsync(x => x.RecordType == normalizedType && x.RecordId == recordId &&
                                          x.ActorId == actorId && x.EditingSessionId == sessionId &&
                                          (x.State == "Queued" || x.State == "Reserved"), ct);

            if (existingQueue is null)
            {
                var maxSeq = await _db.EditQueueEntries
                    .Where(x => x.RecordType == normalizedType && x.RecordId == recordId)
                    .Select(x => (long?)x.SequenceNumber)
                    .MaxAsync(ct) ?? 0L;

                _db.EditQueueEntries.Add(new EditQueueEntryEntity
                {
                    Id = _ids.NewId(),
                    RecordType = normalizedType,
                    RecordId = recordId,
                    ActorId = actorId,
                    ActorDisplayName = _actor.ActorDisplayName,
                    EditingSessionId = sessionId,
                    SequenceNumber = maxSeq + 1,
                    State = "Queued",
                    RequestedAtUtc = now
                });
                await _db.SaveChangesAsync(ct);
                await _notifier.NotifyRecordChangedAsync(ModuleForRecordType(normalizedType), normalizedType, recordId, "lock.queued", ct);
            }

            return await BuildStatusAsync(normalizedType, recordId, ct);
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task<EditLockStatusDto> ClaimReservationAsync(string recordType, Guid recordId, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Alter);
        await EnsureRecordExistsAsync(normalizedType, recordId, ct);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var sessionId = _actor.EditingSessionId;

        await SerialGate.WaitAsync(ct);
        try
        {
            await ExpireAndAdvanceAsync(normalizedType, recordId, ct);
            var current = await _db.EditLeases
                .FirstOrDefaultAsync(x => x.RecordType == normalizedType && x.RecordId == recordId && x.State == "Reserved", ct);

            if (current is null || current.ActorId != actorId || current.EditingSessionId != sessionId)
            {
                throw new ConflictServiceException(
                    "Your editing reservation has expired or is no longer active.",
                    ErrorCodes.ReservationExpired,
                    "Click Modify again to acquire a lock or rejoin the queue.");
            }

            return await ClaimInternalAsync(normalizedType, recordId, current, ct);
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task<EditLockStatusDto> RenewLeaseAsync(string recordType, Guid recordId, Guid leaseId, long fencingVersion, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Alter);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var sessionId = _actor.EditingSessionId;

        await SerialGate.WaitAsync(ct);
        try
        {
            await ExpireAndAdvanceAsync(normalizedType, recordId, ct);
            var current = await _db.EditLeases
                .FirstOrDefaultAsync(x => x.Id == leaseId && x.RecordType == normalizedType && x.RecordId == recordId, ct);

            if (current is null || current.State != "Active" || current.ActorId != actorId ||
                current.EditingSessionId != sessionId || current.FencingVersion != fencingVersion)
            {
                throw new ConflictServiceException(
                    "Your editing lock is no longer active.",
                    ErrorCodes.EditLockExpired,
                    "Another editor may now hold the lock. Reacquire Modify and review the latest version before saving.");
            }

            var now = _timeProvider.GetUtcNow();
            var leaseDuration = await GetPolicyDurationAsync("Lock.LeaseDurationSeconds", 300, ct);
            current.LastRenewedAtUtc = now;
            current.ExpiresAtUtc = now.Add(leaseDuration);
            await _db.SaveChangesAsync(ct);

            return await BuildStatusAsync(normalizedType, recordId, ct);
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task<EditLockStatusDto> ReleaseLeaseAsync(string recordType, Guid recordId, Guid? leaseId = null, long? fencingVersion = null, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Read);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var sessionId = _actor.EditingSessionId;

        await SerialGate.WaitAsync(ct);
        try
        {
            await ExpireAndAdvanceAsync(normalizedType, recordId, ct);

            var current = await _db.EditLeases
                .FirstOrDefaultAsync(x => x.RecordType == normalizedType && x.RecordId == recordId &&
                                          (x.State == "Active" || x.State == "Reserved") &&
                                          x.ActorId == actorId && x.EditingSessionId == sessionId, ct);

            if (current is not null)
            {
                if (leaseId.HasValue && current.Id != leaseId.Value)
                {
                    throw new ConflictServiceException("The lease identifier does not match the active lock.", ErrorCodes.EditLockExpired);
                }

                if (fencingVersion.HasValue && current.FencingVersion != fencingVersion.Value)
                {
                    throw new ConflictServiceException("The fencing token is stale.", ErrorCodes.EditLockExpired);
                }

                current.State = "Released";
                if (current.QueueEntryId.HasValue)
                {
                    var q = await _db.EditQueueEntries.FirstOrDefaultAsync(x => x.Id == current.QueueEntryId.Value, ct);
                    if (q is not null && q.State == "Reserved")
                    {
                        q.State = "Cancelled";
                    }
                }

                await _db.SaveChangesAsync(ct);
                await AdvanceQueueIfFreeAsync(normalizedType, recordId, ct);
                await _notifier.NotifyRecordChangedAsync(ModuleForRecordType(normalizedType), normalizedType, recordId, "lock.released", ct);
            }

            return await BuildStatusAsync(normalizedType, recordId, ct);
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task<EditLockStatusDto> CancelQueueEntryAsync(string recordType, Guid recordId, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Read);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var sessionId = _actor.EditingSessionId;

        await SerialGate.WaitAsync(ct);
        try
        {
            await ExpireAndAdvanceAsync(normalizedType, recordId, ct);

            var entries = await _db.EditQueueEntries
                .Where(x => x.RecordType == normalizedType && x.RecordId == recordId &&
                            x.ActorId == actorId && x.EditingSessionId == sessionId &&
                            (x.State == "Queued" || x.State == "Reserved"))
                .ToListAsync(ct);

            var wasReserved = false;
            foreach (var entry in entries)
            {
                if (entry.State == "Reserved")
                {
                    wasReserved = true;
                }
                entry.State = "Cancelled";
            }

            if (wasReserved)
            {
                var resLease = await _db.EditLeases
                    .FirstOrDefaultAsync(x => x.RecordType == normalizedType && x.RecordId == recordId &&
                                              x.State == "Reserved" && x.ActorId == actorId && x.EditingSessionId == sessionId, ct);
                if (resLease is not null)
                {
                    resLease.State = "Cancelled";
                }
            }

            if (entries.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
                if (wasReserved)
                {
                    await AdvanceQueueIfFreeAsync(normalizedType, recordId, ct);
                }
            }

            return await BuildStatusAsync(normalizedType, recordId, ct);
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task EnsureActiveLeaseHeldAsync(string recordType, Guid recordId, Guid? leaseId, long? fencingVersion, CancellationToken ct = default)
    {
        var normalizedType = NormalizeRecordType(recordType);
        _permissions.Require(ModuleForRecordType(normalizedType), AccessLevel.Alter);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var sessionId = _actor.EditingSessionId;
        var now = _timeProvider.GetUtcNow();

        var active = await _db.EditLeases
            .FirstOrDefaultAsync(x => x.RecordType == normalizedType && x.RecordId == recordId && x.State == "Active", ct);

        if (active is null)
        {
            throw new ConflictServiceException(
                $"You must select Modify to acquire an exclusive edit lock on this {normalizedType} before saving.",
                ErrorCodes.EditLockRequired,
                "Click Modify to begin editing.");
        }

        if (active.ExpiresAtUtc <= now)
        {
            active.State = "Expired";
            await _db.SaveChangesAsync(ct);
            await AdvanceQueueIfFreeAsync(normalizedType, recordId, ct);
            throw new ConflictServiceException(
                $"Your editing lock on this {normalizedType} has expired.",
                ErrorCodes.EditLockExpired,
                "Click Modify to reacquire the lock and review the latest version before saving.");
        }

        if (active.ActorId != actorId || active.EditingSessionId != sessionId)
        {
            throw new ConflictServiceException(
                $"This {normalizedType} is currently locked for editing by another session.",
                ErrorCodes.EditLockHeldByAnother,
                "Click Modify to join the queue and be notified when editing opens.");
        }

        if (leaseId.HasValue && active.Id != leaseId.Value)
        {
            throw new ConflictServiceException(
                "Your editing session lock has been superseded.",
                ErrorCodes.EditLockExpired,
                "Reload the latest version and reacquire Modify.");
        }

        if (fencingVersion.HasValue && active.FencingVersion != fencingVersion.Value)
        {
            throw new ConflictServiceException(
                "Your editing fencing token is stale.",
                ErrorCodes.EditLockExpired,
                "Reload the latest version and reacquire Modify.");
        }
    }

    public async Task<int> SweepExpiredLeasesAsync(CancellationToken ct = default)
    {
        await SerialGate.WaitAsync(ct);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var expired = await _db.EditLeases
                .Where(x => (x.State == "Active" || x.State == "Reserved") && x.ExpiresAtUtc <= now)
                .ToListAsync(ct);

            if (expired.Count == 0)
            {
                return 0;
            }

            var records = new HashSet<(string Type, Guid Id)>();
            foreach (var lease in expired)
            {
                lease.State = "Expired";
                if (lease.QueueEntryId.HasValue)
                {
                    var q = await _db.EditQueueEntries.FirstOrDefaultAsync(x => x.Id == lease.QueueEntryId.Value, ct);
                    if (q is not null && q.State == "Reserved")
                    {
                        q.State = "Expired";
                    }
                }
                records.Add((lease.RecordType, lease.RecordId));
            }

            await _db.SaveChangesAsync(ct);

            foreach (var (type, id) in records)
            {
                await AdvanceQueueIfFreeAsync(type, id, ct);
            }

            return expired.Count;
        }
        finally
        {
            SerialGate.Release();
        }
    }

    public async Task<IReadOnlyList<InAppNotificationDto>> GetNotificationsAsync(bool unreadOnly = false, CancellationToken ct = default)
    {
        _permissions.RequireAuthenticated();
        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();

        var query = _db.InAppNotifications
            .AsNoTracking()
            .Where(x => x.RecipientUserId == actorId);

        if (unreadOnly)
        {
            query = query.Where(x => !x.IsRead);
        }

        return await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .Select(x => new InAppNotificationDto
            {
                Id = x.Id,
                Category = x.Category,
                Title = x.Title,
                Message = x.Message,
                RecordType = x.RecordType,
                RecordId = x.RecordId,
                ReferenceId = x.ReferenceId,
                IsRead = x.IsRead,
                CreatedAtUtc = x.CreatedAtUtc,
                ExpiresAtUtc = x.ExpiresAtUtc
            })
            .ToListAsync(ct);
    }

    public async Task MarkNotificationReadAsync(Guid notificationId, CancellationToken ct = default)
    {
        _permissions.RequireAuthenticated();
        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();

        var entity = await _db.InAppNotifications
            .FirstOrDefaultAsync(x => x.Id == notificationId && x.RecipientUserId == actorId, ct);

        if (entity is null)
        {
            throw new NotFoundServiceException("Notification not found.");
        }

        if (!entity.IsRead)
        {
            entity.IsRead = true;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<EditLockStatusDto> ClaimInternalAsync(string recordType, Guid recordId, EditLeaseEntity reservedLease, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var leaseDuration = await GetPolicyDurationAsync("Lock.LeaseDurationSeconds", 300, ct);
        reservedLease.State = "Active";
        reservedLease.AcquiredAtUtc = now;
        reservedLease.LastRenewedAtUtc = now;
        reservedLease.ExpiresAtUtc = now.Add(leaseDuration);

        if (reservedLease.QueueEntryId.HasValue)
        {
            var q = await _db.EditQueueEntries.FirstOrDefaultAsync(x => x.Id == reservedLease.QueueEntryId.Value, ct);
            if (q is not null)
            {
                q.State = "Claimed";
            }
        }

        await _db.SaveChangesAsync(ct);
        await _notifier.NotifyRecordChangedAsync(ModuleForRecordType(recordType), recordType, recordId, "lock.claimed", ct);
        return await BuildStatusAsync(recordType, recordId, ct);
    }

    private async Task ExpireAndAdvanceAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var expired = await _db.EditLeases
            .Where(x => x.RecordType == recordType && x.RecordId == recordId &&
                        (x.State == "Active" || x.State == "Reserved") &&
                        x.ExpiresAtUtc <= now)
            .ToListAsync(ct);

        if (expired.Count > 0)
        {
            foreach (var lease in expired)
            {
                lease.State = "Expired";
                if (lease.QueueEntryId.HasValue)
                {
                    var q = await _db.EditQueueEntries.FirstOrDefaultAsync(x => x.Id == lease.QueueEntryId.Value, ct);
                    if (q is not null && q.State == "Reserved")
                    {
                        q.State = "Expired";
                    }
                }
            }

            await _db.SaveChangesAsync(ct);
            await AdvanceQueueIfFreeAsync(recordType, recordId, ct);
        }
    }

    private async Task AdvanceQueueIfFreeAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        var hasActiveOrReserved = await _db.EditLeases
            .AnyAsync(x => x.RecordType == recordType && x.RecordId == recordId &&
                           (x.State == "Active" || x.State == "Reserved"), ct);
        if (hasActiveOrReserved)
        {
            return;
        }

        var moduleKey = ModuleForRecordType(recordType);
        var queued = await _db.EditQueueEntries
            .Where(x => x.RecordType == recordType && x.RecordId == recordId && x.State == "Queued")
            .OrderBy(x => x.SequenceNumber)
            .ToListAsync(ct);

        if (queued.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var claimWindow = await GetPolicyDurationAsync("Lock.ReservationClaimSeconds", 120, ct);

        foreach (var candidate in queued)
        {
            var isEligible = await UserStillHasAlterPermissionAsync(candidate.ActorId, moduleKey, ct);
            if (!isEligible)
            {
                candidate.State = "Denied";
                continue;
            }

            var nextFencing = await GetNextFencingVersionAsync(recordType, recordId, ct);
            var reservedUntil = now.Add(claimWindow);

            candidate.State = "Reserved";
            candidate.ReservedUntilUtc = reservedUntil;

            var reservationLease = new EditLeaseEntity
            {
                Id = _ids.NewId(),
                RecordType = recordType,
                RecordId = recordId,
                ActorId = candidate.ActorId,
                ActorDisplayName = candidate.ActorDisplayName,
                EditingSessionId = candidate.EditingSessionId,
                State = "Reserved",
                FencingVersion = nextFencing,
                AcquiredAtUtc = now,
                LastRenewedAtUtc = now,
                ExpiresAtUtc = reservedUntil,
                QueueEntryId = candidate.Id
            };
            _db.EditLeases.Add(reservationLease);

            var notification = new InAppNotificationEntity
            {
                Id = _ids.NewId(),
                RecipientUserId = candidate.ActorId,
                Category = "edit_turn_ready",
                Title = $"{recordType} is now open for editing",
                Message = $"It is your turn to edit this {recordType}. Claim your turn before it expires to load the latest version.",
                RecordType = recordType,
                RecordId = recordId,
                ReferenceId = reservationLease.Id,
                IsRead = false,
                ExpiresAtUtc = reservedUntil
            };
            _db.InAppNotifications.Add(notification);

            await _db.SaveChangesAsync(ct);
            await _notifier.NotifyUserAsync(
                candidate.ActorId,
                notification.Category,
                notification.Title,
                notification.Message,
                recordType,
                recordId,
                ct);
            return;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<bool> UserStillHasAlterPermissionAsync(Guid userId, string moduleKey, CancellationToken ct)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
        {
            return false;
        }

        var roleSnapshots = user.UserRoles
            .Where(ur => !ur.IsDeleted && !ur.Role.IsDeleted)
            .Select(ur => new RoleGrantSnapshot(
                ur.Role.Id,
                ur.Role.Name,
                ur.Role.IsFullAccess,
                ur.Role.Permissions
                    .Where(p => !p.IsDeleted)
                    .ToDictionary(p => p.ModuleKey, p => p.AccessLevel, StringComparer.Ordinal)));

        var effective = PermissionRegistry.ComputeEffectiveGrants(user.IsActive, roleSnapshots);
        return PermissionRegistry.Satisfies(effective, moduleKey, AccessLevel.Alter);
    }

    private async Task<EditLockStatusDto> BuildStatusAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        var actorId = _actor.ActorId;
        var sessionId = _actor.EditingSessionId;

        var current = await _db.EditLeases
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RecordType == recordType && x.RecordId == recordId &&
                                      (x.State == "Active" || x.State == "Reserved"), ct);

        var queue = await _db.EditQueueEntries
            .AsNoTracking()
            .Where(x => x.RecordType == recordType && x.RecordId == recordId &&
                        (x.State == "Queued" || x.State == "Reserved"))
            .OrderBy(x => x.SequenceNumber)
            .ToListAsync(ct);

        var myQueueIndex = queue.FindIndex(x => x.ActorId == actorId && x.EditingSessionId == sessionId);
        var myQueueEntry = myQueueIndex >= 0 ? queue[myQueueIndex] : null;
        var isHeldByMe = current is { State: "Active" } && current.ActorId == actorId && current.EditingSessionId == sessionId;
        var isReservedForMe = current is { State: "Reserved" } && current.ActorId == actorId && current.EditingSessionId == sessionId;

        return new EditLockStatusDto
        {
            RecordType = recordType,
            RecordId = recordId,
            IsLocked = current is not null,
            IsHeldByCurrentSession = isHeldByMe,
            IsReservedForCurrentSession = isReservedForMe,
            IsCurrentSessionQueued = myQueueEntry is { State: "Queued" },
            QueuePosition = myQueueEntry is { State: "Queued" } ? queue.Where(q => q.State == "Queued").ToList().FindIndex(q => q.Id == myQueueEntry.Id) + 1 : null,
            WaitingCount = queue.Count(q => q.State == "Queued"),
            LeaseId = (isHeldByMe || isReservedForMe) ? current?.Id : null,
            FencingVersion = (isHeldByMe || isReservedForMe) ? current?.FencingVersion : null,
            HolderDisplayName = current is not null && (_permissions.Has(ModuleKeys.Users, AccessLevel.Read) || isHeldByMe || isReservedForMe)
                ? current.ActorDisplayName
                : (current is not null ? "Another staff member" : null),
            ExpiresAtUtc = current is { State: "Active" } ? current.ExpiresAtUtc : null,
            ReservedUntilUtc = isReservedForMe ? current?.ExpiresAtUtc : null,
            QueueEntryId = myQueueEntry?.Id
        };
    }

    private async Task<long> GetNextFencingVersionAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        var max = await _db.EditLeases
            .IgnoreQueryFilters()
            .Where(x => x.RecordType == recordType && x.RecordId == recordId)
            .Select(x => (long?)x.FencingVersion)
            .MaxAsync(ct) ?? 0L;
        return max + 1;
    }

    private async Task<TimeSpan> GetPolicyDurationAsync(string variableKey, int fallbackSeconds, CancellationToken ct)
    {
        var variable = await _db.ApplicationVariables
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == variableKey, ct);
        if (variable is not null && int.TryParse(variable.Value, out var secs) && secs is >= 10 and <= 3600)
        {
            return TimeSpan.FromSeconds(secs);
        }
        return TimeSpan.FromSeconds(fallbackSeconds);
    }

    private async Task EnsureRecordExistsAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        var exists = recordType switch
        {
            LockableRecordTypes.Job => await _db.Jobs.AnyAsync(x => x.Id == recordId, ct),
            LockableRecordTypes.Quote => await _db.FinancialDocuments.AnyAsync(x => x.Id == recordId && x.DocumentType == "Quote", ct),
            LockableRecordTypes.Invoice => await _db.FinancialDocuments.AnyAsync(x => x.Id == recordId && x.DocumentType == "Invoice", ct),
            _ => false
        };

        if (!exists)
        {
            throw new NotFoundServiceException($"The requested {recordType} does not exist or is inactive.");
        }
    }

    private static string NormalizeRecordType(string recordType) => recordType.Trim().ToLowerInvariant() switch
    {
        "job" => LockableRecordTypes.Job,
        "quote" => LockableRecordTypes.Quote,
        "invoice" => LockableRecordTypes.Invoice,
        _ => throw new ValidationServiceException($"'{recordType}' is not a lockable record type.")
    };

    private static string ModuleForRecordType(string recordType) => recordType switch
    {
        LockableRecordTypes.Job => ModuleKeys.Jobs,
        LockableRecordTypes.Quote => ModuleKeys.Quotes,
        LockableRecordTypes.Invoice => ModuleKeys.Invoices,
        _ => throw new ValidationServiceException($"Unknown lockable record type '{recordType}'.")
    };
}
