using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StrataFx.Blazor;
using StrataFx.EFCore;
using StrataFx.Hosting;
using StrataFx.Identity;
using StrataFx.Sample.Components;
using StrataFx.Sample.Data;
using StrataFx.Sample.Sales;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<SampleDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Sample") ?? "Data Source=strata-sample.db"));

builder.Services.AddStrataFx(fx =>
{
    fx.AddModule<SalesModule>();
    fx.AddJsonModelLayer(Path.Combine(builder.Environment.ContentRootPath, "model.json"));
    fx.UseEntityFrameworkCore<SampleDbContext>();
    fx.AddEntityFrameworkCoreUserSettings<SampleDbContext>(); // each user's list layouts, saved in the database

    // Users sign in with ASP.NET Core Identity; their roles decide what they can see and do.
    // SalesModule defines the SalesViewer and SalesManager roles; Admin may do everything.
    fx.UseAspNetCoreIdentity<IdentityUser>()
        .AddEntityFrameworkStores<SampleDbContext>();
    fx.UsePermissionPolicy(policy => policy.Role("Admin").AllowAll());
});
builder.Services.AddStrataFxBlazor();

var app = builder.Build();

await SampleDbContext.SeedAsync(app.Services);
// Roles always; the test users (from appsettings.Development.json) only in development.
await app.Services.SeedStrataIdentityAsync<IdentityUser>(app.Environment.IsDevelopment()
    ? builder.Configuration.GetSection("TestUsers").Get<StrataSeedUser[]>()
    : null);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

// CSS and scripts are public, so the sign-in page can load them before anyone has signed in.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(StrataFxBlazorExtensions.PagesAssembly, StrataFxIdentityExtensions.PagesAssembly);
app.MapStrataFxIdentity();

app.Run();
