# StrataFx

**StrataFx** is an open-source, model-driven application framework for .NET.
You write plain C# entity classes; StrataFx generates the list views, detail forms, lookups,
navigation, validation and permission checks for them, and lets you customize everything in layers.

> Status: early preview (0.1). APIs will change.

## Why "Strata"?

The application model is built in layers (strata), and each one can override the one below:

1. **Code**: entity classes and attributes (`[StrataEntity]`, `[Required]`, `[Display]`, ...)
2. **Modules**: code layers that ship with a feature (`DelegateModelLayer`)
3. **JSON**: deployment or admin overrides (`model.json`), no recompile needed
4. *(planned)* **User**: per-user customizations saved in the database

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
| `StrataFx.EFCore` | `IDataStore` on EF Core: paging, search, sorting, lookups, saves, with permission and validation enforcement. |
| `StrataFx.Blazor` | MudBlazor UI: generated list and detail views, property editor registry, navigation menu, command bar. |
| `samples/StrataFx.Sample` | A sales app (customers, products, orders) with a module, commands, a rule and a JSON layer. |
| `tests/StrataFx.Core.Tests` | Unit tests, including EF Core tests on in-memory SQLite. |

## Concepts

- **Entities**: any class with a key (`Id`, `{Type}Id` or `[Key]`) and a parameterless constructor.
  Reference properties to other entities become lookups; their foreign-key scalars are hidden automatically.
- **Commands**: business operations shown as buttons (`StrataCommand<T>`), resolved from DI.
- **Rules**: validation beyond data annotations (`IEntityRule`), enforced in the UI and the data store.
- **Modules**: `StrataModule` bundles entities, model layers, commands, rules and permissions.
- **Security**: allow-all by default; `fx.UsePermissionPolicy(...)` enables role-based type permissions,
  checked in the UI and enforced by the data store.
- **Editors**: replace or add property editors with `AddStrataFxBlazor(e => e.Register<MyEditor>("name"))`
  and pick them with `[PropertyEditor("name")]`.

## Run the sample

```bash
dotnet run --project samples/StrataFx.Sample
```

## Roadmap

- Collection properties (master-detail grids)
- Per-user model layer stored in the database
- Row-level and field-level permissions
- Audit trail, multi-tenancy, reports, dashboards
- ASP.NET Core Identity integration

## License

MIT. StrataFx is an independent project and is not affiliated with any commercial framework vendor.
