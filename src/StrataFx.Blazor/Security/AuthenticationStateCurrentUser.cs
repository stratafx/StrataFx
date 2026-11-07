using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StrataFx.Hosting;
using StrataFx.Security;

namespace StrataFx.Blazor.Security;

/// <summary>
/// The signed-in user as Blazor sees it: the <see cref="System.Security.Claims.ClaimsPrincipal"/> from the
/// <see cref="AuthenticationStateProvider"/>. It knows nothing about how users sign in, so it works with ASP.NET Core
/// Identity, Entra ID, OpenID Connect or any other cookie or token authentication.
/// </summary>
/// <remarks>
/// <para>
/// Why not <c>IHttpContextAccessor</c>: an interactive Server circuit outlives the HTTP request that started it, so
/// there is no HttpContext (or a stale one) while users click around. The AuthenticationStateProvider is the supported
/// source of the user in every render mode.
/// </para>
/// <para>
/// Why it is safe to read synchronously (<see cref="ICurrentUser"/> is synchronous because the data layer and the UI
/// call it on every check): the provider is scoped to the circuit, or to the request when rendering statically, and
/// the framework gives it the user before any component runs: from HttpContext.User for static rendering and
/// prerendering, and from the connection's user when a circuit starts. The server provider keeps that state as an
/// already completed task. So the principal is captured once, without blocking, and replaced when the provider
/// raises <see cref="AuthenticationStateProvider.AuthenticationStateChanged"/> (for example when revalidation finds
/// the user was deleted or their security stamp changed).
/// </para>
/// <para>
/// When there is no state yet, or it is still loading (outside a Blazor render, or a custom provider that fetches
/// the user asynchronously), the user counts as anonymous: permissions fail closed, and no thread ever waits on
/// <c>.Result</c>.
/// </para>
/// </remarks>
public sealed class AuthenticationStateCurrentUser : ICurrentUser, IDisposable
{
    private readonly AuthenticationStateProvider _provider;
    private volatile ClaimsPrincipalUser? _user;

    public AuthenticationStateCurrentUser(AuthenticationStateProvider provider)
    {
        _provider = provider;
        _provider.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    public string? Name => User.Name;
    public bool IsAuthenticated => User.IsAuthenticated;
    public bool IsInRole(string role) => User.IsInRole(role);

    private ClaimsPrincipalUser User => _user ?? Capture();

    private ClaimsPrincipalUser Capture()
    {
        Task<AuthenticationState> state;
        try
        {
            state = _provider.GetAuthenticationStateAsync();
        }
        catch (InvalidOperationException)
        {
            // The server provider throws when nothing has set the state yet, e.g. a service used outside a render.
            return ClaimsPrincipalUser.Anonymous;
        }
        return Use(state);
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        _user = null;
        Use(state);
    }

    private ClaimsPrincipalUser Use(Task<AuthenticationState> state)
    {
        if (state.IsCompletedSuccessfully)
            return _user = new ClaimsPrincipalUser(state.Result.User);

        // Not known yet: anonymous for now, and the real user once the task completes.
        state.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) _user = new ClaimsPrincipalUser(t.Result.User);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return ClaimsPrincipalUser.Anonymous;
    }

    public void Dispose() => _provider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
}

public static class StrataFxBlazorSecurityExtensions
{
    /// <summary>
    /// Checks permissions for the user Blazor's authentication state reports, whichever way they signed in.
    /// Configure authentication (Identity, OpenID Connect, ...) as usual; pair it with <c>fx.UsePermissionPolicy(...)</c>.
    /// </summary>
    public static StrataFxBuilder UseBlazorAuthenticationState(this StrataFxBuilder fx)
    {
        fx.Services.AddCascadingAuthenticationState();
        fx.Services.RemoveAll<ICurrentUser>();
        fx.Services.AddScoped<ICurrentUser, AuthenticationStateCurrentUser>();
        return fx;
    }
}
