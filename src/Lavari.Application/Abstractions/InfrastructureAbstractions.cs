namespace Lavari.Application.Abstractions;

public interface IBackgroundJobScheduler
{
    Task<Guid> ScheduleOneTimeAsync<TPayload>(
        string functionName,
        TPayload payload,
        DateTimeOffset executeAtUtc,
        string? description = null,
        CancellationToken ct = default) where TPayload : notnull;

    Task<Guid> EnqueueImmediateAsync<TPayload>(
        string functionName,
        TPayload payload,
        string? description = null,
        CancellationToken ct = default) where TPayload : notnull;
}

public interface IPrivateFileStore
{
    string StorageRoot { get; }
    Task<string> SaveAsync(string category, string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default);
    Task<byte[]> ReadAsync(string storageKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default);
    Task DeleteTemporaryAsync(string storageKey, CancellationToken ct = default);
}

public interface IRealtimeNotifier
{
    Task NotifyRecordChangedAsync(string moduleKey, string recordType, Guid recordId, string eventType, CancellationToken ct = default);
    Task NotifyUserAsync(Guid userId, string category, string title, string message, string? recordType, Guid? recordId, CancellationToken ct = default);
}
