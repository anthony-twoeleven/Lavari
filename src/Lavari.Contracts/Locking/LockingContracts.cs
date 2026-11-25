using Lavari.Contracts.Common;

namespace Lavari.Contracts.Locking;

public sealed record EditLockStatusDto
{
    public required LockableRecordType RecordType { get; init; }
    public required Guid RecordId { get; init; }
    public required bool IsLocked { get; init; }
    public required bool IsHeldByCurrentSession { get; init; }
    public required bool IsReservedForCurrentSession { get; init; }
    public required bool IsCurrentSessionQueued { get; init; }
    public int? QueuePosition { get; init; }
    public required int WaitingCount { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
    public string? HolderDisplayName { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? ReservedUntilUtc { get; init; }
    public Guid? QueueEntryId { get; init; }
}

public sealed record EditLockActionRequest
{
    public required LockableRecordType RecordType { get; init; }
    public required Guid RecordId { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
}

public sealed record InAppNotificationDto
{
    public required Guid Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public LockableRecordType? RecordType { get; init; }
    public Guid? RecordId { get; init; }
    public Guid? ReferenceId { get; init; }
    public required bool IsRead { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
