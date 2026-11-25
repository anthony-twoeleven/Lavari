using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Domain.Time;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Services;

public sealed class TimeTrackingService : ITimeTrackingService
{
    private static readonly SemaphoreSlim WorkerTimerGate = new(1, 1);

    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public TimeTrackingService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<IReadOnlyList<TimeEntryDto>> ListForJobAsync(Guid jobId, bool includeDeleted = false, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.TimeTracking, includeDeleted ? AccessLevel.Delete : AccessLevel.Read);

        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        IQueryable<TimeEntryEntity> query = includeDeleted ? _db.TimeEntries.IgnoreQueryFilters() : _db.TimeEntries;
        var entries = await query.AsNoTracking()
            .Include(t => t.Intervals)
            .Where(t => t.JobId == jobId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);

        var invoicedIds = await _db.TimeBillingAllocations.AsNoTracking()
            .Where(a => a.IsActive && entries.Select(e => e.Id).Contains(a.TimeEntryId))
            .Select(a => a.TimeEntryId)
            .ToHashSetAsync(ct);

        var now = _timeProvider.GetUtcNow();
        return entries.Select(e => Map(e, job.Name, job.TimeZoneIana, invoicedIds.Contains(e.Id), now)).ToArray();
    }

    public async Task<IReadOnlyList<TimeEntryDto>> ListActiveTimersForCurrentUserAsync(CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Read);
        var workerId = _actor.ActorId ?? throw new UnauthorizedServiceException();

        var entries = await _db.TimeEntries.AsNoTracking()
            .Include(t => t.Job)
            .Include(t => t.Intervals)
            .Where(t => t.WorkerUserId == workerId && (t.State == "Running" || t.State == "Paused"))
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);

        var now = _timeProvider.GetUtcNow();
        return entries.Select(e => Map(e, e.Job.Name, e.Job.TimeZoneIana, false, now)).ToArray();
    }

    public async Task<StartTimerResultDto> StartTimerAsync(StartTimerRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);

        var workerId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == request.JobId, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var existingIdem = await _db.TimeEntries
                    .Include(t => t.Intervals)
                    .FirstOrDefaultAsync(t => t.WorkerUserId == workerId && t.IdempotencyKey == request.IdempotencyKey, ct);
                if (existingIdem is not null)
                {
                    return new StartTimerResultDto
                    {
                        RequiresMultiTimerChoice = false,
                        StartedEntry = Map(existingIdem, job.Name, job.TimeZoneIana, false, _timeProvider.GetUtcNow())
                    };
                }
            }

            var now = _timeProvider.GetUtcNow();
            var runningOnOtherJobs = await _db.TimeEntries
                .Include(t => t.Job)
                .Include(t => t.Intervals)
                .Where(t => t.WorkerUserId == workerId && t.State == "Running" && t.JobId != request.JobId)
                .ToListAsync(ct);

            var resolution = request.ConflictResolution.Trim();
            if (runningOnOtherJobs.Count > 0 && string.Equals(resolution, "None", StringComparison.OrdinalIgnoreCase))
            {
                return new StartTimerResultDto
                {
                    RequiresMultiTimerChoice = true,
                    RunningTimersOnOtherJobs = runningOnOtherJobs.Select(t => new RunningTimerSummaryDto
                    {
                        TimeEntryId = t.Id,
                        JobId = t.JobId,
                        JobName = t.Job.Name,
                        LiveDurationSeconds = ComputeLiveSeconds(t, now),
                        ConcurrencyToken = t.ConcurrencyToken
                    }).ToArray()
                };
            }

            var stoppedIds = new List<Guid>();
            var hasIntentionalOverlap = false;
            string? overlapNote = null;

            if (runningOnOtherJobs.Count > 0)
            {
                // Verify the caller's choice is bound to the exact observed running set & versions.
                if (request.ObservedRunningTimers is null || request.ObservedRunningTimers.Count != runningOnOtherJobs.Count)
                {
                    throw new ConflictServiceException(
                        "Your running timers changed while you were choosing. Please review and try again.",
                        ErrorCodes.RunningTimersChoiceRequired);
                }

                var observedMap = request.ObservedRunningTimers.ToDictionary(x => x.TimeEntryId, x => x.ConcurrencyToken);
                foreach (var running in runningOnOtherJobs)
                {
                    if (!observedMap.TryGetValue(running.Id, out var token) || !string.Equals(token, running.ConcurrencyToken, StringComparison.Ordinal))
                    {
                        throw new ConflictServiceException(
                            "One or more running timers changed before your choice was applied. Review and retry.",
                            ErrorCodes.RunningTimersChoiceRequired);
                    }
                }

                if (string.Equals(resolution, "StopExisting", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var running in runningOnOtherJobs)
                    {
                        CloseRunningInterval(running, now, now);
                        running.State = "Stopped";
                        running.DurationSeconds = ComputeClosedDurationSeconds(running);
                        running.OriginalRecordedSeconds = running.DurationSeconds;
                        stoppedIds.Add(running.Id);
                    }
                }
                else if (string.Equals(resolution, "KeepRunning", StringComparison.OrdinalIgnoreCase))
                {
                    hasIntentionalOverlap = true;
                    overlapNote = "Explicitly started alongside existing running timer(s). Review before invoicing.";
                    foreach (var running in runningOnOtherJobs)
                    {
                        running.HasIntentionalOverlap = true;
                        running.OverlapNote ??= "Concurrent timer explicitly approved.";
                    }
                }
                else
                {
                    throw new ValidationServiceException("Invalid multi-timer conflict resolution choice.");
                }
            }

            var jobZone = JobTimeZone.Create(job.TimeZoneIana);
            var localStart = jobZone.ConvertUtcToZone(now);

            var entry = new TimeEntryEntity
            {
                Id = _ids.NewId(),
                JobId = job.Id,
                WorkerUserId = workerId,
                WorkerDisplayName = _actor.ActorDisplayName,
                Description = request.Description?.Trim(),
                SourceType = "Timer",
                State = "Running",
                IsBillable = request.IsBillable,
                WorkDate = DateOnly.FromDateTime(localStart.Date),
                DurationSeconds = 0,
                OriginalRecordedSeconds = 0,
                HasIntentionalOverlap = hasIntentionalOverlap,
                OverlapNote = overlapNote,
                IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim()
            };
            entry.Intervals.Add(new TimeIntervalEntity
            {
                Id = _ids.NewId(),
                TimeEntryId = entry.Id,
                StartedAtUtc = now,
                EffectiveStartedAtUtc = now,
                OperationRecordedAtUtc = now,
                Sequence = 1
            });

            _db.TimeEntries.Add(entry);
            await _db.SaveChangesAsync(ct);

            return new StartTimerResultDto
            {
                RequiresMultiTimerChoice = false,
                StartedEntry = Map(entry, job.Name, job.TimeZoneIana, false, now),
                StoppedTimerIds = stoppedIds
            };
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimeEntryDto> PauseTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, concurrencyToken, ct);
            if (entry.State != "Running")
            {
                throw new ValidationServiceException("Only a running timer can be paused.", ErrorCodes.TimerStateInvalid);
            }

            var now = _timeProvider.GetUtcNow();
            CloseRunningInterval(entry, now, now);
            entry.State = "Paused";
            entry.DurationSeconds = ComputeClosedDurationSeconds(entry);
            entry.OriginalRecordedSeconds = entry.DurationSeconds;

            await _db.SaveChangesAsync(ct);
            return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, false, now);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimeEntryDto> ResumeTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, concurrencyToken, ct);
            if (entry.State != "Paused")
            {
                throw new ValidationServiceException("Only a paused timer can be resumed.", ErrorCodes.TimerStateInvalid);
            }

            var now = _timeProvider.GetUtcNow();
            var nextSeq = entry.Intervals.Count == 0 ? 1 : entry.Intervals.Max(i => i.Sequence) + 1;
            _db.TimeIntervals.Add(new TimeIntervalEntity
            {
                Id = _ids.NewId(),
                TimeEntryId = entry.Id,
                StartedAtUtc = now,
                EffectiveStartedAtUtc = now,
                OperationRecordedAtUtc = now,
                Sequence = nextSeq
            });
            entry.State = "Running";

            await _db.SaveChangesAsync(ct);
            return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, false, now);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimeEntryDto> StopTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, concurrencyToken, ct);
            if (entry.State is not ("Running" or "Paused"))
            {
                throw new ValidationServiceException("Only a running or paused timer can be stopped.", ErrorCodes.TimerStateInvalid);
            }

            var now = _timeProvider.GetUtcNow();
            if (entry.State == "Running")
            {
                CloseRunningInterval(entry, now, now);
            }
            // Normal stop of a paused timer adds no time since its pause!
            entry.State = "Stopped";
            entry.DurationSeconds = ComputeClosedDurationSeconds(entry);
            entry.OriginalRecordedSeconds = entry.DurationSeconds;

            await _db.SaveChangesAsync(ct);
            return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, false, now);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimeEntryDto> CancelTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, concurrencyToken, ct);
            if (entry.State is not ("Running" or "Paused"))
            {
                throw new ValidationServiceException("Only a running or paused timer can be cancelled.", ErrorCodes.TimerStateInvalid);
            }

            var now = _timeProvider.GetUtcNow();
            if (entry.State == "Running")
            {
                CloseRunningInterval(entry, now, now);
            }
            entry.State = "Cancelled";
            entry.IsBillable = false;
            entry.DurationSeconds = 0;

            await _db.SaveChangesAsync(ct);
            return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, false, now);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimerAdjustmentPreviewDto> PreviewStopAtAsync(Guid timeEntryId, DateTimeOffset effectiveTimeUtc, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        var entry = await _db.TimeEntries.AsNoTracking().Include(t => t.Intervals).FirstOrDefaultAsync(t => t.Id == timeEntryId, ct)
            ?? throw new NotFoundServiceException("Time entry not found.");

        var resultingSeconds = ValidateStopAtAndCalculateSeconds(entry, effectiveTimeUtc.ToUniversalTime(), _timeProvider.GetUtcNow());
        return new TimerAdjustmentPreviewDto
        {
            TimeEntryId = entry.Id,
            Action = "StopAt",
            EffectiveTimeUtc = effectiveTimeUtc.ToUniversalTime(),
            ResultingDurationSeconds = resultingSeconds,
            HasOverlapWarning = false,
            ConcurrencyToken = entry.ConcurrencyToken
        };
    }

    public async Task<TimeEntryDto> StopAtAsync(Guid timeEntryId, TimerAtTimeRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        if (!request.Confirmed)
        {
            throw new ValidationServiceException("Preview and confirm the Stop At time before saving.");
        }

        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, request.ConcurrencyToken, ct);
            var now = _timeProvider.GetUtcNow();
            var eff = request.EffectiveTimeUtc.ToUniversalTime();
            var resultingSeconds = ValidateStopAtAndCalculateSeconds(entry, eff, now);

            CloseRunningInterval(entry, eff, now);
            entry.State = "Stopped";
            entry.DurationSeconds = resultingSeconds;
            entry.OriginalRecordedSeconds = resultingSeconds;

            await _db.SaveChangesAsync(ct);
            return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, false, now);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimerAdjustmentPreviewDto> PreviewResumeAtAsync(Guid timeEntryId, DateTimeOffset effectiveTimeUtc, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        var entry = await _db.TimeEntries.AsNoTracking().Include(t => t.Intervals).FirstOrDefaultAsync(t => t.Id == timeEntryId, ct)
            ?? throw new NotFoundServiceException("Time entry not found.");

        var now = _timeProvider.GetUtcNow();
        var eff = effectiveTimeUtc.ToUniversalTime();
        var resultingSeconds = ValidateResumeAtAndCalculateSeconds(entry, eff, now);
        var hasOverlap = await HasWorkerOverlapAsync(entry.WorkerUserId, eff, now, entry.Id, ct);

        return new TimerAdjustmentPreviewDto
        {
            TimeEntryId = entry.Id,
            Action = "ResumeAt",
            EffectiveTimeUtc = eff,
            ResultingDurationSeconds = resultingSeconds,
            HasOverlapWarning = hasOverlap,
            OverlapWarningMessage = hasOverlap ? "Resuming at this time overlaps another interval for this worker." : null,
            ConcurrencyToken = entry.ConcurrencyToken
        };
    }

    public async Task<TimeEntryDto> ResumeAtAsync(Guid timeEntryId, TimerAtTimeRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        if (!request.Confirmed)
        {
            throw new ValidationServiceException("Preview and confirm the Resume At time before saving.");
        }

        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, request.ConcurrencyToken, ct);
            var now = _timeProvider.GetUtcNow();
            var eff = request.EffectiveTimeUtc.ToUniversalTime();
            _ = ValidateResumeAtAndCalculateSeconds(entry, eff, now);

            var hasOverlap = await HasWorkerOverlapAsync(entry.WorkerUserId, eff, now, entry.Id, ct);
            if (hasOverlap && !request.ConfirmOverlap)
            {
                throw new ConflictServiceException(
                    "Resuming at this time overlaps another worked interval. Confirm intentional overlap or choose another time.",
                    ErrorCodes.ValidationFailed);
            }

            if (hasOverlap)
            {
                entry.HasIntentionalOverlap = true;
                entry.OverlapNote = "Intentional overlap confirmed during Resume At.";
            }

            var nextSeq = entry.Intervals.Count == 0 ? 1 : entry.Intervals.Max(i => i.Sequence) + 1;
            _db.TimeIntervals.Add(new TimeIntervalEntity
            {
                Id = _ids.NewId(),
                TimeEntryId = entry.Id,
                StartedAtUtc = eff,
                EffectiveStartedAtUtc = eff,
                OperationRecordedAtUtc = now,
                Sequence = nextSeq
            });
            entry.State = "Running";

            await _db.SaveChangesAsync(ct);
            return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, false, now);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task<TimeEntryDto> CreateFlatEntryAsync(CreateFlatTimeEntryRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);

        if (request.DurationSeconds <= 0)
        {
            throw new ValidationServiceException("Duration must be greater than zero.");
        }

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == request.JobId, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        var workerId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var entry = new TimeEntryEntity
        {
            Id = _ids.NewId(),
            JobId = job.Id,
            WorkerUserId = workerId,
            WorkerDisplayName = _actor.ActorDisplayName,
            Description = request.Description?.Trim(),
            SourceType = "FlatDuration",
            State = "Stopped",
            IsBillable = request.IsBillable,
            WorkDate = request.WorkDate,
            DurationSeconds = request.DurationSeconds,
            OriginalRecordedSeconds = request.DurationSeconds
        };
        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return Map(entry, job.Name, job.TimeZoneIana, false, _timeProvider.GetUtcNow());
    }

    public async Task<TimeEntryDto> CreateRangeEntryAsync(CreateRangeTimeEntryRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);

        var startUtc = request.StartedAtUtc.ToUniversalTime();
        var endUtc = request.EndedAtUtc.ToUniversalTime();
        if (endUtc <= startUtc)
        {
            throw new ValidationServiceException("End time must be after start time.");
        }

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == request.JobId, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        var workerId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var hasOverlap = await HasWorkerOverlapAsync(workerId, startUtc, endUtc, null, ct);
        if (hasOverlap && !request.ConfirmOverlap)
        {
            throw new ConflictServiceException(
                "This time range overlaps an existing time entry for this worker. Correct the range or explicitly confirm intentional overlap.",
                ErrorCodes.ValidationFailed);
        }

        var seconds = (long)Math.Round((endUtc - startUtc).TotalSeconds);
        var jobZone = JobTimeZone.Create(job.TimeZoneIana);
        var workDate = DateOnly.FromDateTime(jobZone.ConvertUtcToZone(startUtc).Date);
        var now = _timeProvider.GetUtcNow();

        var entry = new TimeEntryEntity
        {
            Id = _ids.NewId(),
            JobId = job.Id,
            WorkerUserId = workerId,
            WorkerDisplayName = _actor.ActorDisplayName,
            Description = request.Description?.Trim(),
            SourceType = "TimeRange",
            State = "Stopped",
            IsBillable = request.IsBillable,
            WorkDate = workDate,
            DurationSeconds = seconds,
            OriginalRecordedSeconds = seconds,
            HasIntentionalOverlap = hasOverlap,
            OverlapNote = hasOverlap ? "Intentional overlap confirmed on manual range entry." : null
        };
        entry.Intervals.Add(new TimeIntervalEntity
        {
            Id = _ids.NewId(),
            TimeEntryId = entry.Id,
            StartedAtUtc = startUtc,
            EndedAtUtc = endUtc,
            EffectiveStartedAtUtc = startUtc,
            EffectiveEndedAtUtc = endUtc,
            OperationRecordedAtUtc = now,
            Sequence = 1
        });
        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return Map(entry, job.Name, job.TimeZoneIana, false, now);
    }

    public async Task<TimeEntryDto> UpdateEntryAsync(Guid timeEntryId, UpdateTimeEntryRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Alter);
        var entry = await LoadTimerForMutationAsync(timeEntryId, request.ConcurrencyToken, ct);

        entry.Description = request.Description?.Trim();
        entry.IsBillable = request.IsBillable;
        if (request.DurationSeconds.HasValue)
        {
            if (request.DurationSeconds.Value <= 0)
            {
                throw new ValidationServiceException("Duration must be greater than zero.");
            }
            entry.DurationSeconds = request.DurationSeconds.Value;
        }
        if (request.WorkDate.HasValue)
        {
            entry.WorkDate = request.WorkDate.Value;
        }

        await _db.SaveChangesAsync(ct);
        var isInvoiced = await _db.TimeBillingAllocations.AnyAsync(a => a.TimeEntryId == timeEntryId && a.IsActive, ct);
        return Map(entry, entry.Job.Name, entry.Job.TimeZoneIana, isInvoiced, _timeProvider.GetUtcNow());
    }

    public async Task SoftDeleteAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Delete);
        await WorkerTimerGate.WaitAsync(ct);
        try
        {
            var entry = await LoadTimerForMutationAsync(timeEntryId, concurrencyToken, ct);
            var now = _timeProvider.GetUtcNow();

            // Spec: Deleting a running or paused timer atomically cancels tracking and soft-deletes it; restoring does not restart it.
            if (entry.State is "Running" or "Paused")
            {
                if (entry.State == "Running")
                {
                    CloseRunningInterval(entry, now, now);
                }
                entry.State = "Cancelled";
                entry.IsBillable = false;
                entry.DurationSeconds = 0;
            }

            entry.DeletedAtUtc = now;
            entry.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            WorkerTimerGate.Release();
        }
    }

    public async Task RestoreAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.TimeTracking, AccessLevel.Delete);
        var entry = await _db.TimeEntries.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == timeEntryId, ct)
            ?? throw new NotFoundServiceException("Time entry not found.");

        if (!string.Equals(entry.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Time entry was modified by another user.");
        }

        entry.DeletedAtUtc = null;
        entry.DeletedBy = null;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TimeEntryEntity> LoadTimerForMutationAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct)
    {
        var entry = await _db.TimeEntries
            .Include(t => t.Job)
            .Include(t => t.Intervals)
            .FirstOrDefaultAsync(t => t.Id == timeEntryId, ct)
            ?? throw new NotFoundServiceException("Time entry not found.");

        if (!string.Equals(entry.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("This time entry was modified by another request. Reload and try again.");
        }

        return entry;
    }

    private static long ValidateStopAtAndCalculateSeconds(TimeEntryEntity entry, DateTimeOffset effectiveEndUtc, DateTimeOffset nowUtc)
    {
        if (entry.State != "Running")
        {
            throw new ValidationServiceException("Stop At is only available for a running timer.", ErrorCodes.TimerStateInvalid);
        }

        if (effectiveEndUtc > nowUtc)
        {
            throw new ValidationServiceException("Stop At time cannot be in the future.", ErrorCodes.TimerStateInvalid);
        }

        var openInterval = entry.Intervals.OrderBy(i => i.Sequence).LastOrDefault(i => i.EndedAtUtc is null)
            ?? throw new ValidationServiceException("No open running interval was found.", ErrorCodes.TimerStateInvalid);

        var start = openInterval.EffectiveStartedAtUtc ?? openInterval.StartedAtUtc;
        if (effectiveEndUtc <= start)
        {
            throw new ValidationServiceException("Stop At time must be after the current interval started.", ErrorCodes.TimerStateInvalid);
        }

        long priorClosed = 0;
        foreach (var iv in entry.Intervals.Where(i => i.Id != openInterval.Id && i.EndedAtUtc.HasValue))
        {
            var s = iv.EffectiveStartedAtUtc ?? iv.StartedAtUtc;
            var e = iv.EffectiveEndedAtUtc ?? iv.EndedAtUtc!.Value;
            priorClosed += Math.Max(0, (long)Math.Round((e - s).TotalSeconds));
        }

        var currentSeconds = (long)Math.Round((effectiveEndUtc - start).TotalSeconds);
        return priorClosed + Math.Max(1, currentSeconds);
    }

    private static long ValidateResumeAtAndCalculateSeconds(TimeEntryEntity entry, DateTimeOffset effectiveStartUtc, DateTimeOffset nowUtc)
    {
        if (entry.State != "Paused")
        {
            throw new ValidationServiceException("Resume At is only available for a paused timer.", ErrorCodes.TimerStateInvalid);
        }

        if (effectiveStartUtc > nowUtc)
        {
            throw new ValidationServiceException("Resume At time cannot be in the future.", ErrorCodes.TimerStateInvalid);
        }

        var lastClosed = entry.Intervals.OrderBy(i => i.Sequence).LastOrDefault(i => i.EndedAtUtc.HasValue);
        if (lastClosed is not null)
        {
            var lastEnd = lastClosed.EffectiveEndedAtUtc ?? lastClosed.EndedAtUtc!.Value;
            if (effectiveStartUtc < lastEnd)
            {
                throw new ValidationServiceException(
                    "Resume At time cannot be earlier than when the timer was paused.",
                    ErrorCodes.TimerStateInvalid);
            }
        }

        var closed = ComputeClosedDurationSeconds(entry);
        var resumedSoFar = Math.Max(0, (long)Math.Round((nowUtc - effectiveStartUtc).TotalSeconds));
        return closed + resumedSoFar;
    }

    private static void CloseRunningInterval(TimeEntryEntity entry, DateTimeOffset effectiveEndUtc, DateTimeOffset operationTimeUtc)
    {
        var open = entry.Intervals.OrderBy(i => i.Sequence).LastOrDefault(i => i.EndedAtUtc is null);
        if (open is not null)
        {
            open.EndedAtUtc = effectiveEndUtc;
            open.EffectiveEndedAtUtc = effectiveEndUtc;
            open.OperationRecordedAtUtc = operationTimeUtc;
        }
    }

    private static long ComputeClosedDurationSeconds(TimeEntryEntity entry)
    {
        long total = 0;
        foreach (var iv in entry.Intervals.Where(i => i.EndedAtUtc.HasValue))
        {
            var s = iv.EffectiveStartedAtUtc ?? iv.StartedAtUtc;
            var e = iv.EffectiveEndedAtUtc ?? iv.EndedAtUtc!.Value;
            if (e > s)
            {
                total += (long)Math.Round((e - s).TotalSeconds);
            }
        }
        return total;
    }

    private static long ComputeLiveSeconds(TimeEntryEntity entry, DateTimeOffset now)
    {
        if (entry.SourceType == "FlatDuration")
        {
            return entry.DurationSeconds;
        }

        if (entry.State == "Cancelled")
        {
            return 0;
        }

        var closed = ComputeClosedDurationSeconds(entry);
        var open = entry.Intervals.LastOrDefault(i => i.EndedAtUtc is null);
        if (entry.State == "Running" && open is not null)
        {
            var s = open.EffectiveStartedAtUtc ?? open.StartedAtUtc;
            if (now > s)
            {
                closed += (long)Math.Round((now - s).TotalSeconds);
            }
        }
        return closed > 0 ? closed : entry.DurationSeconds;
    }

    private async Task<bool> HasWorkerOverlapAsync(Guid workerId, DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? excludeEntryId, CancellationToken ct)
    {
        var intervals = await _db.TimeIntervals.AsNoTracking()
            .Include(i => i.TimeEntry)
            .Where(i => i.TimeEntry.WorkerUserId == workerId &&
                        i.TimeEntry.State != "Cancelled" &&
                        (!excludeEntryId.HasValue || i.TimeEntryId != excludeEntryId.Value))
            .ToListAsync(ct);

        var now = _timeProvider.GetUtcNow();
        foreach (var iv in intervals)
        {
            var s = iv.EffectiveStartedAtUtc ?? iv.StartedAtUtc;
            var e = iv.EffectiveEndedAtUtc ?? iv.EndedAtUtc ?? now;
            if (s < endUtc && startUtc < e)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeEntryDto Map(TimeEntryEntity e, string jobName, string jobTz, bool isInvoiced, DateTimeOffset now) => new()
    {
        Id = e.Id,
        JobId = e.JobId,
        JobName = jobName,
        JobTimeZoneIana = jobTz,
        WorkerUserId = e.WorkerUserId,
        WorkerDisplayName = e.WorkerDisplayName,
        Description = e.Description,
        SourceType = e.SourceType,
        State = e.State,
        IsBillable = e.IsBillable,
        WorkDate = e.WorkDate,
        DurationSeconds = e.DurationSeconds,
        LiveDurationSeconds = ComputeLiveSeconds(e, now),
        HasIntentionalOverlap = e.HasIntentionalOverlap,
        OverlapNote = e.OverlapNote,
        IsInvoiced = isInvoiced,
        IsDeleted = e.IsDeleted,
        ConcurrencyToken = e.ConcurrencyToken,
        Intervals = e.Intervals.OrderBy(i => i.Sequence).Select(i => new TimeIntervalDto
        {
            Id = i.Id,
            StartedAtUtc = i.StartedAtUtc,
            EndedAtUtc = i.EndedAtUtc,
            EffectiveStartedAtUtc = i.EffectiveStartedAtUtc,
            EffectiveEndedAtUtc = i.EffectiveEndedAtUtc,
            Sequence = i.Sequence
        }).ToArray()
    };
}
