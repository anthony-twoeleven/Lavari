namespace Lavari.Contracts.Business;

public sealed record TimeIntervalDto
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public DateTimeOffset? EffectiveStartedAtUtc { get; init; }
    public DateTimeOffset? EffectiveEndedAtUtc { get; init; }
    public required int Sequence { get; init; }
}

public sealed record TimeEntryDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string JobName { get; init; }
    public required string JobTimeZoneIana { get; init; }
    public required Guid WorkerUserId { get; init; }
    public required string WorkerDisplayName { get; init; }
    public string? Description { get; init; }
    public required string SourceType { get; init; }
    public required string State { get; init; }
    public required bool IsBillable { get; init; }
    public required DateOnly WorkDate { get; init; }
    public required long DurationSeconds { get; init; }
    public required long LiveDurationSeconds { get; init; }
    public required bool HasIntentionalOverlap { get; init; }
    public string? OverlapNote { get; init; }
    public required bool IsInvoiced { get; init; }
    public required bool IsDeleted { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<TimeIntervalDto> Intervals { get; init; }
}

public sealed record ObservedRunningTimerRef
{
    public required Guid TimeEntryId { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record RunningTimerSummaryDto
{
    public required Guid TimeEntryId { get; init; }
    public required Guid JobId { get; init; }
    public required string JobName { get; init; }
    public required long LiveDurationSeconds { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record StartTimerRequest
{
    public required Guid JobId { get; init; }
    public string? Description { get; init; }
    public bool IsBillable { get; init; } = true;
    public string? IdempotencyKey { get; init; }
    /// <summary>
    /// None (initial check), StopExisting, or KeepRunning.
    /// </summary>
    public string ConflictResolution { get; init; } = "None";
    public IReadOnlyList<ObservedRunningTimerRef>? ObservedRunningTimers { get; init; }
}

public sealed record StartTimerResultDto
{
    public required bool RequiresMultiTimerChoice { get; init; }
    public TimeEntryDto? StartedEntry { get; init; }
    public IReadOnlyList<RunningTimerSummaryDto> RunningTimersOnOtherJobs { get; init; } = [];
    public IReadOnlyList<Guid> StoppedTimerIds { get; init; } = [];
}

public sealed record TimerAtTimeRequest
{
    public required DateTimeOffset EffectiveTimeUtc { get; init; }
    public required string ConcurrencyToken { get; init; }
    public bool Confirmed { get; init; }
    public bool ConfirmOverlap { get; init; }
}

public sealed record TimerAdjustmentPreviewDto
{
    public required Guid TimeEntryId { get; init; }
    public required string Action { get; init; }
    public required DateTimeOffset EffectiveTimeUtc { get; init; }
    public required long ResultingDurationSeconds { get; init; }
    public required bool HasOverlapWarning { get; init; }
    public string? OverlapWarningMessage { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record CreateFlatTimeEntryRequest
{
    public required Guid JobId { get; init; }
    public required DateOnly WorkDate { get; init; }
    public required long DurationSeconds { get; init; }
    public string? Description { get; init; }
    public bool IsBillable { get; init; } = true;
}

public sealed record CreateRangeTimeEntryRequest
{
    public required Guid JobId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public string? Description { get; init; }
    public bool IsBillable { get; init; } = true;
    public bool ConfirmOverlap { get; init; }
}

public sealed record UpdateTimeEntryRequest
{
    public string? Description { get; init; }
    public bool IsBillable { get; init; } = true;
    public long? DurationSeconds { get; init; }
    public DateOnly? WorkDate { get; init; }
    public required string ConcurrencyToken { get; init; }
}
