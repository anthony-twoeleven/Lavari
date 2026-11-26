using Lavari.Application.Abstractions;
using Lavari.Application.Locking;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Contracts.Caching;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Domain.Time;
using Lavari.Infrastructure.Locking;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Services;

public sealed class ClientAndJobService : IClientAndJobService
{
    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IEditLockService _editLocks;
    private readonly IAppCacheService _cache;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;
    private readonly IRealtimeNotifier _notifier;

    public ClientAndJobService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IEditLockService editLocks,
        IAppCacheService cache,
        TimeProvider timeProvider,
        IIdGenerator ids,
        IRealtimeNotifier notifier)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _editLocks = editLocks;
        _cache = cache;
        _timeProvider = timeProvider;
        _ids = ids;
        _notifier = notifier;
    }

    public async Task<PagedResult<ClientDto>> ListClientsAsync(PagedRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Clients, request.IncludeDeleted ? AccessLevel.Delete : AccessLevel.Read);

        IQueryable<ClientEntity> query = request.IncludeDeleted ? _db.Clients.IgnoreQueryFilters() : _db.Clients;
        query = query.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(c => c.Name.ToLower().Contains(term) ||
                                     (c.ContactName != null && c.ContactName.ToLower().Contains(term)) ||
                                     (c.Email != null && c.Email.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var page = request.NormalizedPage;
        var pageSize = request.NormalizedPageSize;
        var rows = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ClientDto
            {
                Id = c.Id,
                Name = c.Name,
                ContactName = c.ContactName,
                Email = c.Email,
                Phone = c.Phone,
                BillingAddress = c.BillingAddress,
                Notes = c.Notes,
                ConcurrencyToken = c.ConcurrencyToken,
                IsDeleted = c.DeletedAtUtc != null,
                ActiveGroupCount = c.JobGroups.Count(g => g.DeletedAtUtc == null),
                UpdatedAtUtc = c.UpdatedAtUtc
            })
            .ToListAsync(ct);

        return new PagedResult<ClientDto> { Items = rows, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<ClientDto> GetClientAsync(Guid id, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
        var c = await _db.Clients.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(c => new ClientDto
            {
                Id = c.Id,
                Name = c.Name,
                ContactName = c.ContactName,
                Email = c.Email,
                Phone = c.Phone,
                BillingAddress = c.BillingAddress,
                Notes = c.Notes,
                ConcurrencyToken = c.ConcurrencyToken,
                IsDeleted = c.DeletedAtUtc != null,
                ActiveGroupCount = c.JobGroups.Count(g => g.DeletedAtUtc == null),
                UpdatedAtUtc = c.UpdatedAtUtc
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundServiceException("Client not found.");
        return c;
    }

    public async Task<ClientDto> CreateClientAsync(UpsertClientRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Client name is required.");
        }

        var entity = new ClientEntity
        {
            Id = _ids.NewId(),
            Name = request.Name.Trim(),
            ContactName = request.ContactName?.Trim(),
            Email = request.Email?.Trim(),
            Phone = request.Phone?.Trim(),
            BillingAddress = request.BillingAddress?.Trim(),
            Notes = request.Notes?.Trim()
        };
        _db.Clients.Add(entity);
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Clients));
        await _notifier.NotifyRecordChangedAsync(ModuleKeys.Clients, "Client", entity.Id, "client.created", ct);
        return await GetClientAsync(entity.Id, ct);
    }

    public async Task<ClientDto> UpdateClientAsync(Guid id, UpsertClientRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Client name is required.");
        }

        var entity = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundServiceException("Client not found.");

        if (!string.Equals(entity.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Client was modified by another user.");
        }

        entity.Name = request.Name.Trim();
        entity.ContactName = request.ContactName?.Trim();
        entity.Email = request.Email?.Trim();
        entity.Phone = request.Phone?.Trim();
        entity.BillingAddress = request.BillingAddress?.Trim();
        entity.Notes = request.Notes?.Trim();

        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Clients));
        await _notifier.NotifyRecordChangedAsync(ModuleKeys.Clients, "Client", entity.Id, "client.updated", ct);
        return await GetClientAsync(entity.Id, ct);
    }

    public async Task SoftDeleteClientAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Delete);
        var entity = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundServiceException("Client not found.");

        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Client was modified by another user.");
        }

        if (await _db.JobGroups.AnyAsync(g => g.ClientId == id, ct))
        {
            throw new ConflictServiceException(
                "Cannot delete this client while active job groups exist.",
                ErrorCodes.ActiveChildrenPreventDelete);
        }

        entity.DeletedAtUtc = _timeProvider.GetUtcNow();
        entity.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Clients));
    }

    public async Task RestoreClientAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Delete);
        var entity = await _db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundServiceException("Client not found.");

        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Client was modified by another user.");
        }

        entity.DeletedAtUtc = null;
        entity.DeletedBy = null;
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Clients));
    }

    public async Task<IReadOnlyList<JobGroupDto>> ListJobGroupsAsync(Guid? clientId, bool includeDeleted = false, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobGroups, includeDeleted ? AccessLevel.Delete : AccessLevel.Read);
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);

        IQueryable<JobGroupEntity> query = includeDeleted ? _db.JobGroups.IgnoreQueryFilters() : _db.JobGroups;
        query = query.AsNoTracking();
        if (clientId.HasValue)
        {
            query = query.Where(g => g.ClientId == clientId.Value);
        }

        return await query.OrderBy(g => g.Name)
            .Select(g => new JobGroupDto
            {
                Id = g.Id,
                ClientId = g.ClientId,
                ClientName = g.Client.Name,
                Name = g.Name,
                Description = g.Description,
                ActiveJobCount = g.Jobs.Count(j => j.DeletedAtUtc == null),
                ConcurrencyToken = g.ConcurrencyToken,
                IsDeleted = g.DeletedAtUtc != null
            })
            .ToListAsync(ct);
    }

    public async Task<JobGroupDto> CreateJobGroupAsync(UpsertJobGroupRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobGroups, AccessLevel.Alter);
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Job group name is required.");
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == request.ClientId, ct)
            ?? throw new ConflictServiceException("The selected client is inactive or does not exist.", ErrorCodes.ParentInactive);

        var entity = new JobGroupEntity
        {
            Id = _ids.NewId(),
            ClientId = client.Id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim()
        };
        _db.JobGroups.Add(entity);
        await _db.SaveChangesAsync(ct);
        return (await ListJobGroupsAsync(client.Id, false, ct)).First(g => g.Id == entity.Id);
    }

    public async Task<JobGroupDto> UpdateJobGroupAsync(Guid id, UpsertJobGroupRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobGroups, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Job group name is required.");
        }

        var entity = await _db.JobGroups.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundServiceException("Job group not found.");

        if (!string.Equals(entity.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Job group was modified by another user.");
        }

        entity.Name = request.Name.Trim();
        entity.Description = request.Description?.Trim();
        await _db.SaveChangesAsync(ct);
        return (await ListJobGroupsAsync(entity.ClientId, false, ct)).First(g => g.Id == entity.Id);
    }

    public async Task SoftDeleteJobGroupAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobGroups, AccessLevel.Delete);
        var entity = await _db.JobGroups.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundServiceException("Job group not found.");

        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Job group was modified by another user.");
        }

        if (await _db.Jobs.AnyAsync(j => j.JobGroupId == id, ct))
        {
            throw new ConflictServiceException(
                "Cannot delete this job group while active jobs exist.",
                ErrorCodes.ActiveChildrenPreventDelete);
        }

        entity.DeletedAtUtc = _timeProvider.GetUtcNow();
        entity.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    public async Task RestoreJobGroupAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobGroups, AccessLevel.Delete);
        var entity = await _db.JobGroups.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundServiceException("Job group not found.");

        if (!await _db.Clients.AnyAsync(c => c.Id == entity.ClientId, ct))
        {
            throw new ConflictServiceException("Restore the parent client before restoring this job group.", ErrorCodes.ParentInactive);
        }

        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Job group was modified by another user.");
        }

        entity.DeletedAtUtc = null;
        entity.DeletedBy = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<JobDto>> ListJobsAsync(Guid? clientId, Guid? jobGroupId, PagedRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, request.IncludeDeleted ? AccessLevel.Delete : AccessLevel.Read);
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);

        IQueryable<JobEntity> query = request.IncludeDeleted ? _db.Jobs.IgnoreQueryFilters() : _db.Jobs;
        query = query.AsNoTracking();

        if (clientId.HasValue)
        {
            query = query.Where(j => j.ClientId == clientId.Value);
        }
        if (jobGroupId.HasValue)
        {
            query = query.Where(j => j.JobGroupId == jobGroupId.Value);
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(j => j.Name.ToLower().Contains(term) ||
                                     j.Client.Name.ToLower().Contains(term) ||
                                     (j.PoNumber != null && j.PoNumber.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var page = request.NormalizedPage;
        var pageSize = request.NormalizedPageSize;
        var offset = (long)(page - 1) * pageSize;
        if (offset >= total)
        {
            return new PagedResult<JobDto> { Items = [], TotalCount = total, Page = page, PageSize = pageSize };
        }

        var items = await query.OrderByDescending(j => j.UpdatedAtUtc)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(j => new JobDto
            {
                Id = j.Id,
                ClientId = j.ClientId,
                ClientName = j.Client.Name,
                JobGroupId = j.JobGroupId,
                JobGroupName = j.JobGroup.Name,
                Name = j.Name,
                Description = j.Description,
                Status = j.Status,
                TimeZoneIana = j.TimeZoneIana,
                DefaultCurrency = j.DefaultCurrency,
                PoNumber = j.PoNumber,
                DefaultHourlyRateCents = j.DefaultHourlyRateCents,
                InternalHourlyCostCents = j.InternalHourlyCostCents,
                Notes = j.Notes,
                ConcurrencyToken = j.ConcurrencyToken,
                IsDeleted = j.DeletedAtUtc != null,
                CreatedAtUtc = j.CreatedAtUtc,
                UpdatedAtUtc = j.UpdatedAtUtc
            })
            .ToListAsync(ct);

        return new PagedResult<JobDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<JobDto> GetJobAsync(Guid id, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);

        return await _db.Jobs.AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new JobDto
            {
                Id = j.Id,
                ClientId = j.ClientId,
                ClientName = j.Client.Name,
                JobGroupId = j.JobGroupId,
                JobGroupName = j.JobGroup.Name,
                Name = j.Name,
                Description = j.Description,
                Status = j.Status,
                TimeZoneIana = j.TimeZoneIana,
                DefaultCurrency = j.DefaultCurrency,
                PoNumber = j.PoNumber,
                DefaultHourlyRateCents = j.DefaultHourlyRateCents,
                InternalHourlyCostCents = j.InternalHourlyCostCents,
                Notes = j.Notes,
                ConcurrencyToken = j.ConcurrencyToken,
                IsDeleted = j.DeletedAtUtc != null,
                CreatedAtUtc = j.CreatedAtUtc,
                UpdatedAtUtc = j.UpdatedAtUtc
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundServiceException("Job not found.");
    }

    public async Task<JobDto> CreateJobAsync(UpsertJobRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Alter);
        _permissions.Require(ModuleKeys.JobGroups, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Job name is required.");
        }

        var tz = JobTimeZone.Create(request.TimeZoneIana);
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.DefaultCurrency);
        _ = new MoneyCents(request.DefaultHourlyRateCents, currency);

        var group = await _db.JobGroups.Include(g => g.Client).FirstOrDefaultAsync(g => g.Id == request.JobGroupId, ct)
            ?? throw new ConflictServiceException("The selected job group is inactive or does not exist.", ErrorCodes.ParentInactive);
        if (group.Client.IsDeleted)
        {
            throw new ConflictServiceException("The parent client is inactive.", ErrorCodes.ParentInactive);
        }

        var job = new JobEntity
        {
            Id = _ids.NewId(),
            ClientId = group.ClientId,
            JobGroupId = group.Id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Status = string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim(),
            TimeZoneIana = tz.IanaId,
            DefaultCurrency = currency,
            PoNumber = request.PoNumber?.Trim(),
            DefaultHourlyRateCents = request.DefaultHourlyRateCents,
            InternalHourlyCostCents = request.InternalHourlyCostCents,
            Notes = request.Notes?.Trim()
        };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Jobs));
        await _notifier.NotifyRecordChangedAsync(ModuleKeys.Jobs, "Job", job.Id, "job.created", ct);
        return await GetJobAsync(job.Id, ct);
    }

    public async Task<JobDto> UpdateJobAsync(Guid id, UpsertJobRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Alter);
        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, id, request.LeaseId, request.FencingVersion, ct);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Job name is required.");
        }

        var tz = JobTimeZone.Create(request.TimeZoneIana);
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.DefaultCurrency);
        _ = new MoneyCents(request.DefaultHourlyRateCents, currency);

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        if (!string.Equals(job.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Job was modified by another user.");
        }

        if (job.JobGroupId != request.JobGroupId)
        {
            var newGroup = await _db.JobGroups.FirstOrDefaultAsync(g => g.Id == request.JobGroupId, ct)
                ?? throw new ConflictServiceException("Target job group is inactive.", ErrorCodes.ParentInactive);
            if (newGroup.ClientId != job.ClientId)
            {
                throw new ValidationServiceException("Moving a job across different clients is not permitted without explicit transfer approval.");
            }
            job.JobGroupId = newGroup.Id;
        }

        job.Name = request.Name.Trim();
        job.Description = request.Description?.Trim();
        job.Status = string.IsNullOrWhiteSpace(request.Status) ? job.Status : request.Status.Trim();
        job.TimeZoneIana = tz.IanaId;
        job.DefaultCurrency = currency;
        job.PoNumber = request.PoNumber?.Trim();
        job.DefaultHourlyRateCents = request.DefaultHourlyRateCents;
        job.InternalHourlyCostCents = request.InternalHourlyCostCents;
        job.Notes = request.Notes?.Trim();

        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForRecord(ModuleKeys.Jobs, id));
        await _notifier.NotifyRecordChangedAsync(ModuleKeys.Jobs, "Job", job.Id, "job.updated", ct);
        return await GetJobAsync(job.Id, ct);
    }

    public async Task SoftDeleteJobAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Delete);
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        if (!string.Equals(job.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Job was modified by another user.");
        }

        job.DeletedAtUtc = _timeProvider.GetUtcNow();
        job.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Jobs));
    }

    public async Task RestoreJobAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Delete);
        var job = await _db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        if (!await _db.JobGroups.AnyAsync(g => g.Id == job.JobGroupId, ct) ||
            !await _db.Clients.AnyAsync(c => c.Id == job.ClientId, ct))
        {
            throw new ConflictServiceException("Restore the parent client and job group before restoring this job.", ErrorCodes.ParentInactive);
        }

        if (!string.Equals(job.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Job was modified by another user.");
        }

        job.DeletedAtUtc = null;
        job.DeletedBy = null;
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.Jobs));
    }
}

public sealed class JobDateService : IJobDateService
{
    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IEditLockService _editLocks;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public JobDateService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IEditLockService editLocks,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _editLocks = editLocks;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<IReadOnlyList<JobDateDto>> ListForJobAsync(Guid jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.JobDates, AccessLevel.Read);

        return await _db.JobDates.AsNoTracking()
            .Where(d => d.JobId == jobId)
            .OrderBy(d => d.CalendarDate)
            .ThenBy(d => d.OccurredAtUtc)
            .Select(d => Map(d))
            .ToListAsync(ct);
    }

    public async Task<JobDateDto> CreateAsync(Guid jobId, UpsertJobDateRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.JobDates, AccessLevel.Alter);
        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, jobId, request.LeaseId, request.FencingVersion, ct);

        ValidateJobDate(request);
        if (!await _db.Jobs.AnyAsync(j => j.Id == jobId, ct))
        {
            throw new NotFoundServiceException("Job not found.");
        }

        var entity = new JobDateEntity
        {
            Id = _ids.NewId(),
            JobId = jobId,
            Label = request.Label.Trim(),
            Description = request.Description?.Trim(),
            CategoryKey = request.CategoryKey.Trim().ToLowerInvariant(),
            CategoryLabel = request.CategoryLabel.Trim(),
            Status = request.Status.Trim(),
            IsActual = request.IsActual,
            CalendarDate = request.CalendarDate,
            OccurredAtUtc = request.OccurredAtUtc?.ToUniversalTime(),
            ForecastAmountCents = request.ForecastAmountCents,
            ForecastCurrency = request.ForecastAmountCents.HasValue ? MoneyCents.NormalizeAndValidateCurrency(request.ForecastCurrency ?? "AUD") : null,
            ForecastDirection = request.ForecastDirection,
            LinkedEntityType = request.LinkedEntityType,
            LinkedEntityId = request.LinkedEntityId
        };
        _db.JobDates.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<JobDateDto> UpdateAsync(Guid id, UpsertJobDateRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobDates, AccessLevel.Alter);
        var entity = await _db.JobDates.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundServiceException("Job date/milestone not found.");

        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, entity.JobId, request.LeaseId, request.FencingVersion, ct);
        ValidateJobDate(request);

        if (!string.Equals(entity.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("This milestone was modified by another user.");
        }

        entity.Label = request.Label.Trim();
        entity.Description = request.Description?.Trim();
        entity.CategoryKey = request.CategoryKey.Trim().ToLowerInvariant();
        entity.CategoryLabel = request.CategoryLabel.Trim();
        entity.Status = request.Status.Trim();
        entity.IsActual = request.IsActual;
        entity.CalendarDate = request.CalendarDate;
        entity.OccurredAtUtc = request.OccurredAtUtc?.ToUniversalTime();
        entity.ForecastAmountCents = request.ForecastAmountCents;
        entity.ForecastCurrency = request.ForecastAmountCents.HasValue ? MoneyCents.NormalizeAndValidateCurrency(request.ForecastCurrency ?? "AUD") : null;
        entity.ForecastDirection = request.ForecastDirection;
        entity.LinkedEntityType = request.LinkedEntityType;
        entity.LinkedEntityId = request.LinkedEntityId;

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task SoftDeleteAsync(Guid id, string concurrencyToken, Guid? leaseId, long? fencingVersion, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobDates, AccessLevel.Delete);
        var entity = await _db.JobDates.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundServiceException("Job date/milestone not found.");

        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, entity.JobId, leaseId, fencingVersion, ct);

        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("This milestone was modified by another user.");
        }

        entity.DeletedAtUtc = _timeProvider.GetUtcNow();
        entity.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    private static void ValidateJobDate(UpsertJobDateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            throw new ValidationServiceException("Milestone label is required.");
        }
        if (request.CalendarDate is null && request.OccurredAtUtc is null)
        {
            throw new ValidationServiceException("Provide either a calendar date or a date and time.");
        }
        if (request.ForecastAmountCents.HasValue)
        {
            _ = new MoneyCents(request.ForecastAmountCents.Value, request.ForecastCurrency ?? "AUD");
        }
    }

    private static JobDateDto Map(JobDateEntity d) => new()
    {
        Id = d.Id,
        JobId = d.JobId,
        Label = d.Label,
        Description = d.Description,
        CategoryKey = d.CategoryKey,
        CategoryLabel = d.CategoryLabel,
        Status = d.Status,
        IsActual = d.IsActual,
        CalendarDate = d.CalendarDate,
        OccurredAtUtc = d.OccurredAtUtc,
        ForecastAmountCents = d.ForecastAmountCents,
        ForecastCurrency = d.ForecastCurrency,
        ForecastDirection = d.ForecastDirection,
        LinkedEntityType = d.LinkedEntityType,
        LinkedEntityId = d.LinkedEntityId,
        ConcurrencyToken = d.ConcurrencyToken
    };
}
