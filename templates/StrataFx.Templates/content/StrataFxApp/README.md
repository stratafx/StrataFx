# StrataFxApp

A [StrataFx](https://github.com/stratafx/StrataFx) app: Blazor Server, SQLite, and pages generated from the
classes in `Models/Entities.cs`.

## Sign in (if the app was created with sign-in, the default)

No user is created until you configure one. The first user's name and password are read from configuration,
never from code. In development, keep them in [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets):

```bash
dotnet user-secrets set "AdminUser:UserName" "admin"
dotnet user-secrets set "AdminUser:Email" "admin@example.com"
dotnet user-secrets set "AdminUser:Password" "<choose a password>"
```

The password must meet the ASP.NET Core Identity defaults: at least 6 characters with an upper-case letter,
a lower-case letter, a digit and a symbol. The user gets the `Admin` role (set `AdminUser:Roles:0` to pick
another). The user is created on the next start if it doesn't exist yet; changing the secret later does not
change an existing user's password.

In production, set the same keys as environment variables (`AdminUser__UserName`, `AdminUser__Password`) or
in your secret store, start the app once, then remove the password.

## Run

```bash
dotnet run
```

The database (`app.db`) is created with a few example records on first start. It is created with
`EnsureCreated`, which does not change an existing database: after changing entities, delete `app.db`
(development only) or switch to EF Core migrations.

## Next steps

- Add an entity: a class with `[StrataEntity]` in `Models`, and a `DbSet` in `Data/AppDbContext.cs`.
- Change captions, visibility and navigation in `model.json`.
- Commands, rules, save handlers, roles: see the
  [getting started guide](https://github.com/stratafx/StrataFx/blob/main/docs/getting-started.md).
