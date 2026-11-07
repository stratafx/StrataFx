using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrataFx.Blazor.Security;
using StrataFx.Hosting;
using StrataFx.Security;

namespace StrataFx.Identity;

public static class StrataIdentityRoutes
{
    public const string Login = "account/login";
    public const string Logout = "account/logout";
    public const string AccessDenied = "account/access-denied";
}

public sealed class StrataIdentityOptions
{
    /// <summary>
    /// When true (the default) every page and endpoint needs a signed-in user, except the sign-in pages and anything
    /// marked [AllowAnonymous]. Set to false to protect pages yourself with [Authorize].
    /// </summary>
    public bool RequireSignIn { get; set; } = true;

    /// <summary>Lock an account for a while after repeated wrong passwords.</summary>
    public bool LockoutOnFailure { get; set; } = true;
}

/// <summary>
/// Signs users in and out. Lets the (non-generic) sign-in pages work with any Identity user type.
/// </summary>
public interface IStrataSignInService
{
    Task<SignInResult> PasswordSignInAsync(string userName, string password, bool rememberMe);
    Task SignOutAsync();
}

internal sealed class StrataSignInService<TUser>(SignInManager<TUser> signInManager, IOptions<StrataIdentityOptions> options)
    : IStrataSignInService where TUser : class
{
    public async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool rememberMe)
    {
        // Users may type their e-mail address instead of their user name.
        var users = signInManager.UserManager;
        var user = await users.FindByNameAsync(userName) ?? (userName.Contains('@') ? await users.FindByEmailAsync(userName) : null);
        if (user is null) return SignInResult.Failed;
        return await signInManager.PasswordSignInAsync(user, password, rememberMe, options.Value.LockoutOnFailure);
    }

    public Task SignOutAsync() => signInManager.SignOutAsync();
}

/// <summary>
/// Checks every 30 minutes, while a circuit is connected, that the user still exists and their security stamp
/// has not changed (password reset, roles changed by an admin). When it fails the user is signed out of the circuit.
/// </summary>
internal sealed class StrataRevalidatingAuthenticationStateProvider<TUser>(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory) where TUser : class
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<TUser>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null) return false;
        if (!users.SupportsUserSecurityStamp) return true;
        var principalStamp = state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await users.GetSecurityStampAsync(user);
    }
}

public static class StrataFxIdentityExtensions
{
    /// <summary>The assembly with the sign-in pages. Add it to the Router's AdditionalAssemblies and AddAdditionalAssemblies.</summary>
    public static Assembly PagesAssembly => typeof(StrataFxIdentityExtensions).Assembly;

    /// <summary>ASP.NET Core Identity with <see cref="IdentityRole"/> roles. See the generic overload.</summary>
    public static IdentityBuilder UseAspNetCoreIdentity<TUser>(this StrataFxBuilder fx,
        Action<IdentityOptions>? identity = null, Action<StrataIdentityOptions>? configure = null) where TUser : class =>
        fx.UseAspNetCoreIdentity<TUser, IdentityRole>(identity, configure);

    /// <summary>
    /// Signs users in with ASP.NET Core Identity (cookie authentication) and checks the permission policy against
    /// their roles. Add a store to the returned builder, e.g. <c>.AddEntityFrameworkStores&lt;AppDbContext&gt;()</c>,
    /// and call <c>app.MapStrataFxIdentity()</c>.
    /// </summary>
    public static IdentityBuilder UseAspNetCoreIdentity<TUser, TRole>(this StrataFxBuilder fx,
        Action<IdentityOptions>? identity = null, Action<StrataIdentityOptions>? configure = null)
        where TUser : class where TRole : class
    {
        var services = fx.Services;
        var options = new StrataIdentityOptions();
        configure?.Invoke(options);
        services.Configure<StrataIdentityOptions>(o =>
        {
            o.RequireSignIn = options.RequireSignIn;
            o.LockoutOnFailure = options.LockoutOnFailure;
        });

        fx.UseBlazorAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, StrataRevalidatingAuthenticationStateProvider<TUser>>();
        services.AddScoped<IStrataSignInService, StrataSignInService<TUser>>();

        services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.LoginPath = "/" + StrataIdentityRoutes.Login;
            cookie.LogoutPath = "/" + StrataIdentityRoutes.Logout;
            cookie.AccessDeniedPath = "/" + StrataIdentityRoutes.AccessDenied;
        });

        var authorization = services.AddAuthorizationBuilder();
        if (options.RequireSignIn)
            authorization.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services.AddIdentityCore<TUser>(o => identity?.Invoke(o))
            .AddRoles<TRole>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
    }

    /// <summary>
    /// Maps the sign-out endpoint: a form POST with an antiforgery token, so another site cannot sign users out.
    /// </summary>
    public static IEndpointConventionBuilder MapStrataFxIdentity(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/" + StrataIdentityRoutes.Logout, async (HttpContext context,
            [FromServices] IAntiforgery antiforgery, [FromServices] IStrataSignInService signIn) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await signIn.SignOutAsync();
            return TypedResults.LocalRedirect("~/" + StrataIdentityRoutes.Login);
        });
}

/// <summary>A user to create at startup, e.g. test logins read from appsettings.Development.json.</summary>
public sealed class StrataSeedUser
{
    public string UserName { get; set; } = "";
    public string? Email { get; set; }
    public string Password { get; set; } = "";
    public List<string> Roles { get; set; } = [];
}

public static class StrataIdentitySeeder
{
    /// <summary>
    /// Creates the roles of the permission policy (all but the anonymous role) and the given users, if missing.
    /// Existing users keep their password; missing roles are added to them. Safe to run on every start.
    /// </summary>
    public static async Task SeedStrataIdentityAsync<TUser, TRole>(this IServiceProvider services, IEnumerable<StrataSeedUser>? users = null)
        where TUser : class, new() where TRole : class, new()
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var roleManager = provider.GetRequiredService<RoleManager<TRole>>();
        var userManager = provider.GetRequiredService<UserManager<TUser>>();
        var seedUsers = users?.ToList() ?? [];

        var roleNames = (provider.GetService<PermissionPolicy>()?.Roles.Select(r => r.Name) ?? [])
            .Where(r => !string.Equals(r, PermissionPolicy.AnonymousRole, StringComparison.OrdinalIgnoreCase))
            .Concat(seedUsers.SelectMany(u => u.Roles))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in roleNames)
        {
            if (await roleManager.RoleExistsAsync(name)) continue;
            var role = new TRole();
            await roleManager.SetRoleNameAsync(role, name);
            Check(await roleManager.CreateAsync(role), $"role {name}");
        }

        foreach (var seed in seedUsers.Where(u => !string.IsNullOrWhiteSpace(u.UserName)))
        {
            var user = await userManager.FindByNameAsync(seed.UserName);
            if (user is null)
            {
                user = new TUser();
                await userManager.SetUserNameAsync(user, seed.UserName);
                if (seed.Email is not null) await userManager.SetEmailAsync(user, seed.Email);
                Check(await userManager.CreateAsync(user, seed.Password), $"user {seed.UserName}");
            }

            var missing = new List<string>();
            foreach (var role in seed.Roles)
            {
                if (!await userManager.IsInRoleAsync(user, role)) missing.Add(role);
            }
            if (missing.Count > 0) Check(await userManager.AddToRolesAsync(user, missing), $"roles of {seed.UserName}");
        }
    }

    /// <summary>Same as the generic overload, with <see cref="IdentityRole"/> roles.</summary>
    public static Task SeedStrataIdentityAsync<TUser>(this IServiceProvider services, IEnumerable<StrataSeedUser>? users = null)
        where TUser : class, new() =>
        services.SeedStrataIdentityAsync<TUser, IdentityRole>(users);

    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not create {what}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
    }
}
