using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;

namespace Lavari.Application.Locking;

public interface IEditLockService
{
    Task<EditLockStatusDto> GetStatusAsync(LockableRecordType recordType, Guid recordId, CancellationToken ct = default);
    Task<EditLockStatusDto> RequestModifyAsync(LockableRecordType recordType, Guid recordId, CancellationToken ct = default);
    Task<EditLockStatusDto> ClaimReservationAsync(LockableRecordType recordType, Guid recordId, CancellationToken ct = default);
    Task<EditLockStatusDto> RenewLeaseAsync(LockableRecordType recordType, Guid recordId, Guid leaseId, long fencingVersion, CancellationToken ct = default);
    Task<EditLockStatusDto> ReleaseLeaseAsync(LockableRecordType recordType, Guid recordId, Guid? leaseId = null, long? fencingVersion = null, CancellationToken ct = default);
    Task<EditLockStatusDto> CancelQueueEntryAsync(LockableRecordType recordType, Guid recordId, CancellationToken ct = default);
    Task EnsureActiveLeaseHeldAsync(LockableRecordType recordType, Guid recordId, Guid? leaseId, long? fencingVersion, CancellationToken ct = default);
    Task<int> SweepExpiredLeasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<InAppNotificationDto>> GetNotificationsAsync(bool unreadOnly = false, CancellationToken ct = default);
    Task MarkNotificationReadAsync(Guid notificationId, CancellationToken ct = default);
}
