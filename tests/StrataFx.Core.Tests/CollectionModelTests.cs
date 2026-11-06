using StrataFx.Model;
using StrataFx.Validation;

namespace StrataFx.Core.Tests;

public class CollectionModelTests
{
    internal static AppModel Build() => new AppModelBuilder()
        .AddEntity<Company>().AddEntity<Contact>()
        .AddEntity<Invoice>().AddEntity<InvoiceLine>()
        .Build();

    [Fact]
    public void Related_collection_with_named_inverse()
    {
        var model = Build();
        var contacts = model.GetEntity(typeof(Company)).FindProperty("Contacts")!;

        Assert.Equal(EditorKind.Collection, contacts.EditorKind);
        Assert.Equal(CollectionKind.Related, contacts.CollectionKind);
        Assert.Same(model.GetEntity(typeof(Contact)), contacts.ElementEntity);
        Assert.Equal("Company", contacts.InverseReference!.Name); // not Referrer
        Assert.False(contacts.VisibleInList);
        Assert.True(contacts.VisibleInDetail);
        Assert.True(model.GetEntity(typeof(Contact)).VisibleInNavigation);
    }

    [Fact]
    public void Owned_collection_finds_inverse_and_hides_items_from_navigation()
    {
        var model = Build();
        var lines = model.GetEntity(typeof(Invoice)).FindProperty("Lines")!;

        Assert.Equal(CollectionKind.Owned, lines.CollectionKind);
        Assert.Equal("Invoice", lines.InverseReference!.Name);
        Assert.False(model.GetEntity(typeof(InvoiceLine)).VisibleInNavigation);
        Assert.Single(model.GetEntity(typeof(Invoice)).OwnedCollections);
    }

    [Fact]
    public void Collection_helpers_add_remove_and_format()
    {
        var lines = Build().GetEntity(typeof(Invoice)).FindProperty("Lines")!;
        var invoice = new Invoice();
        var line = new InvoiceLine { Description = "A" };

        lines.AddItem(invoice, line);
        lines.AddItem(invoice, new InvoiceLine { Description = "B" });
        Assert.Equal(2, lines.GetItems(invoice).Count);
        Assert.Equal("2 items", lines.FormatValue(invoice.Lines));

        lines.RemoveItem(invoice, line);
        Assert.Equal("B", ((InvoiceLine)Assert.Single(lines.GetItems(invoice))).Description);
    }

    [Fact]
    public void Json_layer_can_change_collection_kind()
    {
        var model = new AppModelBuilder().AddEntity<Company>().AddEntity<Contact>()
            .AddLayer(new JsonModelLayer("t", """{ "entities": { "company": { "properties": { "Contacts": { "collectionKind": "owned" } } } } }"""))
            .Build();

        Assert.Equal(CollectionKind.Owned, model.GetEntity(typeof(Company)).FindProperty("Contacts")!.CollectionKind);
        Assert.False(model.GetEntity(typeof(Contact)).VisibleInNavigation);
    }

    [Fact]
    public void Owned_items_are_validated_with_the_parent_without_requiring_the_parent_reference()
    {
        var model = Build();
        var invoice = new Invoice
        {
            Number = "INV-1",
            Company = new Company { Name = "C" },
            Lines = [new InvoiceLine { Description = "ok" }, new InvoiceLine { Description = "", Quantity = 500 }],
        };

        var errors = new EntityValidator().Validate(model.GetEntity(typeof(Invoice)), invoice);

        Assert.All(errors, e => Assert.Equal("Lines", e.PropertyName));
        Assert.All(errors, e => Assert.StartsWith("Lines, row 2:", e.Message));
        Assert.Equal(2, errors.Count); // Description required + Quantity range; "Invoice is required" is not reported
    }
}
