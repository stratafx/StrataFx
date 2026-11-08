using System.Security.Claims;

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

/// <summary>
/// A user read from a <see cref="ClaimsPrincipal"/>, so any ASP.NET Core authentication works: Identity cookies,
/// Entra ID, OpenID Connect, JWT bearer tokens. Roles are the principal's role claims.
/// </summary>
public sealed class ClaimsPrincipalUser(ClaimsPrincipal principal) : ICurrentUser
{
    public static ClaimsPrincipalUser Anonymous { get; } = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public ClaimsPrincipal Principal { get; } = principal;
    public string? Name => IsAuthenticated ? Principal.Identity?.Name : null;
    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    /// <summary>Ignores case, like the role names of a <see cref="PermissionPolicy"/> (ClaimsPrincipal.IsInRole does not).</summary>
    public bool IsInRole(string role) => IsAuthenticated && Principal.Identities.Any(identity =>
        identity.FindAll(identity.RoleClaimType).Any(c => string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Decides what the current user may do. Checked by the UI (to hide commands) and the data layer (to enforce).</summary>
public interface IPermissionService
{
    bool IsAllowed(Type entityType, EntityOperation operation);

    /// <summary>
    /// True when the user has a named permission such as <see cref="StrataPermissions.ModelDesigner"/>. Named permissions
    /// are only ever granted explicitly by a <see cref="PermissionPolicy"/>, never by allow-all.
    /// </summary>
    bool HasPermission(string permission) => false;
}

/// <summary>Named permissions that are not about an entity type.</summary>
public static class StrataPermissions
{
    /// <summary>Open the model designer and save model changes. Grant it with <see cref="RolePermissions.AllowModelDesigner"/>.</summary>
    public const string ModelDesigner = "StrataFx.ModelDesigner";
}

public static class PermissionServiceExtensions
{
    public static void Demand(this IPermissionService permissions, Type entityType, EntityOperation operation)
    {
        if (!permissions.IsAllowed(entityType, operation))
            throw new StrataAccessDeniedException(entityType, operation);
    }
}

public static class PermissionServiceNamedExtensions
{
    public static void Demand(this IPermissionService permissions, string permission)
    {
        if (!permissions.HasPermission(permission))
            throw new UnauthorizedAccessException($"You do not have the {permission} permission.");
    }
}

public sealed class StrataAccessDeniedException(Type entityType, EntityOperation operation)
    : UnauthorizedAccessException($"You do not have permission to {operation.ToString().ToLowerInvariant()} {entityType.Name} records.")
{
    public Type EntityType { get; } = entityType;
    public EntityOperation Operation { get; } = operation;
}

/// <summary>
/// Allows every operation on every entity type. The default until a policy is configured. Named permissions such as
/// the model designer are not granted: they need a policy.
/// </summary>
public sealed class AllowAllPermissionService : IPermissionService
{
    public bool IsAllowed(Type entityType, EntityOperation operation) => true;
    public bool HasPermission(string permission) => false;
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

    /// <summary>True when one of the user's roles was granted the named permission.</summary>
    public bool HasPermission(ICurrentUser user, string permission) =>
        _roles.Values.Any(role => role.HasPermission(permission) && (user.IsAuthenticated
            ? user.IsInRole(role.Name)
            : string.Equals(role.Name, PermissionPolicy.AnonymousRole, StringComparison.OrdinalIgnoreCase)));
}

public sealed class RolePermissions(string name)
{
    private readonly Dictionary<Type, EntityOperation> _types = [];
    private readonly HashSet<string> _permissions = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Grants a named permission (see <see cref="StrataPermissions"/>). <see cref="AllowAll"/> does not include these.</summary>
    public RolePermissions Grant(string permission)
    {
        _permissions.Add(permission);
        return this;
    }

    /// <summary>Lets the role open the model designer and save model changes.</summary>
    public RolePermissions AllowModelDesigner() => Grant(StrataPermissions.ModelDesigner);

    public bool HasPermission(string permission) => _permissions.Contains(permission);

    internal EntityOperation GetGranted(Type entityType) => _allTypes | _types.GetValueOrDefault(entityType);
}

/// <summary>Checks a <see cref="PermissionPolicy"/> for the current user.</summary>
public sealed class PolicyPermissionService(PermissionPolicy policy, ICurrentUser user) : IPermissionService
{
    public bool IsAllowed(Type entityType, EntityOperation operation) => policy.IsAllowed(user, entityType, operation);
    public bool HasPermission(string permission) => policy.HasPermission(user, permission);
}
