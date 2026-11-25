namespace Lavari.Contracts.Business;

public sealed record ClientDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? ContactName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? BillingAddress { get; init; }
    public string? Notes { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required bool IsDeleted { get; init; }
    public required int ActiveGroupCount { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record UpsertClientRequest
{
    public required string Name { get; init; }
    public string? ContactName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? BillingAddress { get; init; }
    public string? Notes { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record JobGroupDto
{
    public required Guid Id { get; init; }
    public required Guid ClientId { get; init; }
    public required string ClientName { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required int ActiveJobCount { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required bool IsDeleted { get; init; }
}

public sealed record UpsertJobGroupRequest
{
    public required Guid ClientId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record JobDto
{
    public required Guid Id { get; init; }
    public required Guid ClientId { get; init; }
    public required string ClientName { get; init; }
    public required Guid JobGroupId { get; init; }
    public required string JobGroupName { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required string TimeZoneIana { get; init; }
    public required string DefaultCurrency { get; init; }
    public string? PoNumber { get; init; }
    public required long DefaultHourlyRateCents { get; init; }
    public long? InternalHourlyCostCents { get; init; }
    public string? Notes { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required bool IsDeleted { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record UpsertJobRequest
{
    public required Guid JobGroupId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required string TimeZoneIana { get; init; }
    public required string DefaultCurrency { get; init; }
    public string? PoNumber { get; init; }
    public long DefaultHourlyRateCents { get; init; }
    public long? InternalHourlyCostCents { get; init; }
    public string? Notes { get; init; }
    public string? ConcurrencyToken { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
}

public sealed record JobDateDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required string CategoryKey { get; init; }
    public required string CategoryLabel { get; init; }
    public required string Status { get; init; }
    public required bool IsActual { get; init; }
    public DateOnly? CalendarDate { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
    public long? ForecastAmountCents { get; init; }
    public string? ForecastCurrency { get; init; }
    public required string ForecastDirection { get; init; }
    public string? LinkedEntityType { get; init; }
    public Guid? LinkedEntityId { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record UpsertJobDateRequest
{
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required string CategoryKey { get; init; }
    public required string CategoryLabel { get; init; }
    public required string Status { get; init; }
    public bool IsActual { get; init; }
    public DateOnly? CalendarDate { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
    public long? ForecastAmountCents { get; init; }
    public string? ForecastCurrency { get; init; }
    public string ForecastDirection { get; init; } = "None";
    public string? LinkedEntityType { get; init; }
    public Guid? LinkedEntityId { get; init; }
    public string? ConcurrencyToken { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
}
