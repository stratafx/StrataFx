# StrataFx concepts

StrataFx builds an **application model** from your entity classes and generates the UI from it: lists, forms,
lookups, navigation, validation and permission checks. You change the generated app by changing the model, in
layers, and by adding behavior (commands, rules, handlers) that the generated views pick up.

For a hands-on walkthrough, see [getting-started.md](getting-started.md).

## The strata

The model is built in layers (strata). Each one can override the one below:

1. **Code**: entity classes and attributes (`[StrataEntity]`, `[Required]`, `[Display]`, `[OwnedCollection]`, ...).
2. **Modules**: code layers that ship with a feature (`DelegateModelLayer`, added with `model.AddLayer(...)` or
   `fx.AddModelLayer(...)`).
3. **JSON**: deployment or admin overrides (`model.json`, `fx.AddJsonModelLayer(path)`), no recompile needed.
   Unknown entity or property names stop the app at startup, so typos are caught.
4. **Database**: changes saved by the [model designer](#model-designer) outside development
   (`fx.AddDatabaseModelLayer()`, added by `fx.AddModelDesigner()`). Always applied after every other layer.
5. **User**: each signed-in user's list layouts (columns, order, widths, sort, page size), saved in the database.

Layers 1 to 4 build one shared model (`AppModel`). The user stratum never changes it: it is applied when a list
opens, so every user sees the lower layers plus their own choices.

The model is built at startup by `IAppModelProvider` and can be rebuilt while the app runs
(`IAppModelProvider.Rebuild()`, which the designer calls after saving). A rebuild makes a new `AppModel` and swaps it
in; a model that a page is using is never changed. If a layer fails (a typo in model.json), the app keeps the last
good model and `IAppModelProvider.LastError` says why. Components that inject `AppModel` get the model current when
they are created; StrataFx's pages read `IAppModelProvider.Current` when they are navigated to, and the navigation
menu and `<StrataAppTitle />` update at once (subscribe to `IAppModelProvider.Changed` to do the same).

## Entities

Any class with a key (`Id`, `{Type}Id` or `[Key]`) and a public parameterless constructor. Mark it
`[StrataEntity]` to give it a menu entry (`NavigationGroup`, `Order`, `Icon`, `Caption`, `PluralCaption`) and pick
it up with `fx.AddEntitiesFrom(assembly)`, or add it with `fx.AddEntity<T>()`.

- Property captions come from `[Display(Name)]` or the property name split into words; `[Display(GroupName)]`
  groups fields in the form.
- Data annotations (`[Required]`, `[StringLength]`, `[Range]`, `[EmailAddress]`, ...) are validated in the form and
  in the data store. `[Editable(false)]` and get-only properties are read-only.
- `[HideIn(ViewKinds.List)]`, `[Multiline(lines)]`, `[DisplayFormatString("N2")]` (or `[DisplayFormat]`) and
  `[LookupText(nameof(Number))]` tune how properties are shown.
- **References** to other entities become lookups. Their foreign-key scalars (`CustomerId`) are hidden. A lookup
  shows the property named by `[LookupText]`, or one called Name, Title, Caption or Code, or the first string.
  A non-nullable reference is required.

## Collections

A `List<T>` of another entity is shown on the detail form.

- `[OwnedCollection]` (e.g. `Order.Lines`): an editable grid. Items are validated, inserted, updated and deleted
  together with the parent in one save. Owned item types have no menu entry.
- Otherwise **related** (e.g. `Customer.Orders`): an embedded list of separate records, filtered to the parent.
  *New* pre-fills the parent and returns to it after saving.
- Use `[InverseProperty]` when the item has more than one reference to the parent type.

## Modules

A `StrataModule` bundles a feature: `ConfigureModel` (entities and code layers), `ConfigureServices` (commands,
rules, handlers, your own services) and `ConfigureSecurity` (roles and permissions). Add it with
`fx.AddModule<T>()`.

## Commands

Business operations shown as buttons in lists and forms: `StrataCommand<T>` (or `StrataCommand` for several
entity types). A command has an `Id`, `Caption`, `Icon`, `Target` (list, detail or both), `Selection` (none, one,
at least one) and an optional `ConfirmationMessage`. Register with `fx.AddCommand<T>()`; commands are resolved from
dependency injection.

A command is shown only to users with its `RequiredOperation` on the entity: `Update` by default; override with
`Read` for commands that only look, such as export or print.

## Rules, save handlers and change handlers

- **Rules** (`IEntityRule`, `fx.AddRule<T>()`): validation beyond data annotations, enforced in the UI and in the
  data store.
- **Save handlers** (`IEntitySaveHandler`, `fx.AddSaveHandler<T>()`): run before validation on every save, with or
  without a form, e.g. to compute totals or stamp dates. The source of truth for values that must be right.
- **Change handlers** (`EntityChangeHandler<T>` or `IEntityChangeHandler`, `fx.AddChangeHandler<T>()`): react when a
  user changes a field in a form, before saving, and the form shows the result right away (e.g. picking a product
  fills in the price). For a row of an owned collection, the row's handlers run first, then the parent's for the
  collection property; adding or removing a row raises a change on the parent too. The context has `Property`,
  `Kind`, `Item`, `ParentEntity`, `ParentInstance` and `Data` (for lookups). Values handlers set don't raise
  further changes, and failures show a message without breaking the form. Keep values that must be right in a
  save handler too, since data can be saved without a form.

## Data

`IDataStore` is the storage-agnostic API the views use: query (paging, search, sort, filters), find, lookup, save
(with owned collections) and delete. `StrataFx.EFCore` implements it on EF Core (`fx.UseEntityFrameworkCore<T>()`,
with the context registered by `AddDbContextFactory`). Every implementation enforces permissions and validation.

The EF Core store reads unique indexes and foreign keys from the EF model and checks them before saving, so users
see messages built from captions instead of database errors: "Order # SO-1002 is already used." next to the field
(owned items too, including two items in the same save), and "This product is used on 12 order lines and can't
be deleted." for a delete blocked by `Restrict`/`NoAction` references. The checks are plain queries, so they work
the same on every provider; if another user wins a race, the database error is explained the same way. The delete
confirmation says what a cascade removes ("This will also delete 3 orders and their lines.",
`IDataStore.GetDeleteImpactAsync`).

## Security

Allow-all by default. `fx.UsePermissionPolicy(...)` enables role-based type permissions
(`policy.Role("Viewer").Allow<Customer>(EntityOperation.Read)`, `policy.Role("Admin").AllowAll()`). A user gets the
union of their roles' permissions; users who are not signed in get the `Anonymous` role's.

The UI hides what a user may not do: navigation entries, New, Delete, Save, commands and related lists, and shows
"You don't have access" for a list or record they can't open. The data store enforces the same policy on every
query, save and delete, so it holds even when code calls the store directly (`StrataAccessDeniedException`).

- **Who the user is**: `ICurrentUser`. `fx.UseBlazorAuthenticationState()` (in `StrataFx.Blazor`) reads the user and
  their role claims from Blazor's `AuthenticationStateProvider`, so any ASP.NET Core authentication works
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
  `<RedirectToLogin />` when not authorized, and put `<StrataUserMenu />` (name and Log out) in the app bar. The
  `dotnet new stratafx` template sets all of this up.

## User layouts (the user stratum)

The columns button in a list's toolbar lets each user show or hide columns and reorder them (drag, or the arrows);
dragging a column header's right edge resizes it (double-click for automatic width). The sort and page size are
remembered too, and **Reset to default** drops the user's layout.

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

## Model designer

`StrataFx.Designer` adds an in-app designer for the model, for admins:

```csharp
builder.Services.AddStrataFx(fx =>
{
    // ...
    fx.UsePermissionPolicy(policy => policy.Role("Admin").AllowAll().AllowModelDesigner());
    fx.AddModelDesigner(); // optional: o => o.SaveTo = ModelDesignerSaveTarget.Database
});
// Router and MapRazorComponents: add StrataFxDesignerExtensions.PagesAssembly to the additional assemblies.
// App bar: <StrataDesignerLink /> (shown only to users who may use the designer).
```

- **The page** (`/strata/designer`): a tree of navigation groups, entities and properties (with a search box) and,
  for the selected node, every value a model layer can change: the same settings as model.json. Each value shows the
  layer it comes from ("Classes and attributes", "Sales defaults (SalesModule)", "model.json", "Database") and, when
  the designer's layer sets it, a reset to the value of the layer below. Values that can't be changed safely are
  disabled with the reason (the key's read-only flag, foreign keys, properties without a public setter, values set
  by a later layer). **Preview** shows the real list and form for the selected entity with the unsaved changes and
  records from the database, read-only. **Changes** lists the changed values and the exact text a save writes.
- **Where it saves**: in Development, the JSON layer file (the last `fx.AddJsonModelLayer` file, or
  `o.JsonFile`). Only the changed values are written: comments, formatting, key order and keys the designer doesn't
  manage stay as they were, new values are inserted in model order next to their siblings, and empty objects are
  removed. Everywhere else (where the app's files are usually read-only or replaced on deploy), the database layer:
  JSON in the user settings store (`IUserSettingsStore`, user `*`, key `strata/model`), so it needs
  `fx.AddEntityFrameworkCoreUserSettings<T>()` to survive restarts. Choose with `o.SaveTo`.
- **Applying changes**: a save writes the layer and rebuilds the model; everyone gets the new model right away (on
  their next navigation; menus and the title update at once). On other servers of a web farm the database layer is
  read at startup. A save that would not build is refused; if model.json was changed by someone else meanwhile, the
  save is refused rather than overwriting it.
- **Errors**: a model.json with an unknown name or invalid JSON (edited by hand) is shown in the designer, which
  can't save until it is fixed; **Reload** re-reads the layers. A database layer that no longer applies (e.g. a
  property was renamed in code) is set aside at startup with an error in the log and in the designer, and the next
  save replaces it.
- **Security**: only users with the model designer permission (`AllowModelDesigner()`) can open the page, read the
  layers or save, checked on the server. `AllowAll()` doesn't include it, and without a permission policy nobody
  has it.
- Users' own list layouts still apply on top of the model, so a column hidden in the designer stays visible for a
  user who chose to show it.

## Editors

Each property kind (text, number, date, enum, reference, collection, ...) has a default editor component. Replace
one for a kind or add a named editor with `AddStrataFxBlazor(e => e.Register<MyEditor>("name"))`, and pick it with
`[PropertyEditor("name")]` or `"editor": "name"` in model.json. Editors inherit `PropertyEditorBase`.

## Themes

Slim built-in themes (Fluent slim by default, plus Graphite, Indigo slate, Nordic and Zinc emerald), each with
light and dark palettes. Use `<StrataThemeProvider />` instead of `<MudThemeProvider />`, link
`_content/StrataFx.Blazor/strata.css`, and put `<StrataThemeSwitcher />` in the app bar so users can pick a theme
and mode (remembered in their browser). Configure with
`AddStrataFxBlazor(themes: t => { t.DefaultThemeId = StrataThemes.Nordic.Id; t.Themes.Add(StrataThemes.Create(...)); })`,
or set `t.AllowUserSelection = false` to give everyone the default.
