using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StrataFx.Data;
using StrataFx.EFCore;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Core.Tests;

public sealed class CrmContext(DbContextOptions<CrmContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>().HasOne(c => c.Company).WithMany(c => c.Contacts).HasForeignKey(c => c.CompanyId);
        modelBuilder.Entity<Contact>().HasOne(c => c.Referrer).WithMany().HasForeignKey(c => c.ReferrerId);
        modelBuilder.Entity<Contact>().Ignore(c => c.Computed);
    }
}

/// <summary>Runs the EF Core data store against an in-memory SQLite database.</summary>
public sealed class EfDataStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppModel _model = CollectionModelTests.Build();
    private DbContextOptions<CrmContext> _options = null!;

    private EntityModel Companies => _model.GetEntity(typeof(Company));
    private EntityModel Contacts => _model.GetEntity(typeof(Contact));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<CrmContext>().UseSqlite(_connection).Options;
        await using var db = new CrmContext(_options);
        await db.Database.EnsureCreatedAsync();

        var contoso = new Company { Name = "Contoso" };
        var fabrikam = new Company { Name = "Fabrikam" };
        db.AddRange(
            new Contact { FullName = "Ann Lee", Company = contoso },
            new Contact { FullName = "Bob Stone", Company = fabrikam, Referrer = contoso },
            new Contact { FullName = "Cara 100% Real", Company = fabrikam });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private EfDataStore<CrmContext> Store(IPermissionService? permissions = null) =>
        new(new Factory(_options), permissions ?? new AllowAllPermissionService(), new EntityValidator());

    private sealed class Factory(DbContextOptions<CrmContext> options) : IDbContextFactory<CrmContext>
    {
        public CrmContext CreateDbContext() => new(options);
    }

    private sealed class ReadOnlyPermissions : IPermissionService
    {
        public bool IsAllowed(Type entityType, EntityOperation operation) => operation == EntityOperation.Read;
    }

    [Fact]
    public async Task Query_pages_and_includes_references()
    {
        var page = await Store().QueryAsync(Contacts, new QueryRequest { Take = 2 });

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items.Cast<Contact>(), c => Assert.NotNull(c.Company));
    }

    [Fact]
    public async Task Query_searches_text_and_reference_lookup_text()
    {
        var byName = await Store().QueryAsync(Contacts, new QueryRequest { Search = "ann" });
        Assert.Equal("Ann Lee", Assert.Single(byName.Items.Cast<Contact>()).FullName);

        var byCompany = await Store().QueryAsync(Contacts, new QueryRequest { Search = "fabrik" });
        Assert.Equal(2, byCompany.TotalCount);
    }

    [Fact]
    public async Task Search_treats_wildcards_literally()
    {
        var result = await Store().QueryAsync(Contacts, new QueryRequest { Search = "100%" });
        Assert.Equal("Cara 100% Real", Assert.Single(result.Items.Cast<Contact>()).FullName);

        // "%" alone must match only the record that contains a literal percent sign, not everything.
        Assert.Equal(1, (await Store().QueryAsync(Contacts, new QueryRequest { Search = "%" })).TotalCount);
    }

    [Fact]
    public async Task Query_sorts_by_property_and_by_reference_text()
    {
        var desc = await Store().QueryAsync(Contacts, new QueryRequest { SortProperty = "FullName", SortDescending = true });
        Assert.Equal(["Cara 100% Real", "Bob Stone", "Ann Lee"], desc.Items.Cast<Contact>().Select(c => c.FullName));

        var byCompany = await Store().QueryAsync(Contacts, new QueryRequest { SortProperty = "Company" });
        Assert.Equal("Contoso", byCompany.Items.Cast<Contact>().First().Company.Name);
    }

    [Fact]
    public async Task Lookup_filters_on_lookup_text()
    {
        var result = await Store().LookupAsync(Companies, "fab");
        Assert.Equal("Fabrikam", Assert.Single(result.Cast<Company>()).Name);
    }

    [Fact]
    public async Task Insert_update_and_change_reference_without_touching_the_referenced_record()
    {
        var store = Store();
        var fabrikam = (Company)(await store.LookupAsync(Companies, "Fabrikam"))[0];
        fabrikam.Name = "Changed locally"; // must NOT be saved through the contact

        var contact = new Contact { FullName = "Dan New", Company = fabrikam };
        await store.SaveAsync(Contacts, contact, isNew: true);
        Assert.NotEqual(0, contact.Id);

        var loaded = (Contact)(await store.FindAsync(Contacts, contact.Id))!;
        Assert.Equal("Fabrikam", loaded.Company.Name);

        var contoso = (Company)(await store.LookupAsync(Companies, "Contoso"))[0];
        loaded.Company = contoso;
        loaded.FullName = "Dan Moved";
        await store.SaveAsync(Contacts, loaded, isNew: false);

        var reloaded = (Contact)(await store.FindAsync(Contacts, contact.Id))!;
        Assert.Equal("Dan Moved", reloaded.FullName);
        Assert.Equal(contoso.Id, reloaded.CompanyId);
    }

    [Fact]
    public async Task Clearing_an_optional_reference_clears_the_foreign_key()
    {
        var store = Store();
        var bob = (Contact)(await store.QueryAsync(Contacts, new QueryRequest { Search = "Bob" })).Items[0];
        Assert.NotNull(bob.ReferrerId);

        bob.Referrer = null;
        await store.SaveAsync(Contacts, bob, isNew: false);

        var reloaded = (Contact)(await store.FindAsync(Contacts, bob.Id))!;
        Assert.Null(reloaded.ReferrerId);
    }

    [Fact]
    public async Task Save_validates()
    {
        var ex = await Assert.ThrowsAsync<StrataValidationException>(() =>
            Store().SaveAsync(Contacts, new Contact { FullName = "No company" }, isNew: true));
        Assert.Contains(ex.Errors, e => e.PropertyName == "Company");
    }

    [Fact]
    public async Task Delete_removes_records()
    {
        var store = Store();
        var ann = (await store.QueryAsync(Contacts, new QueryRequest { Search = "Ann" })).Items;
        await store.DeleteAsync(Contacts, ann);

        Assert.Equal(2, (await store.QueryAsync(Contacts, new QueryRequest())).TotalCount);
    }

    [Fact]
    public async Task Permissions_are_enforced_by_the_store()
    {
        var store = Store(new ReadOnlyPermissions());
        Assert.Equal(3, (await store.QueryAsync(Contacts, new QueryRequest())).TotalCount);
        await Assert.ThrowsAsync<StrataAccessDeniedException>(() =>
            store.SaveAsync(Companies, new Company { Name = "X" }, isNew: true));
        await Assert.ThrowsAsync<StrataAccessDeniedException>(() =>
            store.DeleteAsync(Contacts, []));
    }
}
