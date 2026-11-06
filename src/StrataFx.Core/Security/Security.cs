namespace StrataFx.Security;

/// <summary>Operations that can be allowed or denied on an entity type.</summary>
[Flags]
public enum EntityOperation
{
    None = 0,
    Read = 1,
    Create = 2,
    Update = 4,
    Delete = 8,
    Write = Create | Update | Delete,
    All = Read | Write,
}

/// <summary>The user StrataFx checks permissions for. Hosts plug in their own (ASP.NET Core Identity, etc.).</summary>
public interface ICurrentUser
{
    string? Name { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}

/// <summary>Used when the host registers no user: everyone is an anonymous user with no roles.</summary>
public sealed class AnonymousUser : ICurrentUser
{
    public string? Name => null;
    public bool IsAuthenticated => false;
    public bool IsInRole(string role) => false;
}

/// <summary>Decides what the current user may do. Checked by the UI (to hide commands) and the data layer (to enforce).</summary>
public interface IPermissionService
{
    bool IsAllowed(Type entityType, EntityOperation operation);
}

public static class PermissionServiceExtensions
{
    public static void Demand(this IPermissionService permissions, Type entityType, EntityOperation operation)
    {
        if (!permissions.IsAllowed(entityType, operation))
            throw new StrataAccessDeniedException(entityType, operation);
    }
}

public sealed class StrataAccessDeniedException(Type entityType, EntityOperation operation)
    : UnauthorizedAccessException($"You do not have permission to {operation.ToString().ToLowerInvariant()} {entityType.Name} records.")
{
    public Type EntityType { get; } = entityType;
    public EntityOperation Operation { get; } = operation;
}

/// <summary>Allows everything. The default until a policy is configured.</summary>
public sealed class AllowAllPermissionService : IPermissionService
{
    public bool IsAllowed(Type entityType, EntityOperation operation) => true;
}

/// <summary>
/// Role-based type permissions. A user gets the union of the permissions of all their roles.
/// Unauthenticated users get the permissions of <see cref="AnonymousRole"/>.
/// </summary>
/// <example>
/// policy.Role("Viewer").Allow&lt;Customer&gt;(EntityOperation.Read);
/// policy.Role("Admin").AllowAll();
/// </example>
public sealed class PermissionPolicy
{
    public const string AnonymousRole = "Anonymous";

    private readonly Dictionary<string, RolePermissions> _roles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<RolePermissions> Roles => _roles.Values;

    public RolePermissions Role(string name)
    {
        if (!_roles.TryGetValue(name, out var role))
            _roles[name] = role = new RolePermissions(name);
        return role;
    }

    public bool IsAllowed(ICurrentUser user, Type entityType, EntityOperation operation)
    {
        var granted = EntityOperation.None;
        foreach (var role in _roles.Values)
        {
            var applies = user.IsAuthenticated
                ? user.IsInRole(role.Name)
                : string.Equals(role.Name, AnonymousRole, StringComparison.OrdinalIgnoreCase);
            if (applies) granted |= role.GetGranted(entityType);
        }
        return (granted & operation) == operation;
    }
}

public sealed class RolePermissions(string name)
{
    private readonly Dictionary<Type, EntityOperation> _types = [];
    private EntityOperation _allTypes;

    public string Name { get; } = name;

    public RolePermissions Allow<T>(EntityOperation operations) => Allow(typeof(T), operations);

    public RolePermissions Allow(Type entityType, EntityOperation operations)
    {
        _types[entityType] = _types.GetValueOrDefault(entityType) | operations;
        return this;
    }

    /// <summary>Grants operations on every entity type.</summary>
    public RolePermissions AllowAll(EntityOperation operations = EntityOperation.All)
    {
        _allTypes |= operations;
        return this;
    }

    internal EntityOperation GetGranted(Type entityType) => _allTypes | _types.GetValueOrDefault(entityType);
}

/// <summary>Checks a <see cref="PermissionPolicy"/> for the current user.</summary>
public sealed class PolicyPermissionService(PermissionPolicy policy, ICurrentUser user) : IPermissionService
{
    public bool IsAllowed(Type entityType, EntityOperation operation) => policy.IsAllowed(user, entityType, operation);
}
