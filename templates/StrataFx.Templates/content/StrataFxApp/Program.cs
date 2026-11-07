#if (auth)
using Microsoft.AspNetCore.Identity;
#endif
using Microsoft.EntityFrameworkCore;
using StrataFx.Blazor;
using StrataFx.EFCore;
using StrataFx.Hosting;
#if (auth)
using StrataFx.Identity;
using StrataFx.Security;
#endif
using StrataFxApp.Components;
using StrataFxApp.Data;
using StrataFxApp.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("App") ?? "Data Source=app.db"));

builder.Services.AddStrataFx(fx =>
{
    fx.AddEntitiesFrom(typeof(Company).Assembly); // classes marked [StrataEntity]
    fx.AddJsonModelLayer(Path.Combine(builder.Environment.ContentRootPath, "model.json"));
    fx.UseEntityFrameworkCore<AppDbContext>();
    fx.AddEntityFrameworkCoreUserSettings<AppDbContext>(); // each user's list layouts, saved in the database
#if (auth)

    // Users sign in with ASP.NET Core Identity; their roles decide what they can see and do.
    fx.UseAspNetCoreIdentity<IdentityUser>()
        .AddEntityFrameworkStores<AppDbContext>();
    fx.UsePermissionPolicy(policy =>
    {
        policy.Role("Admin").AllowAll();
        policy.Role("User").AllowAll(EntityOperation.Read);
    });
#endif
});
builder.Services.AddStrataFxBlazor();

var app = builder.Build();

await AppDbContext.InitializeAsync(app.Services);
#if (auth)

// The first user is read from configuration, never from code: user secrets in development,
// environment variables or a secret store in production. See README.md.
var admin = builder.Configuration.GetSection("AdminUser").Get<StrataSeedUser>();
if (admin is { UserName.Length: > 0, Password.Length: > 0 })
{
    if (admin.Roles.Count == 0) admin.Roles.Add("Admin");
}
else
{
    admin = null;
    app.Logger.LogWarning("No first user is configured, so nobody can sign in yet. Run: " +
        "dotnet user-secrets set \"AdminUser:UserName\" \"admin\" and " +
        "dotnet user-secrets set \"AdminUser:Password\" \"<a password>\", then restart the app.");
}
// Creates the policy's roles, and the first user if it does not exist yet (an existing user keeps their password).
await app.Services.SeedStrataIdentityAsync<IdentityUser>(admin is null ? null : [admin]);
#endif

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

#if (auth)
// CSS and scripts are public, so the sign-in page can load them before anyone has signed in.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(StrataFxBlazorExtensions.PagesAssembly, StrataFxIdentityExtensions.PagesAssembly);
app.MapStrataFxIdentity(); // sign-out endpoint
#else
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(StrataFxBlazorExtensions.PagesAssembly);
#endif

app.Run();
