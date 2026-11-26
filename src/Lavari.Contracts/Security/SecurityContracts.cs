namespace Lavari.Contracts.Security;

public sealed record ModuleInfoDto
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
}

public sealed record UserSessionDto
{
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsFullAccess { get; init; }
    public required string EditingSessionId { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required IReadOnlyDictionary<string, int> EffectivePermissions { get; init; }
}

public sealed record LoginRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
}

public sealed record AppearanceDto
{
    public required string Mode { get; init; }
    public required string Color { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record UpdateAppearanceRequest
{
    public required string Mode { get; init; }
    public required string Color { get; init; }
    public required string ConcurrencyToken { get; init; }
}
