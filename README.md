# StrataFx

[![CI](https://github.com/stratafx/StrataFx/actions/workflows/ci.yml/badge.svg)](https://github.com/stratafx/StrataFx/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/StrataFx.Core.svg)](https://www.nuget.org/packages/StrataFx.Core)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/stratafx/StrataFx/blob/main/LICENSE)

**StrataFx** is an open-source, model-driven application framework for .NET.
You write plain C# entity classes; StrataFx generates the list views, detail forms, lookups,
navigation, validation and permission checks for them, and lets you customize everything in layers.

> Status: early preview (0.1). APIs will change between 0.x versions.

## Install

Create a new app from the template:

```bash
dotnet new install StrataFx.Templates
dotnet new stratafx -n MyApp
cd MyApp
dotnet run
```

With sign-in (the default), configure the first user before you run; see the generated `README.md` or
[getting started](https://github.com/stratafx/StrataFx/blob/main/docs/getting-started.md#3-create-the-first-user-sign-in-only). Use
`dotnet new stratafx -n MyApp --auth false` for an app without sign-in.

Or add the packages to an existing Blazor Server app (.NET 10):

```bash
dotnet add package StrataFx.Blazor
dotnet add package StrataFx.EFCore
dotnet add package StrataFx.Identity   # optional: sign-in with ASP.NET Core Identity
dotnet add package StrataFx.Designer   # optional: the model designer for admins
```

**Documentation:** [Getting started](https://github.com/stratafx/StrataFx/blob/main/docs/getting-started.md) ·
[Concepts](https://github.com/stratafx/StrataFx/blob/main/docs/concepts.md) · [Changelog](https://github.com/stratafx/StrataFx/blob/main/CHANGELOG.md) ·
[Contributing](https://github.com/stratafx/StrataFx/blob/main/CONTRIBUTING.md)

## Why "Strata"?

The application model is built in layers (strata), and each one can override the one below:

1. **Code**: entity classes and attributes (`[StrataEntity]`, `[Required]`, `[Display]`, ...)
2. **Modules**: code layers that ship with a feature (`DelegateModelLayer`)
3. **JSON**: deployment or admin overrides (`model.json`), no recompile needed
4. **User**: each signed-in user's own list layouts (columns, order, widths, sort, page size), saved in the database

The user stratum never changes the shared model: it is applied when a view is built, so every user sees the
lower layers plus their own choices.

## Quick start (existing app)

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
in your layout, and you get `/app/customer` (list) and `/app/customer/{id}` (detail) pages. The template sets up the
layout, theme, router and sign-in for you; [getting started](https://github.com/stratafx/StrataFx/blob/main/docs/getting-started.md) walks through
the rest.

## Packages

| Package | What it contains |
|---|---|
| `StrataFx.Core` | Application model and builder, model layers, commands, modules, security, validation, `IDataStore`. No UI or ORM dependencies. |
| `StrataFx.EFCore` | `IDataStore` on EF Core: paging, search, sorting, lookups, saves, with permission and validation enforcement. Optional per-user settings store. |
| `StrataFx.Blazor` | MudBlazor UI: generated list and detail views, property editor registry, navigation menu, command bar, and an `ICurrentUser` read from Blazor's authentication state (works with any authentication). |
| `StrataFx.Identity` | ASP.NET Core Identity: `fx.UseAspNetCoreIdentity<TUser>()`, sign-in, sign-out and access-denied pages, a user menu for the app bar, and a seeder for roles and users. |
| `StrataFx.Designer` | The model designer: `fx.AddModelDesigner()`, an admin-only page at `/strata/designer` to change captions, navigation, columns, forms and editors with a live preview, saved to `model.json` in development or to the database, applied without a restart. |
| `StrataFx.Templates` | The `dotnet new stratafx` project template. |
| `samples/StrataFx.Sample` (not packaged) | A sales app (customers, products, orders) with a module, commands, a rule and a JSON layer. |
| `tests/StrataFx.Core.Tests` (not packaged) | Unit tests, including EF Core tests on in-memory SQLite. |

## Concepts

- **Entities**: plain classes with a key. References become lookups; `List<T>` collections are shown in the form,
  either **owned** (`[OwnedCollection]`, edited and saved with the parent) or **related** (separate records).
- **Model layers**: code, modules, `model.json`, the database layer and each user's list layouts, each overriding the
  one below.
- **Model designer**: admins change the model in the app, see where each value comes from, preview the list and form,
  and save; everyone gets the new model without a restart.
- **Modules** bundle entities, layers, commands, rules, handlers and permissions for a feature.
- **Commands** are business operations shown as buttons; **rules** validate beyond data annotations; **save
  handlers** compute values on every save; **change handlers** react to edits in a form.
- **Data**: `IDataStore`, implemented on EF Core with permission and validation enforcement and friendly messages
  for unique indexes and blocked deletes.
- **Security**: role-based permissions enforced in the UI and the data store; any ASP.NET Core authentication, or
  `StrataFx.Identity` for sign-in pages out of the box.
- **Editors and themes**: replaceable property editors and slim light/dark themes on MudBlazor.

Details and code: [docs/concepts.md](https://github.com/stratafx/StrataFx/blob/main/docs/concepts.md).

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

As `admin`, open the model designer (the icon in the app bar, or `/strata/designer`): rename an entity or hide a
column, look at the preview, and save. The sample saves to `samples/StrataFx.Sample/model.json` (it runs in
Development) and the app picks up the change at once. `manager` and `viewer` can't open the designer.

## Roadmap

- More of the user stratum: saved filters and searches, detail form layouts
- Row-level and field-level permissions
- Audit trail, multi-tenancy, reports, dashboards
- User and role management pages; external sign-in (Entra ID, Google) on the Identity sign-in page
- ~~ASP.NET Core Identity integration~~ (done: `StrataFx.Identity`)
- ~~Per-user model layer stored in the database~~ (done: user list layouts)
- ~~A visual model designer~~ (done: `StrataFx.Designer`)

## Contributing

Build, test and coding conventions are in [CONTRIBUTING.md](https://github.com/stratafx/StrataFx/blob/main/CONTRIBUTING.md); maintainers release with
[RELEASING.md](https://github.com/stratafx/StrataFx/blob/main/RELEASING.md).

## License

MIT. StrataFx is an independent project and is not affiliated with any commercial framework vendor.
