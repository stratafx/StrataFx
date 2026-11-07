using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StrataFx.Blazor.Components;
using StrataFx.EFCore;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Core.Tests;

/// <summary>Friendly messages for unique indexes and blocked deletes, instead of database errors.</summary>
public sealed class EfConstraintTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppModel _model = CollectionModelTests.Build();
    private DbContextOptions<CrmContext> _options = null!;
    private int _invoiceId;

    private EntityModel Companies => _model.GetEntity(typeof(Company));
    private EntityModel Contacts => _model.GetEntity(typeof(Contact));
    private EntityModel Invoices => _model.GetEntity(typeof(Invoice));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<CrmContext>().UseSqlite(_connection).Options;
        await using var db = new CrmContext(_options);
        await db.Database.EnsureCreatedAsync();

        var contoso = new Company { Name = "Contoso" };
        var fabrikam = new Company { Name = "Fabrikam" };
        var northwind = new Company { Name = "Northwind" };
        var invoice = new Invoice
        {
            Number = "INV-1",
            Company = contoso,
            Lines =
            [
                new InvoiceLine { Description = "Laptop", Supplier = fabrikam },
                new InvoiceLine { Description = "Mouse", Supplier = fabrikam },
            ],
        };
        db.AddRange(
            new Contact { FullName = "Ann", Company = contoso },
            new Contact { FullName = "Bob", Company = fabrikam },
            invoice,
            new Invoice { Number = "INV-2", Company = contoso, Lines = [new InvoiceLine { Description = "Cable", Supplier = northwind }] });
        await db.SaveChangesAsync();
        _invoiceId = invoice.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private EfDataStore<CrmContext> Store(IInterceptor? interceptor = null)
    {
        var options = interceptor is null
            ? _options
            : new DbContextOptionsBuilder<CrmContext>().UseSqlite(_connection).AddInterceptors(interceptor).Options;
        return new(new Factory(options), new AllowAllPermissionService(), new EntityValidator(), [], _model);
    }

    private sealed class Factory(DbContextOptions<CrmContext> options) : IDbContextFactory<CrmContext>
    {
        public CrmContext CreateDbContext() => new(options);
    }

    /// <summary>Runs <paramref name="action"/> on another context just before the store saves: a user who was faster.</summary>
    private sealed class JustBeforeSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done) { _done = true; await action(); }
            return result;
        }
    }

    private async Task<Company> Company(string name)
    {
        await using var db = new CrmContext(_options);
        return await db.Companies.AsNoTracking().SingleAsync(c => c.Name == name);
    }

    private async Task<Invoice> LoadInvoice(int id) => (Invoice)(await Store().FindAsync(Invoices, id))!;

    private static async Task<ValidationError> SingleError(Task save) =>
        Assert.Single((await Assert.ThrowsAsync<StrataValidationException>(() => save)).Errors);

    // ---- unique values ----

    [Fact]
    public async Task Insert_with_a_used_value_is_reported_on_the_field()
    {
        var invoice = new Invoice { Number = "INV-1", Company = await Company("Fabrikam") };

        var error = await SingleError(Store().SaveAsync(Invoices, invoice, isNew: true));

        Assert.Equal(new ValidationError("Number", "Number INV-1 is already used."), error);
    }

    [Fact]
    public async Task Update_keeping_its_own_value_saves_but_taking_another_records_value_does_not()
    {
        var invoice = await LoadInvoice(_invoiceId);
        invoice.Total = 99;
        await Store().SaveAsync(Invoices, invoice, isNew: false); // INV-1 is its own number

        invoice.Number = "INV-2";
        var error = await SingleError(Store().SaveAsync(Invoices, invoice, isNew: false));
        Assert.Equal("Number INV-2 is already used.", error.Message);
    }

    [Fact]
    public async Task Composite_index_names_the_combination_and_uses_reference_captions()
    {
        var duplicate = new Contact { FullName = "Ann", Company = await Company("Contoso") };

        var error = await SingleError(Store().SaveAsync(Contacts, duplicate, isNew: true));
        Assert.Equal(new ValidationError("Company", "This combination of Company and Full Name is already used."), error);

        // The same name at another company is fine.
        await Store().SaveAsync(Contacts, new Contact { FullName = "Ann", Company = await Company("Fabrikam") }, isNew: true);
    }

    [Fact]
    public async Task Owned_items_are_checked_against_each_other()
    {
        var invoice = new Invoice
        {
            Number = "INV-3",
            Company = await Company("Contoso"),
            Lines = [new InvoiceLine { Description = "Desk" }, new InvoiceLine { Description = "Chair" }, new InvoiceLine { Description = "Desk" }],
        };

        var error = await SingleError(Store().SaveAsync(Invoices, invoice, isNew: true));

        Assert.Equal(new ValidationError("Lines", "Lines, row 3: Description Desk is already used in row 1."), error);
    }

    [Fact]
    public async Task Owned_items_of_another_parent_do_not_clash_and_replacing_an_item_is_not_a_duplicate()
    {
        // "Laptop" is used on INV-1, but the index is per invoice.
        var other = new Invoice { Number = "INV-3", Company = await Company("Contoso"), Lines = [new InvoiceLine { Description = "Laptop" }] };
        await Store().SaveAsync(Invoices, other, isNew: true);

        // Removing the stored "Mouse" and adding a new "Mouse" in the same save is allowed.
        var invoice = await LoadInvoice(_invoiceId);
        invoice.Lines.RemoveAll(l => l.Description == "Mouse");
        invoice.Lines.Add(new InvoiceLine { Description = "Mouse", Quantity = 3 });
        await Store().SaveAsync(Invoices, invoice, isNew: false);

        var saved = await LoadInvoice(_invoiceId);
        Assert.Equal(3, saved.Lines.Single(l => l.Description == "Mouse").Quantity);
    }

    [Fact]
    public async Task Renaming_an_owned_item_to_a_value_used_in_the_same_record_is_reported()
    {
        var invoice = await LoadInvoice(_invoiceId);
        invoice.Lines.Single(l => l.Description == "Mouse").Description = "Laptop";

        var error = await SingleError(Store().SaveAsync(Invoices, invoice, isNew: false));

        Assert.EndsWith("Description Laptop is already used in row 1.", error.Message);
    }

    // ---- deletes ----

    [Fact]
    public async Task Delete_of_a_record_still_in_use_names_the_count_and_the_records()
    {
        var fabrikam = await SingleError(Store().DeleteAsync(Companies, [await Company("Fabrikam")]));
        Assert.Equal(new ValidationError(null, "This company is used on 2 invoice lines and can't be deleted."), fabrikam);

        var northwind = await SingleError(Store().DeleteAsync(Companies, [await Company("Northwind")]));
        Assert.Equal("This company is used on 1 invoice line and can't be deleted.", northwind.Message);
    }

    [Fact]
    public async Task Delete_of_several_records_names_each_blocked_one_and_deletes_none()
    {
        List<object> companies = [await Company("Contoso"), await Company("Fabrikam"), await Company("Northwind")];

        var ex = await Assert.ThrowsAsync<StrataValidationException>(() => Store().DeleteAsync(Companies, companies));

        Assert.Equal(
            [
                "Fabrikam is used on 2 invoice lines and can't be deleted.",
                "Northwind is used on 1 invoice line and can't be deleted.",
                "Nothing was deleted.",
            ],
            ex.Errors.Select(e => e.Message));
        await using var db = new CrmContext(_options);
        Assert.Equal(3, await db.Companies.CountAsync()); // Contoso was free to go, but the delete is all or nothing
    }

    [Fact]
    public async Task Delete_impact_describes_what_cascades()
    {
        var contoso = await Company("Contoso");

        var impact = await Store().GetDeleteImpactAsync(Companies, [contoso]);
        Assert.Equal("This will also delete 1 contact and 2 invoices and their lines.", impact);
        Assert.Null(await Store().GetDeleteImpactAsync(Contacts, [new Contact { Id = 1 }]));

        await Store().DeleteAsync(Companies, [contoso]); // nothing blocks it
        await using var db = new CrmContext(_options);
        Assert.Equal(0, await db.Invoices.CountAsync());
        Assert.Equal(0, await db.InvoiceLines.CountAsync());
        Assert.Equal(["Bob"], await db.Contacts.Select(c => c.FullName).ToListAsync());
    }

    // ---- races and other database errors ----

    [Fact]
    public async Task Value_saved_by_someone_else_after_the_check_is_still_reported_on_the_field()
    {
        var store = Store(new JustBeforeSave(async () =>
        {
            await using var other = new CrmContext(_options);
            other.Invoices.Add(new Invoice { Number = "INV-9", CompanyId = (await Company("Fabrikam")).Id });
            await other.SaveChangesAsync();
        }));

        var error = await SingleError(store.SaveAsync(Invoices, new Invoice { Number = "INV-9", Company = await Company("Contoso") }, isNew: true));

        Assert.Equal(new ValidationError("Number", "Number INV-9 is already used."), error);
    }

    [Fact]
    public async Task Record_used_by_someone_else_after_the_check_still_explains_the_blocked_delete()
    {
        var litware = new Company { Name = "Litware" };
        await using (var db = new CrmContext(_options))
        {
            db.Add(litware);
            await db.SaveChangesAsync();
        }
        var store = Store(new JustBeforeSave(async () =>
        {
            await using var other = new CrmContext(_options);
            other.InvoiceLines.Add(new InvoiceLine { InvoiceId = _invoiceId, Description = "Pen", SupplierId = litware.Id });
            await other.SaveChangesAsync();
        }));

        var error = await SingleError(store.DeleteAsync(Companies, [litware]));

        Assert.Equal("This company is used on 1 invoice line and can't be deleted.", error.Message);
    }

    [Fact]
    public async Task Other_database_errors_keep_the_generic_message()
    {
        var store = Store(new JustBeforeSave(() => throw new DbUpdateException("SQLite Error 19: raw details users must not see")));

        var invoice = new Invoice { Number = "INV-5", Company = await Company("Contoso") };

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveAsync(Invoices, invoice, isNew: true));

        Assert.Equal("Something went wrong. Please try again.", ErrorText.For(ex));
    }
}
