using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StrataFx.Data;
using StrataFx.EFCore;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Core.Tests;

/// <summary>Saving owned collections, references without attaching, related-list filters and save handlers.</summary>
public sealed class EfCollectionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppModel _model = CollectionModelTests.Build();
    private DbContextOptions<CrmContext> _options = null!;
    private int _contosoId, _fabrikamId;

    private EntityModel Invoices => _model.GetEntity(typeof(Invoice));
    private EntityModel Contacts => _model.GetEntity(typeof(Contact));
    private EntityModel Companies => _model.GetEntity(typeof(Company));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<CrmContext>().UseSqlite(_connection).Options;
        await using var db = new CrmContext(_options);
        await db.Database.EnsureCreatedAsync();
        var contoso = new Company { Name = "Contoso" };
        var fabrikam = new Company { Name = "Fabrikam" };
        db.AddRange(contoso, fabrikam, new Contact { FullName = "Ann", Company = contoso });
        await db.SaveChangesAsync();
        (_contosoId, _fabrikamId) = (contoso.Id, fabrikam.Id);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private sealed class Factory(DbContextOptions<CrmContext> options) : IDbContextFactory<CrmContext>
    {
        public CrmContext CreateDbContext() => new(options);
    }

    private sealed class InvoiceTotals : IEntitySaveHandler
    {
        public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Invoice);
        public void BeforeSave(EntityModel entity, object instance, bool isNew) =>
            ((Invoice)instance).Total = ((Invoice)instance).Lines.Sum(l => l.Quantity * l.Price);
    }

    private EfDataStore<CrmContext> Store() =>
        new(new Factory(_options), new AllowAllPermissionService(), new EntityValidator(), [new InvoiceTotals()]);

    /// <summary>A fresh copy of a company, as the UI's lookup would load it.</summary>
    private async Task<Company> CompanyCopy(string name) => (Company)(await Store().LookupAsync(Companies, name))[0];

    private async Task<int> CompanyCount()
    {
        await using var db = new CrmContext(_options);
        return await db.Companies.CountAsync();
    }

    [Fact]
    public async Task Insert_saves_owned_items_and_never_duplicates_references()
    {
        var invoice = new Invoice
        {
            Number = "INV-1",
            Company = await CompanyCopy("Contoso"),
            Lines =
            [
                new InvoiceLine { Description = "Laptop", Quantity = 2, Price = 100, Supplier = await CompanyCopy("Fabrikam") },
                new InvoiceLine { Description = "Mouse", Quantity = 1, Price = 10, Supplier = await CompanyCopy("Fabrikam") }, // second copy of the same record
            ],
        };

        await Store().SaveAsync(Invoices, invoice, isNew: true);

        Assert.Equal(2, await CompanyCount());
        var saved = (Invoice)(await Store().FindAsync(Invoices, invoice.Id))!;
        Assert.Equal(210, saved.Total); // save handler ran
        Assert.Equal(["Laptop", "Mouse"], saved.Lines.OrderBy(l => l.Id).Select(l => l.Description));
        Assert.All(saved.Lines, l => Assert.Equal("Fabrikam", l.Supplier!.Name)); // item references are loaded
        Assert.Equal("Contoso", invoice.Company.Name); // the caller's references are restored after saving
    }

    [Fact]
    public async Task Update_changes_adds_and_removes_owned_items()
    {
        var invoice = new Invoice
        {
            Number = "INV-2",
            Company = await CompanyCopy("Contoso"),
            Lines = [new InvoiceLine { Description = "Keep", Price = 5 }, new InvoiceLine { Description = "Drop", Price = 7 }],
        };
        await Store().SaveAsync(Invoices, invoice, isNew: true);

        var edited = (Invoice)(await Store().FindAsync(Invoices, invoice.Id))!;
        var keep = edited.Lines.Single(l => l.Description == "Keep");
        keep.Quantity = 3;
        keep.Supplier = await CompanyCopy("Fabrikam");
        edited.Lines.Remove(edited.Lines.Single(l => l.Description == "Drop"));
        edited.Lines.Add(new InvoiceLine { Description = "New", Price = 1, Supplier = await CompanyCopy("Fabrikam") });
        edited.Company = await CompanyCopy("Fabrikam");
        await Store().SaveAsync(Invoices, edited, isNew: false);

        var reloaded = (Invoice)(await Store().FindAsync(Invoices, invoice.Id))!;
        Assert.Equal(_fabrikamId, reloaded.CompanyId);
        Assert.Equal(["Keep", "New"], reloaded.Lines.OrderBy(l => l.Id).Select(l => l.Description));
        Assert.Equal(3, reloaded.Lines.Single(l => l.Description == "Keep").Quantity);
        Assert.Equal(_fabrikamId, reloaded.Lines.Single(l => l.Description == "Keep").SupplierId);
        Assert.Equal(16, reloaded.Total);
        Assert.Equal(2, await CompanyCount());

        await using var db = new CrmContext(_options);
        Assert.Equal(2, await db.InvoiceLines.CountAsync()); // "Drop" was deleted, not orphaned
    }

    [Fact]
    public async Task Invalid_owned_item_blocks_the_save()
    {
        var invoice = new Invoice { Number = "INV-3", Company = await CompanyCopy("Contoso"), Lines = [new InvoiceLine { Description = "" }] };

        var ex = await Assert.ThrowsAsync<StrataValidationException>(() => Store().SaveAsync(Invoices, invoice, isNew: true));
        Assert.Contains(ex.Errors, e => e.PropertyName == "Lines");
    }

    [Fact]
    public async Task Two_copies_of_the_same_referenced_record_do_not_clash()
    {
        var contact = new Contact { FullName = "Bob", Company = await CompanyCopy("Fabrikam"), Referrer = await CompanyCopy("Fabrikam") };
        await Store().SaveAsync(Contacts, contact, isNew: true);

        var saved = (Contact)(await Store().FindAsync(Contacts, contact.Id))!;
        Assert.Equal(_fabrikamId, saved.CompanyId);
        Assert.Equal(_fabrikamId, saved.ReferrerId);
    }

    [Fact]
    public async Task Filters_list_a_parents_related_records()
    {
        await Store().SaveAsync(Contacts, new Contact { FullName = "Cara", Company = await CompanyCopy("Fabrikam") }, isNew: true);

        var ofContoso = await Store().QueryAsync(Contacts, new QueryRequest { Filters = [new PropertyFilter("Company", _contosoId)] });
        var ofFabrikam = await Store().QueryAsync(Contacts, new QueryRequest { Filters = [new PropertyFilter("Company", _fabrikamId)] });
        var withoutReferrer = await Store().QueryAsync(Contacts, new QueryRequest { Filters = [new PropertyFilter("Referrer", null)] });

        Assert.Equal("Ann", ((Contact)Assert.Single(ofContoso.Items)).FullName);
        Assert.Equal("Cara", ((Contact)Assert.Single(ofFabrikam.Items)).FullName);
        Assert.Equal(2, withoutReferrer.TotalCount);
    }

    [Fact]
    public async Task Updating_a_deleted_record_reports_it_instead_of_crashing()
    {
        var store = Store();
        var ann = (Contact)(await store.QueryAsync(Contacts, new QueryRequest())).Items[0];
        await store.DeleteAsync(Contacts, [ann]);

        var ex = await Assert.ThrowsAsync<StrataValidationException>(() => store.SaveAsync(Contacts, ann, isNew: false));
        Assert.Contains("no longer exists", Assert.Single(ex.Errors).Message);
    }
}
