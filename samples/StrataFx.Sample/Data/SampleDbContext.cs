using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using StrataFx.EFCore;
using StrataFx.Sample.Sales;

namespace StrataFx.Sample.Data;

/// <summary>Business data and the ASP.NET Core Identity tables (users, roles) in one database.</summary>
public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    // SQLite cannot sort or compare decimals, so the sample stores them as REAL. Not needed on SQL Server or PostgreSQL.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HaveConversion<double>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity tables
        modelBuilder.Entity<Order>().HasIndex(o => o.Number).IsUnique();
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku).IsUnique();
        modelBuilder.Entity<Order>().HasOne(o => o.Customer).WithMany(c => c.Orders).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderLine>().HasOne(l => l.Order).WithMany(o => o.Lines).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderLine>().HasOne(l => l.Product).WithMany().OnDelete(DeleteBehavior.Restrict);
        modelBuilder.AddStrataUserSettings(); // per-user list layouts (fx.AddEntityFrameworkCoreUserSettings)
    }

    /// <summary>Creates the database and adds demo data the first time the sample runs.</summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SampleDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        if (await db.Customers.AnyAsync()) return;

        var cities = new[] { "Lahore", "Karachi", "Islamabad", "Dubai", "London", "Toronto" };
        var customers = Enumerable.Range(1, 40).Select(i => new Customer
        {
            Name = $"Customer {i:00}",
            Email = $"contact{i:00}@example.com",
            City = cities[i % cities.Length],
            IsVip = i % 7 == 0,
            CreatedOn = DateTime.Today.AddDays(-i * 9),
        }).ToList();

        var products = new List<Product>
        {
            new() { Name = "Laptop Pro 14", Sku = "HW-001", Category = ProductCategory.Hardware, Price = 1499m },
            new() { Name = "Wireless Mouse", Sku = "HW-002", Category = ProductCategory.Hardware, Price = 29.9m },
            new() { Name = "Office Suite", Sku = "SW-001", Category = ProductCategory.Software, Price = 249m },
            new() { Name = "Setup & Training", Sku = "SV-001", Category = ProductCategory.Service, Price = 400m },
            new() { Name = "Cloud Backup (1 year)", Sku = "SB-001", Category = ProductCategory.Subscription, Price = 99m },
            new() { Name = "Docking Station", Sku = "HW-003", Category = ProductCategory.Hardware, Price = 189m, InStock = false },
        };

        var statuses = Enum.GetValues<OrderStatus>();
        var orders = Enumerable.Range(1, 120).Select(i =>
        {
            var status = statuses[i % statuses.Length];
            var lines = Enumerable.Range(0, i % 3 + 1).Select(n =>
            {
                var product = products[(i + n * 2) % products.Count];
                return new OrderLine { Product = product, Quantity = (i + n) % 5 + 1, UnitPrice = product.Price };
            }).ToList();
            return new Order
            {
                Number = $"SO-{1000 + i}",
                Customer = customers[i % customers.Count],
                OrderDate = DateTime.Today.AddDays(-i),
                Status = status,
                ShippedOn = status == OrderStatus.Shipped ? DateTime.Today.AddDays(-i + 2) : null,
                Lines = lines,
                Total = lines.Sum(l => l.LineTotal),
            };
        }).ToList();

        db.AddRange(customers);
        db.AddRange(products);
        db.AddRange(orders);
        await db.SaveChangesAsync();
    }
}
