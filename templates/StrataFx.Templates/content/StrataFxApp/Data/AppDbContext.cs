#if (auth)
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
#endif
using Microsoft.EntityFrameworkCore;
using StrataFx.EFCore;
using StrataFxApp.Models;

namespace StrataFxApp.Data;

#if (auth)
/// <summary>The app's data and the ASP.NET Core Identity tables (users, roles) in one database.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<IdentityUser>(options)
#else
/// <summary>The app's data.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
#endif
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();

    // SQLite cannot sort or compare decimals, so store them as REAL. Remove this on SQL Server or PostgreSQL.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HaveConversion<double>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Company>().HasIndex(c => c.Name).IsUnique();
        modelBuilder.Entity<Contact>().HasOne(c => c.Company).WithMany(c => c.Contacts).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.AddStrataUserSettings(); // each user's list layouts (fx.AddEntityFrameworkCoreUserSettings)
    }

    /// <summary>
    /// Creates the database and adds a few example records the first time the app runs.
    /// EnsureCreated does not change an existing database: delete app.db after changing entities,
    /// or switch to EF Core migrations (see docs/getting-started.md).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        if (await db.Companies.AnyAsync()) return;

        var northwind = new Company { Name = "Northwind Traders", Website = "https://example.com/northwind", Industry = Industry.Retail };
        var fabrikam = new Company { Name = "Fabrikam", Website = "https://example.com/fabrikam", Industry = Industry.Manufacturing };
        var adatum = new Company { Name = "Adatum", Industry = Industry.Technology };
        db.AddRange(northwind, fabrikam, adatum);
        db.AddRange(
            new Contact { Name = "Maria Anders", Email = "maria@example.com", Company = northwind, LastContactedOn = DateTime.Today.AddDays(-3) },
            new Contact { Name = "Ana Trujillo", Email = "ana@example.com", Company = northwind },
            new Contact { Name = "Thomas Hardy", Email = "thomas@example.com", Phone = "+1 555 0100", Company = fabrikam },
            new Contact { Name = "Yoshi Tannamuri", Email = "yoshi@example.com", Company = adatum, LastContactedOn = DateTime.Today.AddDays(-10) });
        await db.SaveChangesAsync();
    }
}
