using Microsoft.EntityFrameworkCore;
using StrataFx.Blazor;
using StrataFx.EFCore;
using StrataFx.Hosting;
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

    // Allow-all is the default. To try role-based security, uncomment this and register an ICurrentUser
    // (e.g. one backed by ASP.NET Core Identity). SalesModule defines the SalesViewer and SalesManager roles.
    // fx.UsePermissionPolicy(policy => policy.Role(PermissionPolicy.AnonymousRole).AllowAll(EntityOperation.Read));
});
builder.Services.AddStrataFxBlazor();

var app = builder.Build();

await SampleDbContext.SeedAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(StrataFxBlazorExtensions.PagesAssembly);

app.Run();
