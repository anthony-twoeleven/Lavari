using Lavari.Contracts.Common;

namespace Lavari.Infrastructure.Persistence.Entities;

public sealed class JobDateEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CategoryKey { get; set; } = "milestone";
    public string CategoryLabel { get; set; } = "Milestone";
    public string Status { get; set; } = "Planned"; // Planned, Completed, Delayed, Cancelled
    public bool IsActual { get; set; }
    public DateOnly? CalendarDate { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public long? ForecastAmountCents { get; set; }
    public string? ForecastCurrency { get; set; }
    public string ForecastDirection { get; set; } = "None"; // None, Cost, Revenue
    public JobDateLinkedEntityType? LinkedEntityType { get; set; }
    public Guid? LinkedEntityId { get; set; }
}

public sealed class TimeEntryEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public Guid WorkerUserId { get; set; }
    public string WorkerDisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TimeEntrySourceType SourceType { get; set; } = TimeEntrySourceType.Timer;
    public string State { get; set; } = "Running"; // Running, Paused, Stopped, Cancelled
    public bool IsBillable { get; set; } = true;
    public DateOnly WorkDate { get; set; }
    public long DurationSeconds { get; set; }
    public long OriginalRecordedSeconds { get; set; }
    public bool HasIntentionalOverlap { get; set; }
    public string? OverlapNote { get; set; }
    public string? IdempotencyKey { get; set; }
    public List<TimeIntervalEntity> Intervals { get; set; } = [];
}

public sealed class TimeIntervalEntity : AuditableSoftDeletableEntity
{
    public Guid TimeEntryId { get; set; }
    public TimeEntryEntity TimeEntry { get; set; } = null!;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset? EffectiveStartedAtUtc { get; set; }
    public DateTimeOffset? EffectiveEndedAtUtc { get; set; }
    public DateTimeOffset OperationRecordedAtUtc { get; set; }
    public int Sequence { get; set; }
}
