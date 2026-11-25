using Lavari.Contracts.Locking;

namespace Lavari.Application.Locking;

public interface IEditLockService
{
    Task<EditLockStatusDto> GetStatusAsync(string recordType, Guid recordId, CancellationToken ct = default);
    Task<EditLockStatusDto> RequestModifyAsync(string recordType, Guid recordId, CancellationToken ct = default);
    Task<EditLockStatusDto> ClaimReservationAsync(string recordType, Guid recordId, CancellationToken ct = default);
    Task<EditLockStatusDto> RenewLeaseAsync(string recordType, Guid recordId, Guid leaseId, long fencingVersion, CancellationToken ct = default);
    Task<EditLockStatusDto> ReleaseLeaseAsync(string recordType, Guid recordId, Guid? leaseId = null, long? fencingVersion = null, CancellationToken ct = default);
    Task<EditLockStatusDto> CancelQueueEntryAsync(string recordType, Guid recordId, CancellationToken ct = default);
    Task EnsureActiveLeaseHeldAsync(string recordType, Guid recordId, Guid? leaseId, long? fencingVersion, CancellationToken ct = default);
    Task<int> SweepExpiredLeasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<InAppNotificationDto>> GetNotificationsAsync(bool unreadOnly = false, CancellationToken ct = default);
    Task MarkNotificationReadAsync(Guid notificationId, CancellationToken ct = default);
}
