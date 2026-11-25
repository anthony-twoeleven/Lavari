namespace Lavari.Infrastructure.Persistence.Entities;

/// <summary>
/// Marks properties (such as password hashes or secrets) that must never appear in audit history snapshots.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcludeFromAuditAttribute : Attribute;

/// <summary>
/// Marker for append-only tables whose inserts are their own immutable history and must not be recursively audited.
/// </summary>
public interface IAppendOnlyEntity;

/// <summary>
/// Persistence base entity for mutable, soft-deletable application records.
/// Note: EF Core persistence entities are mutable for ORM change tracking and are isolated strictly within Infrastructure.
/// </summary>
public abstract class AuditableSoftDeletableEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsDeleted => DeletedAtUtc.HasValue;
}

public sealed class AuditEntryEntity : IAppendOnlyEntity
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string ActorDisplayName { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}

public sealed class OperationalErrorOccurrenceEntity : IAppendOnlyEntity
{
    public Guid Id { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public string? RequestPath { get; set; }
    public string SanitizedTechnicalDetail { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}
