using Lavari.Domain.Security;

namespace Lavari.Infrastructure.Persistence.Entities;

public sealed class UserEntity : AuditableSoftDeletableEntity
{
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    [ExcludeFromAudit]
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public bool IsSystemPrincipal { get; set; }
    public List<UserRoleEntity> UserRoles { get; set; } = [];
}

public sealed class RoleEntity : AuditableSoftDeletableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsFullAccess { get; set; }
    public List<RolePermissionEntity> Permissions { get; set; } = [];
    public List<UserRoleEntity> UserRoles { get; set; } = [];
}

public sealed class RolePermissionEntity : AuditableSoftDeletableEntity
{
    public Guid RoleId { get; set; }
    public RoleEntity Role { get; set; } = null!;
    public string ModuleKey { get; set; } = string.Empty;
    public AccessLevel AccessLevel { get; set; }
}

public sealed class UserRoleEntity : AuditableSoftDeletableEntity
{
    public Guid UserId { get; set; }
    public UserEntity User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public RoleEntity Role { get; set; } = null!;
}

public sealed class ApplicationVariableEntity : AuditableSoftDeletableEntity
{
    public string Key { get; set; } = string.Empty;
    public string ValueType { get; set; } = "string";
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ValidationRule { get; set; }
}

public sealed class ErrorCodeDefinitionEntity : AuditableSoftDeletableEntity
{
    public string Code { get; set; } = string.Empty;
    public string TechnicalDefinition { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string PossibleCause { get; set; } = string.Empty;
    public int DefaultHttpStatus { get; set; } = 400;
}
