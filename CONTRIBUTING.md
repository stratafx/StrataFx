# Contributing to StrataFx

Thanks for helping. Bug reports, ideas and pull requests are welcome on
[GitHub](https://github.com/stratafx/StrataFx). For larger changes, open an issue first so we can agree on the
approach.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Any editor: Visual Studio 2026, Rider or VS Code with C# Dev Kit

## Repository layout

| Folder | What it contains |
|---|---|
| `src/` | The packages: `StrataFx.Core`, `StrataFx.EFCore`, `StrataFx.Blazor` (Razor class library), `StrataFx.Identity` (Razor class library). |
| `templates/StrataFx.Templates` | The `dotnet new stratafx` template. `content/StrataFxApp` is the template's project. |
| `samples/StrataFx.Sample` | A Blazor Server sales app that uses the projects directly. Not packaged. |
| `tests/StrataFx.Core.Tests` | xUnit tests, including EF Core tests on in-memory SQLite. Not packaged. |
| `docs/` | Getting started and concepts. |
| `Directory.Build.props` | Shared settings, package metadata and the **version** of all packages. |

## Build and test

```bash
dotnet build StrataFx.sln -c Release
dotnet test StrataFx.sln -c Release
```

Release builds treat warnings as errors, and CI runs both commands, so check them before you open a pull request.

## Run the sample

```bash
dotnet run --project samples/StrataFx.Sample
```

Open http://localhost:5037 and sign in with one of the test users from
`samples/StrataFx.Sample/appsettings.Development.json` (listed in the README). The SQLite database
(`strata-sample.db`) is created on first start; delete it after changing entities.

## Pack and try the template locally

```bash
dotnet pack StrataFx.sln -c Release -o artifacts
```

`artifacts/` is git-ignored. To try the template against your local packages, outside the repository:

1. Make a folder with a `nuget.config` that adds the `artifacts` folder as a package source (next to nuget.org).
2. `dotnet new install <repo>/artifacts/StrataFx.Templates.<version>.nupkg`
3. `dotnet new stratafx -n TryIt` (and `--auth false`), then `dotnet run` in the new folder.
4. `dotnet new uninstall StrataFx.Templates` when you're done.

Local packages have the same version as the next release, so NuGet caches them in your global packages folder.
Set `NUGET_PACKAGES` to a temporary folder while testing, or clear the `stratafx.*` folders in
`~/.nuget/packages` afterwards, so a later build doesn't pick up a stale copy.

Template files use the [template engine's](https://github.com/dotnet/templating/wiki) conditionals for the `auth`
option: `#if (auth)` in C#, `@*#if (auth)*@` in Razor and `<!--#if (auth) -->` in the project file. The template's
default StrataFx version is stamped in from `Directory.Build.props` when the package is built.

## Coding conventions

- C# `latest`, nullable reference types and implicit usings on; file-scoped namespaces; primary constructors
  where they keep things short. Follow the style of the surrounding code.
- `StrataFx.Core` has no UI or ORM dependencies. EF Core code goes in `StrataFx.EFCore`, MudBlazor and Blazor code
  in `StrataFx.Blazor`, ASP.NET Core Identity in `StrataFx.Identity`.
- Public types and members get XML doc comments that say what they are for; they ship in the packages for
  IntelliSense.
- Enforce rules in the data store, not only in the UI: permissions, validation and constraints must hold when code
  calls `IDataStore` directly.
- Messages users see are short, plain sentences built from captions ("Order # SO-1002 is already used."), never
  exception text or database errors.
- Add or update tests in `tests/StrataFx.Core.Tests` for behavior changes. EF Core tests use in-memory SQLite.
- Update `README.md`, `docs/` and the `Unreleased` section of `CHANGELOG.md` when you change behavior or public API.
- No secrets in the repository, including test passwords for anything but the sample's development-only users.

## Commits and pull requests

- One topic per pull request, with a short description of what changed and why.
- Commit messages in the imperative ("Add per-user list layouts").
- CI (build, test and pack) must pass.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
