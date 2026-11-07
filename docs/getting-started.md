# Getting started with StrataFx

This guide takes you from an empty folder to a small CRM: companies, contacts and deals with line items, a
command, a business rule, calculated totals, roles and per-user list layouts. It takes about 20 minutes.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). For the ideas behind StrataFx, see
[concepts.md](concepts.md).

## 1. Install the template

```bash
dotnet new install StrataFx.Templates
```

This adds the **StrataFx App** template (`stratafx`). To update it later, run the same command again.

## 2. Create an app

```bash
dotnet new stratafx -n Crm
cd Crm
```

| Option | Default | What it does |
|---|---|---|
| `--auth` | `true` | Sign-in with ASP.NET Core Identity, users and roles. `--auth false` creates an app without sign-in where everyone may do everything. |
| `--stratafx-version` | the template's version | The version of the StrataFx packages the project references. |
| `--no-restore` | `false` | Don't restore packages after creating the project. |

What you get:

| File | What it is |
|---|---|
| `Models/Entities.cs` | Two example entities, `Company` and `Contact`. |
| `Data/AppDbContext.cs` | The EF Core context (SQLite), with the Identity tables when sign-in is on, and a few example records. |
| `model.json` | A JSON model layer: captions, visibility and navigation, changed without recompiling. |
| `Program.cs` | `AddStrataFx(...)`: entities, the JSON layer, EF Core, per-user layouts, sign-in and roles. |
| `Components/Layout/MainLayout.razor` | The app bar, `StrataNavMenu`, theme switcher and user menu. |

## 3. Create the first user (sign-in only)

With `--auth true` (the default) nobody can sign in until you configure a first user. Its name and password are
read from configuration, so they never end up in source control. In development use user secrets:

```bash
dotnet user-secrets set "AdminUser:UserName" "admin"
dotnet user-secrets set "AdminUser:Email" "admin@example.com"
dotnet user-secrets set "AdminUser:Password" "<choose a password>"
```

The password must meet the ASP.NET Core Identity defaults (at least 6 characters with upper case, lower case,
a digit and a symbol). The user gets the `Admin` role and is created on the next start if it doesn't exist; an
existing user keeps their password. In production, set `AdminUser__UserName` and `AdminUser__Password` as
environment variables (or in your secret store) for the first start.

## 4. Run it

```bash
dotnet run
```

Open the URL printed in the console and sign in. The menu has **Contacts → Companies** and **Contacts →
Contacts**: lists with search, sorting and paging, and forms with validation, a company lookup on each
contact, and a company's contacts listed in its form. All of it comes from the two classes in
`Models/Entities.cs`.

The database (`app.db`) is created with `EnsureCreated` on the first start. `EnsureCreated` does not change an
existing database, so **delete `app.db` whenever you change entities** while you follow this guide. For real apps
use [migrations](#11-move-to-ef-core-migrations).

## 5. Add your first entity

Add a deal with line items to `Models/Entities.cs`:

```csharp
public enum DealStage { Lead, Proposal, Won, Lost }

[StrataEntity(NavigationGroup = "Sales", Order = 1, Icon = Icons.Material.Outlined.Handshake)]
public class Deal
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Title { get; set; } = ""; // a property called Name, Title, Caption or Code is shown in lookups

    public Company Company { get; set; } = null!; // required reference: a lookup in the form
    public int CompanyId { get; set; }            // the foreign key is hidden automatically

    public DealStage Stage { get; set; }

    [DataType(DataType.Date), Display(Name = "Expected close")]
    public DateTime ExpectedClose { get; set; } = DateTime.Today.AddDays(30);

    [Editable(false), DisplayFormat(DataFormatString = "{0:N2}")]
    public decimal Amount { get; set; } // calculated from the items (step 8)

    [Multiline(3), Display(GroupName = "Notes")]
    public string? Notes { get; set; }

    // Owned: items are edited in a grid inside the deal form and saved, added and deleted with the deal.
    [OwnedCollection]
    public List<DealItem> Items { get; set; } = [];
}

/// <summary>No [StrataEntity]: items have no menu entry of their own; they are reached through their deal.</summary>
public class DealItem
{
    public int Id { get; set; }

    public Deal Deal { get; set; } = null!; // the parent, set when the deal is saved
    public int DealId { get; set; }

    [Required, StringLength(120)]
    public string Description { get; set; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; set; } = 1;

    [Range(0, 1_000_000), Display(Name = "Unit price"), DisplayFormat(DataFormatString = "{0:N2}")]
    public decimal UnitPrice { get; set; }

    [Display(Name = "Total"), DisplayFormatString("N2")]
    public decimal Total => Quantity * UnitPrice; // get-only: shown, not stored
}
```

And a related collection on `Company`, so each company's form lists its deals:

```csharp
public class Company
{
    // ...
    public List<Deal> Deals { get; set; } = [];
}
```

Register the entities with EF Core in `Data/AppDbContext.cs`:

```csharp
public DbSet<Deal> Deals => Set<Deal>();
public DbSet<DealItem> DealItems => Set<DealItem>();
```

`fx.AddEntitiesFrom(...)` in `Program.cs` picks up every class marked `[StrataEntity]`. `DealItem` has no attribute,
so add it in `AddStrataFx`:

```csharp
fx.AddEntity<DealItem>();
```

Delete `app.db`, run, and open **Sales → Deals**. Things to try:

- **New** deal: the company is a lookup; required fields and ranges are checked as you type and again on save.
- Add items in the grid; they are validated and saved with the deal in one transaction.
- Open a company: its deals are listed in the form. **New** there pre-fills the company and comes back to it.
- Delete a company: the confirmation says what else is removed with it ("This will also delete 2 contacts.").

**Owned or related?** A `List<T>` of another entity is *related* by default: separate records with their own
pages, listed in the parent's form and filtered to it. Mark it `[OwnedCollection]` when the items belong to the
parent and are edited with it. If the item type has more than one reference to the parent type, say which one
the collection uses with `[InverseProperty]`.

## 6. Customize with model.json

`model.json` is a model layer applied on top of the classes. Use it for what deployments or admins change, without
recompiling (restart the app to apply). Entities are named by their class name in lower case, properties by their
C# name:

```json
{
  "title": "Crm",
  "entities": {
    "deal": {
      "pluralCaption": "Opportunities",
      "properties": {
        "Notes": { "visibleInList": false },
        "ExpectedClose": { "caption": "Close date", "description": "When the customer is expected to decide." }
      }
    },
    "dealitem": {
      "properties": {
        "Quantity": { "caption": "Qty" }
      }
    }
  }
}
```

| Level | Settings |
|---|---|
| App | `title` |
| Entity | `caption`, `pluralCaption`, `navigationGroup`, `icon`, `order`, `visibleInNavigation`, `lookupProperty` |
| Property | `caption`, `description`, `group`, `order`, `visibleInList`, `visibleInDetail`, `readOnly`, `displayFormat`, `editor`, `lines`, `collectionKind` (`"Owned"` or `"Related"`) |

An unknown entity or property name stops the app at startup with a message, so typos don't go unnoticed.

The same changes can be made in code with a layer, which is how modules adjust the model:

```csharp
fx.AddModelLayer(new DelegateModelLayer("Deal defaults", app =>
{
    app.GetEntity(typeof(Deal)).FindProperty(nameof(Deal.Stage))!.Description = "Won deals need at least one item.";
}));
```

Layers apply in order: the classes and attributes first, then code layers, then `model.json` (the order of the
`Add...` calls). Later layers win. Each user's list layout is applied last, when a list opens (step 10).

## 7. Add a command

Commands are business operations shown as buttons in lists and forms. Create `Sales/SalesFeatures.cs`:

```csharp
using Crm.Models;
using StrataFx.Commands;

namespace Crm.Sales;

/// <summary>Marks the selected deals as won. Works on one or many rows.</summary>
public sealed class MarkWonCommand : StrataCommand<Deal>
{
    public override string Id => "Sales.MarkWon";
    public override string Caption => "Mark as won";
    public override string? Icon => MudBlazor.Icons.Material.Filled.EmojiEvents;
    public override SelectionRequirement Selection => SelectionRequirement.AtLeastOne;
    public override string? ConfirmationMessage => "Mark the selected deals as won?";

    protected override bool CanExecute(IReadOnlyList<Deal> selection, CommandContext context) =>
        selection.Any(d => d.Stage != DealStage.Won);

    protected override async Task<CommandResult> ExecuteAsync(IReadOnlyList<Deal> selection, CommandContext context, CancellationToken cancellationToken)
    {
        var won = 0;
        foreach (var listed in selection.Where(d => d.Stage != DealStage.Won))
        {
            // List rows don't load owned items, so load the full deal before saving it.
            if (await context.Data.FindAsync(context.Entity, listed.Id, cancellationToken) is not Deal deal) continue;
            deal.Stage = DealStage.Won;
            await context.Data.SaveAsync(context.Entity, deal, isNew: false, cancellationToken);
            won++;
        }
        return CommandResult.Ok(won == 1 ? "Deal marked as won." : $"{won} deals marked as won.");
    }
}
```

Register it in `AddStrataFx`:

```csharp
fx.AddCommand<MarkWonCommand>();
```

Commands are resolved from dependency injection, so their constructors can take services. A command is shown
only to users allowed its `RequiredOperation` on the entity: `Update` by default; override it with
`EntityOperation.Read` for commands that only look, such as an export. Saves through `context.Data` run the same
validation, rules and permission checks as the form. Return `CommandResult.Fail("...")` to show an error.

## 8. Add a rule and a save handler

**Rules** check what data annotations can't express. They run in the form and again in the data store on every
save, so they hold for code that saves directly too. Add to `Sales/SalesFeatures.cs`:

```csharp
using StrataFx.Model;
using StrataFx.Validation;

/// <summary>Business rules for deals.</summary>
public sealed class DealRules : IEntityRule
{
    public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Deal);

    public IEnumerable<ValidationError> Validate(EntityModel entity, object instance)
    {
        var deal = (Deal)instance;
        if (deal.Stage == DealStage.Won && deal.Items.Count == 0)
            yield return new ValidationError(nameof(Deal.Items), "A won deal needs at least one item.");
        if (deal.Stage is DealStage.Lead or DealStage.Proposal && deal.ExpectedClose.Date < DateTime.Today)
            yield return new ValidationError(nameof(Deal.ExpectedClose), "An open deal can't close in the past.");
    }
}
```

The property name puts the message next to that field; pass `null` for a message about the whole record.

**Save handlers** run before validation on every save, with or without a form. Use them for values that must
always be right, such as totals:

```csharp
using StrataFx.Data;

/// <summary>Keeps the stored deal amount in step with its items.</summary>
public sealed class DealAmount : IEntitySaveHandler
{
    public static decimal Calculate(Deal deal) => deal.Items.Sum(i => i.Total);

    public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Deal);

    public void BeforeSave(EntityModel entity, object instance, bool isNew) => ((Deal)instance).Amount = Calculate((Deal)instance);
}
```

To update the amount in the form while the user edits items, add a **change handler**. It runs when a field
changes, before saving; for an owned collection, adding, removing or changing an item raises a change of the
parent's collection property:

```csharp
using StrataFx.Changes;

/// <summary>Shows the new amount as items are added, removed or changed (DealAmount sets it again on save).</summary>
public sealed class DealLiveAmount : EntityChangeHandler<Deal>
{
    protected override ValueTask OnChangedAsync(Deal deal, PropertyChangeContext context)
    {
        if (context.Is(nameof(Deal.Items)))
            deal.Amount = DealAmount.Calculate(deal);
        return ValueTask.CompletedTask;
    }
}
```

Register all three:

```csharp
fx.AddRule<DealRules>();
fx.AddSaveHandler<DealAmount>();
fx.AddChangeHandler<DealLiveAmount>();
```

### Package it as a module

As a feature grows, bundle its entities, layers, services and permissions in a `StrataModule`:

```csharp
using Crm.Models;
using StrataFx.Hosting;
using StrataFx.Model;
using StrataFx.Security;

namespace Crm.Sales;

public sealed class SalesModule : StrataModule
{
    public override void ConfigureModel(AppModelBuilder model)
    {
        model.AddEntity<Deal>();
        model.AddEntity<DealItem>();
    }

    public override void ConfigureServices(StrataFxBuilder fx)
    {
        fx.AddCommand<MarkWonCommand>();
        fx.AddRule<DealRules>();
        fx.AddSaveHandler<DealAmount>();
        fx.AddChangeHandler<DealLiveAmount>();
    }

    public override void ConfigureSecurity(PermissionPolicy policy)
    {
        policy.Role("Sales")
            .Allow<Deal>(EntityOperation.All)
            .Allow<Company>(EntityOperation.Read)
            .Allow<Contact>(EntityOperation.Read | EntityOperation.Update);
    }
}
```

and replace the separate registrations in `Program.cs` with one line:

```csharp
fx.AddModule<SalesModule>();
```

Adding an entity twice (here `Deal`, which `AddEntitiesFrom` also finds) is harmless. `ConfigureSecurity` runs
only when the app uses a permission policy (next step).

## 9. Security and roles

Without a policy everyone may do everything. The template (with sign-in) turns on role-based permissions:

```csharp
fx.UsePermissionPolicy(policy =>
{
    policy.Role("Admin").AllowAll();
    policy.Role("User").AllowAll(EntityOperation.Read);
});
```

- A user gets the union of their roles' permissions: `Allow<T>(operations)` for one entity type,
  `AllowAll(operations)` for every type. Operations are `Read`, `Create`, `Update`, `Delete`, `Write` and `All`.
- Users who are not signed in get the permissions of the `Anonymous` role (none unless you add it), but with
  sign-in on every page needs a signed-in user anyway. Turn that off with
  `fx.UseAspNetCoreIdentity<IdentityUser>(configure: o => o.RequireSignIn = false)` and protect pages yourself.
- The UI hides what a user may not do: menu entries, New, Delete, Save, commands and related lists. Links to a
  list or record they can't open show "You don't have access".
- The data store enforces the same policy on every query, save and delete, so it also holds for code that uses
  `IDataStore` directly (it throws `StrataAccessDeniedException`).
- Roles from the policy are created in the database at startup by `SeedStrataIdentityAsync`.

To try the `Sales` role from the module, create a second user. Add more users to configuration the same way as
the first, for example a `Users` section in user secrets:

```bash
dotnet user-secrets set "Users:0:UserName" "sam"
dotnet user-secrets set "Users:0:Password" "<choose a password>"
dotnet user-secrets set "Users:0:Roles:0" "Sales"
```

and seed them together with the first user in `Program.cs`:

```csharp
var users = builder.Configuration.GetSection("Users").Get<List<StrataSeedUser>>() ?? [];
if (admin is not null) users.Add(admin);
await app.Services.SeedStrataIdentityAsync<IdentityUser>(users);
```

Sign in as `sam`: deals can be created, changed and marked as won, companies are read-only (no New, Save or
Delete), and contacts can be edited but not created or deleted. (User and role management pages are on the roadmap; until then, seed users like this or
use `UserManager<IdentityUser>` in your own code.)

Without ASP.NET Core Identity, any authentication works: `fx.UseBlazorAuthenticationState()` reads the user and
their role claims from Blazor's `AuthenticationStateProvider` (Entra ID, OpenID Connect, your own cookies).

## 10. Per-user list layouts

Every list has a columns button in its toolbar. Each user can show, hide and reorder columns (drag them, or use
the arrows) and resize them by dragging a header's right edge (double-click for automatic width). The sort and
page size are remembered too, and **Reset to default** drops the user's layout.

- The template stores layouts in the database: `modelBuilder.AddStrataUserSettings()` in `AppDbContext` and
  `fx.AddEntityFrameworkCoreUserSettings<AppDbContext>()` in `Program.cs`. Without them, layouts are kept in memory
  until the app restarts.
- Users in the `Admin` role also get **Save as default for everyone**, a shared layout between `model.json` and
  each user's own layout. Change the role with `fx.UserSettings.SharedDefaultsRole = "Manager";`, or set it to
  `null` to turn it off.
- `visibleInList` in code or `model.json` only sets the default; users choose from the properties a list can show.
  Layouts survive model changes: removed properties are ignored and new ones appear where the model puts them.
- A related list inside a form (a company's deals) has its own layout, separate from the full list.
- Users who are not signed in get layouts for their session only.

## 11. Move to EF Core migrations

`EnsureCreated` is fine while you experiment. Before your first deployment, switch to migrations so schema changes
keep the data:

```bash
dotnet tool install --global dotnet-ef
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet ef migrations add Initial
```

Then, in `AppDbContext.InitializeAsync`, replace `await db.Database.EnsureCreatedAsync();` with
`await db.Database.MigrateAsync();`. Add a migration (`dotnet ef migrations add <Name>`) after each entity change.

## Next steps

- **Editors**: replace or add property editors with
  `builder.Services.AddStrataFxBlazor(e => e.Register<MyEditor>("rating"))` and pick one with
  `[PropertyEditor("rating")]` or `"editor": "rating"` in `model.json`. An editor is a Razor component that inherits
  `PropertyEditorBase`.
- **Themes**: `AddStrataFxBlazor(themes: t => t.DefaultThemeId = StrataThemes.Nordic.Id)`, or
  `t.AllowUserSelection = false` to give everyone the default.
- **Another database**: swap `UseSqlite` for SQL Server or PostgreSQL in `Program.cs` and remove the decimal
  conversion in `AppDbContext.ConfigureConventions`.
- The [sample app](../samples/StrataFx.Sample) shows all of this in a sales app with customers, products and orders.
