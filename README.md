# StrataFx

[![CI](https://github.com/stratafx/StrataFx/actions/workflows/ci.yml/badge.svg)](https://github.com/stratafx/StrataFx/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**StrataFx** is an open-source, model-driven application framework for .NET.
You write plain C# entity classes; StrataFx generates the list views, detail forms, lookups,
navigation, validation and permission checks for them, and lets you customize everything in layers.

> Status: early preview (0.1). APIs will change.

## Why "Strata"?

The application model is built in layers (strata), and each one can override the one below:

1. **Code**: entity classes and attributes (`[StrataEntity]`, `[Required]`, `[Display]`, ...)
2. **Modules**: code layers that ship with a feature (`DelegateModelLayer`)
3. **JSON**: deployment or admin overrides (`model.json`), no recompile needed
4. **User**: each signed-in user's own list layouts (columns, order, widths, sort, page size), saved in the database

The user stratum never changes the shared model: it is applied when a view is built, so every user sees the
lower layers plus their own choices.

## Quick start

```csharp
[StrataEntity(NavigationGroup = "Sales")]
public class Customer
{
    public int Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [EmailAddress] public string? Email { get; set; }
    public bool IsVip { get; set; }
}
```

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));

builder.Services.AddStrataFx(fx =>
{
    fx.AddEntitiesFrom(typeof(Customer).Assembly);
    fx.AddJsonModelLayer("model.json");
    fx.UseEntityFrameworkCore<AppDbContext>();
});
builder.Services.AddStrataFxBlazor();

// ...
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(StrataFxBlazorExtensions.PagesAssembly);
```

Add `StrataFxBlazorExtensions.PagesAssembly` to the router's `AdditionalAssemblies`, put `<StrataNavMenu />`
in your layout, and you get `/app/customer` (list) and `/app/customer/{id}` (detail) pages.

## Projects

| Project | What it contains |
|---|---|
| `StrataFx.Core` | Application model and builder, model layers, commands, modules, security, validation, `IDataStore`. No UI or ORM dependencies. |
| `StrataFx.EFCore` | `IDataStore` on EF Core: paging, search, sorting, lookups, saves, with permission and validation enforcement. Optional per-user settings store. |
| `StrataFx.Blazor` | MudBlazor UI: generated list and detail views, property editor registry, navigation menu, command bar, and an `ICurrentUser` read from Blazor's authentication state (works with any authentication). |
| `StrataFx.Identity` | ASP.NET Core Identity: `fx.UseAspNetCoreIdentity<TUser>()`, sign-in, sign-out and access-denied pages, a user menu for the app bar, and a seeder for roles and users. |
| `samples/StrataFx.Sample` | A sales app (customers, products, orders) with a module, commands, a rule and a JSON layer. |
| `tests/StrataFx.Core.Tests` | Unit tests, including EF Core tests on in-memory SQLite. |

## Concepts

- **Entities**: any class with a key (`Id`, `{Type}Id` or `[Key]`) and a parameterless constructor.
  Reference properties to other entities become lookups; their foreign-key scalars are hidden automatically.
- **Collections**: a `List<T>` of another entity is shown on the detail form.
  - `[OwnedCollection]` (e.g. `Order.Lines`): an editable grid; items are validated, inserted, updated and
    deleted together with the parent in one save.
  - Otherwise related (e.g. `Customer.Orders`): an embedded list of separate records, filtered to the parent.
    *New* pre-fills the parent and returns to it after saving.
  - Use `[InverseProperty]` when the item has more than one reference to the parent type.
- **Save handlers**: `IEntitySaveHandler` runs before validation on every save, e.g. to compute totals.
- **Change handlers**: `EntityChangeHandler<T>` (or `IEntityChangeHandler`) reacts when a user changes a field in a
  form, before saving, and the form shows the result right away. In the sample, picking a product fills in the line's
  unit price, and the order total updates as lines change. For a row of an owned collection, the row's handlers run
  first, then the parent's for the collection property; adding or removing a row raises a change on the parent too.
  The context also has `ParentEntity` and `ParentInstance` (for rows), `Kind`, `Item`, and `Data` for lookups.
  Register with `fx.AddChangeHandler<T>()`. Values handlers set don't raise further changes, and failures show a
  message without breaking the form. Keep values that must be right in a save handler too, since data can be saved
  without a form.
- **Commands**: business operations shown as buttons (`StrataCommand<T>`), resolved from DI. A command is shown
  only to users with its `RequiredOperation` on the entity (`Update` by default; override with `Read` for commands
  that only look, such as export or print).
- **Rules**: validation beyond data annotations (`IEntityRule`), enforced in the UI and the data store.
- **Database constraints**: the EF Core store reads unique indexes and foreign keys from the EF model and checks them
  before saving, so users see messages built from captions instead of database errors: "Order # SO-1002 is already
  used." next to the field (owned items too, including two items in the same save), and "This product is used on
  12 order lines and can't be deleted." for a delete blocked by `Restrict`/`NoAction` references. The checks are
  plain queries, so they work the same on every provider; if another user wins a race, the database error is
  explained the same way. The delete confirmation says what a cascade removes ("This will also delete 3 orders and
  their lines.", `IDataStore.GetDeleteImpactAsync`).
- **Modules**: `StrataModule` bundles entities, model layers, commands, rules, save and change handlers, and permissions.
- **Security**: allow-all by default; `fx.UsePermissionPolicy(...)` enables role-based type permissions
  (`policy.Role("Viewer").Allow<Customer>(EntityOperation.Read)`, `policy.Role("Admin").AllowAll()`). A user gets
  the union of their roles' permissions. The UI hides what a user may not do: navigation entries, New, Delete, Save,
  commands, related lists, and shows "You don't have access" for a list or detail link they can't open. The data store
  enforces the same policy on every query, save and delete, so it holds even when code calls the store directly
  (`StrataAccessDeniedException`).
  - **Who the user is**: `ICurrentUser`. `fx.UseBlazorAuthenticationState()` (in `StrataFx.Blazor`) reads the user and
    their role claims from Blazor's `AuthenticationStateProvider`, so it works with any ASP.NET Core authentication
    (Identity, Entra ID, OpenID Connect). It does not use `HttpContext`, which interactive circuits don't have.
  - **ASP.NET Core Identity** (`StrataFx.Identity`):

    ```csharp
    builder.Services.AddStrataFx(fx =>
    {
        // ...
        fx.UseAspNetCoreIdentity<IdentityUser>().AddEntityFrameworkStores<AppDbContext>(); // AppDbContext : IdentityDbContext
        fx.UsePermissionPolicy(policy => policy.Role("Admin").AllowAll());
    });
    // ...
    await app.Services.SeedStrataIdentityAsync<IdentityUser>(users); // creates the policy's roles and the given users
    app.MapStaticAssets().AllowAnonymous();
    app.MapRazorComponents<App>()
       .AddInteractiveServerRenderMode()
       .AddAdditionalAssemblies(StrataFxBlazorExtensions.PagesAssembly, StrataFxIdentityExtensions.PagesAssembly);
    app.MapStrataFxIdentity(); // sign-out endpoint
    ```

    Every page then needs a signed-in user (turn off with `RequireSignIn = false`). The sign-in page sets a cookie, so
    it renders as static HTML: in `App.razor` use `HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null`
    as the render mode of `<Routes>` and `<HeadOutlet>`. In `Routes.razor` use `AuthorizeRouteView` with
    `<RedirectToLogin />` when not authorized, and put `<StrataUserMenu />` (name and Log out) in the app bar.
- **User layouts** (the user stratum): the columns button in a list's toolbar lets each user show or hide columns
  and reorder them (drag, or the arrows); dragging a column header's right edge resizes it (double-click for
  automatic width). The sort and page size are remembered too, and **Reset to default** drops the user's layout.
  - Users choose from the properties that can be shown in a list (not keys, foreign keys, collections or multi-line
    text); `visibleInList` in code or model.json only sets the default.
  - Layouts are merged with the model when the list opens: removed or renamed properties are ignored, and properties
    added later appear where the model puts them, so model changes never break saved layouts. A layout that matches
    the default is not stored, so later model changes reach that user too.
  - A related list inside a form ("Customer → Order history") has its own layout, separate from the full list.
  - Saved per user name and key (`list/customer`, `list/customer.Orders`) through `IUserSettingsStore`. To keep
    them in your database:

    ```csharp
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddStrataUserSettings(); // StrataUserSetting table, unique on UserName + Key
    }
    // in AddStrataFx:
    fx.AddEntityFrameworkCoreUserSettings<AppDbContext>();
    ```

    Without it, layouts are kept in memory until the app restarts. Users who are not signed in get layouts for their
    session only, never shared with other visitors.
  - Users in the `Admin` role also get **Save as default for everyone**, a shared layer between model.json and each
    user's own layout (change the role with `fx.UserSettings.SharedDefaultsRole`, or set it to `null` to turn it off).
- **Editors**: replace or add property editors with `AddStrataFxBlazor(e => e.Register<MyEditor>("name"))`
  and pick them with `[PropertyEditor("name")]`.
- **Themes**: slim built-in themes (Fluent slim by default, plus Graphite, Indigo slate, Nordic and Zinc emerald),
  each with light and dark palettes. Use `<StrataThemeProvider />` instead of `<MudThemeProvider />`, link
  `_content/StrataFx.Blazor/strata.css`, and put `<StrataThemeSwitcher />` in the app bar so users can pick a theme
  and mode (remembered in their browser). Configure with
  `AddStrataFxBlazor(themes: t => { t.DefaultThemeId = StrataThemes.Nordic.Id; t.Themes.Add(StrataThemes.Create(...)); })`,
  or set `t.AllowUserSelection = false` to give everyone the default.

## Run the sample

```bash
dotnet run --project samples/StrataFx.Sample
```

Open http://localhost:5037 and sign in with one of the test users. They are created only in the Development
environment, from `TestUsers` in `samples/StrataFx.Sample/appsettings.Development.json`:

| User | Password | Role | Can |
|---|---|---|---|
| `admin` | `Admin#2026` | Admin | everything |
| `manager` | `Manager#2026` | SalesManager | everything (the role is defined in `SalesModule`) |
| `viewer` | `Viewer#2026` | SalesViewer | read customers, products and orders; no New, Save, Delete, Ship or Mark as VIP |

The database (`strata-sample.db`) is created and seeded on first start; delete it to start over (and after
upgrading, when the schema changed). Try the columns button on a list as `manager`, then sign in as `viewer`: each
user keeps their own layout.

## Roadmap

- More of the user stratum: saved filters and searches, detail form layouts
- Row-level and field-level permissions
- Audit trail, multi-tenancy, reports, dashboards
- User and role management pages; external sign-in (Entra ID, Google) on the Identity sign-in page
- ~~ASP.NET Core Identity integration~~ (done: `StrataFx.Identity`)
- ~~Per-user model layer stored in the database~~ (done: user list layouts)

## License

MIT. StrataFx is an independent project and is not affiliated with any commercial framework vendor.
