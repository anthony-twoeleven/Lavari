namespace Lavari.Contracts.Admin;

public sealed record AuditEntryDto
{
    public required Guid Id { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Operation { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required string ActorId { get; init; }
    public required string ActorDisplayName { get; init; }
    public required string CorrelationId { get; init; }
    public required string TransactionId { get; init; }
    public required int SchemaVersion { get; init; }
    public string? BeforeJson { get; init; }
    public string? AfterJson { get; init; }
}

public sealed record ApplicationVariableDto
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string ValueType { get; init; }
    public required string Value { get; init; }
    public required string Description { get; init; }
    public string? ValidationRule { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record UpsertApplicationVariableRequest
{
    public required string Key { get; init; }
    public required string ValueType { get; init; }
    public required string Value { get; init; }
    public required string Description { get; init; }
    public string? ValidationRule { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record RoleDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsFullAccess { get; init; }
    public required IReadOnlyDictionary<string, int> ModulePermissions { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record UpsertRoleRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsFullAccess { get; init; }
    public required IReadOnlyDictionary<string, int> ModulePermissions { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record UserAdminDto
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsSystemPrincipal { get; init; }
    public required IReadOnlyList<Guid> RoleIds { get; init; }
    public required IReadOnlyList<string> RoleNames { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required bool IsDeleted { get; init; }
}

public sealed record CreateUserRequest
{
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Password { get; init; }
    public required IReadOnlyList<Guid> RoleIds { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record UpdateUserRequest
{
    public required string DisplayName { get; init; }
    public required bool IsActive { get; init; }
    public required IReadOnlyList<Guid> RoleIds { get; init; }
    public string? NewPassword { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record ErrorCodeDefinitionDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string TechnicalDefinition { get; init; }
    public required string UserMessage { get; init; }
    public required string PossibleCause { get; init; }
    public required int DefaultHttpStatus { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record OperationalErrorOccurrenceDto
{
    public required Guid Id { get; init; }
    public required string ErrorCode { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required string CorrelationId { get; init; }
    public string? ActorId { get; init; }
    public string? RequestPath { get; init; }
    public required string SanitizedTechnicalDetail { get; init; }
    public string? StackTrace { get; init; }
}
