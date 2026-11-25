using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Application.Locking;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;

namespace Lavari.Infrastructure.Scheduling;

public static class ScheduledJobNames
{
    public const string SweepExpiredEditLeases = "Lavari.SweepExpiredEditLeases";
}

/// <summary>
/// Open-source TickerQ 10.4.0 (MIT/Apache-2.0) adapter behind Lavari's replaceable IBackgroundJobScheduler abstraction.
/// </summary>
public sealed class TickerQSchedulerAdapter : IBackgroundJobScheduler
{
    private readonly ITimeTickerManager<TimeTickerEntity> _timeTickerManager;
    private readonly TimeProvider _timeProvider;

    public TickerQSchedulerAdapter(
        ITimeTickerManager<TimeTickerEntity> timeTickerManager,
        TimeProvider timeProvider)
    {
        _timeTickerManager = timeTickerManager;
        _timeProvider = timeProvider;
    }

    public async Task<Guid> ScheduleOneTimeAsync<TPayload>(
        string functionName,
        TPayload payload,
        DateTimeOffset executeAtUtc,
        string? description = null,
        CancellationToken ct = default) where TPayload : notnull
    {
        var entity = new TimeTickerEntity
        {
            Id = Guid.CreateVersion7(_timeProvider.GetUtcNow()),
            Function = functionName,
            ExecutionTime = executeAtUtc.UtcDateTime,
            Request = TickerHelper.CreateTickerRequest(payload),
            Description = description ?? functionName,
            Retries = 3,
            RetryIntervals = [15, 60, 180]
        };

        var result = await _timeTickerManager.AddAsync(entity, ct).ConfigureAwait(false);
        if (!result.IsSucceeded)
        {
            throw new InvalidOperationException($"Failed to schedule TickerQ job '{functionName}': {result.Exception?.Message}");
        }

        return entity.Id;
    }

    public Task<Guid> EnqueueImmediateAsync<TPayload>(
        string functionName,
        TPayload payload,
        string? description = null,
        CancellationToken ct = default) where TPayload : notnull
        => ScheduleOneTimeAsync(functionName, payload, _timeProvider.GetUtcNow(), description, ct);
}

public sealed class ScheduledMaintenanceJobs
{
    private readonly IEditLockService _editLocks;
    private readonly ILogger<ScheduledMaintenanceJobs> _logger;

    public ScheduledMaintenanceJobs(IEditLockService editLocks, ILogger<ScheduledMaintenanceJobs> logger)
    {
        _editLocks = editLocks;
        _logger = logger;
    }

    [TickerFunction(ScheduledJobNames.SweepExpiredEditLeases, cronExpression: "0 */1 * * * *")]
    public async Task SweepExpiredEditLeasesAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        var swept = await _editLocks.SweepExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);
        if (swept > 0)
        {
            _logger.LogInformation("Scheduled lease sweep expired {Count} edit lease(s) (TickerJobId={JobId})", swept, context.Id);
        }
    }
}

public sealed class NullBackgroundJobScheduler : IBackgroundJobScheduler
{
    private readonly TimeProvider _timeProvider;
    public NullBackgroundJobScheduler(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public Task<Guid> ScheduleOneTimeAsync<TPayload>(
        string functionName,
        TPayload payload,
        DateTimeOffset executeAtUtc,
        string? description = null,
        CancellationToken ct = default) where TPayload : notnull
    {
        _ = JsonSerializer.Serialize(payload);
        return Task.FromResult(Guid.CreateVersion7(_timeProvider.GetUtcNow()));
    }

    public Task<Guid> EnqueueImmediateAsync<TPayload>(
        string functionName,
        TPayload payload,
        string? description = null,
        CancellationToken ct = default) where TPayload : notnull
        => ScheduleOneTimeAsync(functionName, payload, _timeProvider.GetUtcNow(), description, ct);
}
