namespace Lavari.Infrastructure.Persistence.Entities;

public sealed class ClientEntity : AuditableSoftDeletableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? BillingAddress { get; set; }
    public string? Notes { get; set; }
    public List<JobGroupEntity> JobGroups { get; set; } = [];
}

public sealed class JobGroupEntity : AuditableSoftDeletableEntity
{
    public Guid ClientId { get; set; }
    public ClientEntity Client { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<JobEntity> Jobs { get; set; } = [];
}

public sealed class JobEntity : AuditableSoftDeletableEntity
{
    public Guid ClientId { get; set; }
    public ClientEntity Client { get; set; } = null!;
    public Guid JobGroupId { get; set; }
    public JobGroupEntity JobGroup { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "Active";
    public string TimeZoneIana { get; set; } = "Australia/Sydney";
    public string DefaultCurrency { get; set; } = "AUD";
    public string? PoNumber { get; set; }
    public long DefaultHourlyRateCents { get; set; }
    public long? InternalHourlyCostCents { get; set; }
    public string? Notes { get; set; }
}

public sealed class EditLeaseEntity : AuditableSoftDeletableEntity
{
    public string RecordType { get; set; } = string.Empty; // Job, Quote, Invoice
    public Guid RecordId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorDisplayName { get; set; } = string.Empty;
    public string EditingSessionId { get; set; } = string.Empty;
    public string State { get; set; } = "Active"; // Active, Reserved, Released, Expired, Cancelled
    public long FencingVersion { get; set; }
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public DateTimeOffset LastRenewedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid? QueueEntryId { get; set; }
}

public sealed class EditQueueEntryEntity : AuditableSoftDeletableEntity
{
    public string RecordType { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorDisplayName { get; set; } = string.Empty;
    public string EditingSessionId { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public string State { get; set; } = "Queued"; // Queued, Reserved, Claimed, Cancelled, Expired, Denied
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? ReservedUntilUtc { get; set; }
}

public sealed class InAppNotificationEntity : AuditableSoftDeletableEntity
{
    public Guid RecipientUserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RecordType { get; set; }
    public Guid? RecordId { get; set; }
    public Guid? ReferenceId { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
