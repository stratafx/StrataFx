using Microsoft.Extensions.DependencyInjection;
using StrataFx.Changes;
using StrataFx.Hosting;
using StrataFx.Model;

namespace StrataFx.Core.Tests;

public class ChangeHandlerTests
{
    /// <summary>Records every change it sees, in order, across handlers.</summary>
    public sealed class ChangeLog
    {
        public List<string> Entries { get; } = [];
    }

    /// <summary>Copies the supplier's name into the description when the supplier changes.</summary>
    private sealed class LineSupplierHandler(ChangeLog log) : EntityChangeHandler<InvoiceLine>
    {
        protected override ValueTask OnChangedAsync(InvoiceLine line, PropertyChangeContext context)
        {
            log.Entries.Add($"line.{context.Property.Name} parent={context.ParentEntity?.ClrType.Name}");
            Assert.IsType<Invoice>(context.ParentInstance);
            if (context.Is(nameof(InvoiceLine.Supplier)) && line.Supplier is { } supplier)
                line.Description = supplier.Name;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InvoiceTotalHandler(ChangeLog log) : EntityChangeHandler<Invoice>
    {
        protected override ValueTask OnChangedAsync(Invoice invoice, PropertyChangeContext context)
        {
            log.Entries.Add($"invoice.{context.Property.Name} {context.Kind}");
            if (context.Is(nameof(Invoice.Lines)))
                invoice.Total = invoice.Lines.Sum(l => l.Quantity * l.Price);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An untyped handler that filters with AppliesTo.</summary>
    private sealed class CompanyOnlyHandler(ChangeLog log) : IEntityChangeHandler
    {
        public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Company);

        public ValueTask OnChangedAsync(PropertyChangeContext context)
        {
            log.Entries.Add($"company.{context.Property.Name}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : EntityChangeHandler<Company>
    {
        protected override ValueTask OnChangedAsync(Company instance, PropertyChangeContext context) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class TestModule : StrataModule
    {
        public override void ConfigureModel(AppModelBuilder model) =>
            model.AddEntity<Company>().AddEntity<Contact>().AddEntity<Invoice>().AddEntity<InvoiceLine>();

        public override void ConfigureServices(StrataFxBuilder fx)
        {
            fx.Services.AddScoped<ChangeLog>();
            fx.AddChangeHandler<LineSupplierHandler>();
            fx.AddChangeHandler<InvoiceTotalHandler>();
        }
    }

    private static IServiceScope Scope(Action<StrataFxBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddStrataFx(fx =>
        {
            fx.AddModule<TestModule>(); // registers through StrataModule.ConfigureServices
            configure?.Invoke(fx);
        });
        return services.BuildServiceProvider().CreateScope();
    }

    private static (EntityChangeDispatcher Dispatcher, AppModel Model, ChangeLog Log) Resolve(IServiceScope scope) => (
        scope.ServiceProvider.GetRequiredService<EntityChangeDispatcher>(),
        scope.ServiceProvider.GetRequiredService<AppModel>(),
        scope.ServiceProvider.GetRequiredService<ChangeLog>());

    [Fact]
    public async Task Typed_handler_runs_for_its_entity_only()
    {
        using var scope = Scope();
        var (dispatcher, model, log) = Resolve(scope);
        var invoice = model.GetEntity(typeof(Invoice));
        var company = model.GetEntity(typeof(Company));

        await dispatcher.PropertyChangedAsync(invoice, new Invoice(), invoice.FindProperty(nameof(Invoice.Number))!);
        await dispatcher.PropertyChangedAsync(company, new Company(), company.FindProperty(nameof(Company.Name))!);

        Assert.Equal(["invoice.Number Value"], log.Entries);
    }

    [Fact]
    public async Task AppliesTo_filters_untyped_handlers()
    {
        using var scope = Scope(fx => fx.AddChangeHandler<CompanyOnlyHandler>());
        var (dispatcher, model, log) = Resolve(scope);
        var company = model.GetEntity(typeof(Company));
        var contact = model.GetEntity(typeof(Contact));

        await dispatcher.PropertyChangedAsync(contact, new Contact(), contact.FindProperty(nameof(Contact.FullName))!);
        await dispatcher.PropertyChangedAsync(company, new Company(), company.FindProperty(nameof(Company.Website))!);

        Assert.Equal(["company.Website"], log.Entries);
    }

    [Fact]
    public async Task Owned_item_change_runs_item_handlers_then_the_parent_collection()
    {
        using var scope = Scope();
        var (dispatcher, model, log) = Resolve(scope);
        var lines = model.GetEntity(typeof(Invoice)).FindProperty(nameof(Invoice.Lines))!;
        var line = new InvoiceLine { Quantity = 2, Price = 5 };
        var invoice = new Invoice { Lines = [line] };

        line.Supplier = new Company { Id = 1, Name = "Acme" };
        await dispatcher.ItemChangedAsync(lines, invoice, line, lines.ElementEntity!.FindProperty(nameof(InvoiceLine.Supplier))!);

        Assert.Equal(["line.Supplier parent=Invoice", "invoice.Lines ItemChanged"], log.Entries);
        Assert.Equal("Acme", line.Description); // set by the item handler
        Assert.Equal(10, invoice.Total);        // recomputed by the parent handler
    }

    [Fact]
    public async Task Adding_and_removing_rows_raise_a_change_on_the_parent()
    {
        using var scope = Scope();
        var (dispatcher, model, log) = Resolve(scope);
        var lines = model.GetEntity(typeof(Invoice)).FindProperty(nameof(Invoice.Lines))!;
        var invoice = new Invoice();
        var line = new InvoiceLine { Quantity = 3, Price = 4 };

        lines.AddItem(invoice, line);
        await dispatcher.ItemAddedAsync(lines, invoice, line);
        Assert.Equal(12, invoice.Total);

        lines.RemoveItem(invoice, line);
        await dispatcher.ItemRemovedAsync(lines, invoice, line);
        Assert.Equal(0, invoice.Total);

        Assert.Equal(["invoice.Lines ItemAdded", "invoice.Lines ItemRemoved"], log.Entries); // no item handlers run
    }

    [Fact]
    public async Task Handler_exceptions_reach_the_caller()
    {
        using var scope = Scope(fx => fx.AddChangeHandler<ThrowingHandler>());
        var (dispatcher, model, _) = Resolve(scope);
        var company = model.GetEntity(typeof(Company));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.PropertyChangedAsync(company, new Company(), company.FindProperty(nameof(Company.Name))!));
    }

    [Fact]
    public void ValueEquals_compares_references_by_key()
    {
        var model = CollectionModelTests.Build();
        var supplier = model.GetEntity(typeof(InvoiceLine)).FindProperty(nameof(InvoiceLine.Supplier))!;
        var quantity = model.GetEntity(typeof(InvoiceLine)).FindProperty(nameof(InvoiceLine.Quantity))!;

        Assert.True(supplier.ValueEquals(new Company { Id = 1 }, new Company { Id = 1, Name = "other copy" }));
        Assert.False(supplier.ValueEquals(new Company { Id = 1 }, new Company { Id = 2 }));
        Assert.False(supplier.ValueEquals(new Company(), new Company())); // unsaved: only the same object is equal
        Assert.False(supplier.ValueEquals(null, new Company { Id = 1 }));
        Assert.True(quantity.ValueEquals(3, 3));
        Assert.False(quantity.ValueEquals(3, 4));
    }
}
