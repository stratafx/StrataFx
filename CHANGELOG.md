# Changelog

All notable changes to StrataFx are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/). While the version is 0.x, minor versions may
contain breaking changes.

## [0.1.0] - Unreleased

The first public release: StrataFx.Core, StrataFx.EFCore, StrataFx.Blazor, StrataFx.Identity and StrataFx.Templates
on NuGet, for .NET 10.

### Application model

- Entities are plain C# classes with a key; `[StrataEntity]` adds them to the navigation with a group, order and
  icon. Captions, groups, formats, read-only state and validation come from data annotations and StrataFx
  attributes (`[Multiline]`, `[HideIn]`, `[LookupText]`, `[DisplayFormatString]`, `[PropertyEditor]`).
- References become lookups and their foreign keys are hidden. Collections are shown in the form, either owned
  (`[OwnedCollection]`: an editable grid saved with the parent in one transaction) or related (a filtered list of
  separate records; *New* pre-fills the parent).
- Model layers: code, modules (`DelegateModelLayer`), JSON (`model.json`, validated at startup) and the user stratum.
- Modules (`StrataModule`) bundle entities, layers, services, commands, rules, handlers and permissions.

### Behavior

- Commands (`StrataCommand<T>`): buttons in lists and forms with selection rules, confirmations and a required
  operation for permission checks.
- Rules (`IEntityRule`) and data-annotation validation, enforced in the form and in the data store.
- Save handlers (`IEntitySaveHandler`) that run before validation on every save.
- Change handlers (`EntityChangeHandler<T>`) that react to edits in a form before saving, including rows of owned
  collections.

### Data

- `IDataStore` on EF Core (`fx.UseEntityFrameworkCore<T>()`): paging, search, sorting, filters, lookups and saves of
  whole graphs with owned collections.
- Friendly messages for unique indexes ("Order # SO-1002 is already used.") and blocked deletes ("This product is
  used on 12 order lines and can't be deleted."), and delete confirmations that say what a cascade removes.

### UI

- MudBlazor-based generated list and detail pages, a property editor registry, `StrataNavMenu` and a command bar.
- A compact detail view, master-detail collections and an access-denied view.
- Slim themes (Fluent slim, Graphite, Indigo slate, Nordic, Zinc emerald) with light and dark palettes and a user
  theme switcher.
- Per-user list layouts (the user stratum): columns, order, widths, sort and page size per user, stored in the
  database, with **Reset to default** and an admin-only **Save as default for everyone**.

### Security

- Role-based permissions (`fx.UsePermissionPolicy`), enforced in the UI and in the data store.
- `ICurrentUser` from Blazor's authentication state, so any ASP.NET Core authentication works.
- StrataFx.Identity: ASP.NET Core Identity with sign-in, sign-out and access-denied pages, a user menu, circuit
  revalidation and a seeder for roles and users.

### Getting started

- `dotnet new stratafx` template (StrataFx.Templates): a Blazor Server app with SQLite, example entities,
  `model.json`, the StrataFx layout and optional sign-in (`--auth`, on by default) whose first user is read from
  configuration.
- Packages with Source Link, symbol packages (.snupkg), XML documentation and a package icon.
- Documentation: getting started guide and concepts.

[0.1.0]: https://github.com/stratafx/StrataFx/releases/tag/v0.1.0
