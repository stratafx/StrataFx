using StrataFx.Model;

namespace StrataFx.Core.Tests;

public class AppModelBuilderTests
{
    private static AppModel Build() => new AppModelBuilder()
        .AddEntity<Company>()
        .AddEntity<Contact>()
        .Build();

    [Fact]
    public void Builds_entities_with_captions_ids_and_navigation()
    {
        var model = Build();
        var contact = model.GetEntity(typeof(Contact));

        Assert.Equal("contact", contact.Id);
        Assert.Equal("Contact", contact.Caption);
        Assert.Equal("People", contact.PluralCaption);
        Assert.Equal("Companies", model.GetEntity(typeof(Company)).PluralCaption);
        Assert.Same(contact, model.FindEntity("CONTACT"));
        Assert.Equal("Crm", Assert.Single(model.NavigationGroups()).Key);
    }

    [Fact]
    public void Detects_key_and_hides_generated_numeric_key()
    {
        var company = Build().GetEntity(typeof(Company));

        Assert.Equal("Id", company.Key.Name);
        Assert.True(company.Key.ReadOnly);
        Assert.False(company.Key.VisibleInList);
        Assert.False(company.Key.VisibleInDetail);
    }

    [Fact]
    public void Maps_property_types_to_editor_kinds()
    {
        var contact = Build().GetEntity(typeof(Contact));

        Assert.Equal(EditorKind.Text, contact.FindProperty("FullName")!.EditorKind);
        Assert.Equal(EditorKind.Reference, contact.FindProperty("Company")!.EditorKind);
        Assert.Equal(EditorKind.Enum, contact.FindProperty("Kind")!.EditorKind);
        Assert.Equal(EditorKind.Date, contact.FindProperty("BirthDate")!.EditorKind);
        Assert.Equal(EditorKind.Decimal, contact.FindProperty("CreditLimit")!.EditorKind);
        Assert.Equal(EditorKind.Boolean, contact.FindProperty("Active")!.EditorKind);
        Assert.Equal(EditorKind.MultilineText, Build().GetEntity(typeof(Company)).FindProperty("Remarks")!.EditorKind);
    }

    [Fact]
    public void Skips_collections_and_hides_foreign_keys()
    {
        var model = Build();
        var contact = model.GetEntity(typeof(Contact));

        Assert.Null(model.GetEntity(typeof(Company)).FindProperty("Contacts"));
        Assert.False(contact.FindProperty("CompanyId")!.VisibleInDetail);
        Assert.False(contact.FindProperty("ReferrerId")!.VisibleInList);
        Assert.Same(model.GetEntity(typeof(Company)), contact.FindProperty("Company")!.ReferenceEntity);
    }

    [Fact]
    public void Non_nullable_reference_is_required_and_nullable_is_optional()
    {
        var contact = Build().GetEntity(typeof(Contact));

        Assert.True(contact.FindProperty("Company")!.Required);
        Assert.False(contact.FindProperty("Referrer")!.Required);
    }

    [Fact]
    public void Reads_data_annotations()
    {
        var model = Build();
        var contact = model.GetEntity(typeof(Contact));
        var company = model.GetEntity(typeof(Company));

        var birthDate = contact.FindProperty("BirthDate")!;
        Assert.Equal("Date of birth", birthDate.Caption);
        Assert.Equal(1, birthDate.Order);
        Assert.Equal("BirthDate", contact.DetailItems.First().Name); // Order = 1 sorts first

        Assert.True(company.FindProperty("Name")!.Required);
        Assert.Equal(50, company.FindProperty("Name")!.MaxLength);
        Assert.Equal("N2", contact.FindProperty("CreditLimit")!.DisplayFormat);
        Assert.False(contact.FindProperty("Active")!.VisibleInList);
        Assert.True(contact.FindProperty("Active")!.VisibleInDetail);
        Assert.True(contact.FindProperty("Computed")!.ReadOnly);
    }

    [Fact]
    public void Picks_lookup_property_from_attribute_or_convention()
    {
        var model = Build();
        Assert.Equal("FullName", model.GetEntity(typeof(Contact)).LookupProperty!.Name);
        Assert.Equal("Name", model.GetEntity(typeof(Company)).LookupProperty!.Name);

        var company = new Company { Name = "Contoso" };
        Assert.Equal("Contoso", model.GetEntity(typeof(Company)).GetDisplayText(company));
        Assert.Equal("Contoso", model.GetEntity(typeof(Contact)).FindProperty("Company")!.FormatValue(company));
    }

    [Fact]
    public void Entity_without_key_is_rejected_with_a_helpful_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new AppModelBuilder().AddEntity<NoKeyEntity>().Build());
        Assert.Contains("[Key]", ex.Message);
    }

    [Fact]
    public void Parses_and_formats_keys()
    {
        var company = Build().GetEntity(typeof(Company));
        Assert.Equal(42, company.ParseKey("42"));
        Assert.Equal("7", company.FormatKey(new Company { Id = 7 }));
    }

    [Theory]
    [InlineData("OrderDate", "Order Date")]
    [InlineData("IsVIPCustomer", "Is VIP Customer")]
    [InlineData("Address2", "Address2")]
    [InlineData("first_name", "first name")]
    public void Splits_identifiers_into_words(string input, string expected) =>
        Assert.Equal(expected, Humanizer.SplitWords(input));

    [Theory]
    [InlineData("Company", "Companies")]
    [InlineData("Day", "Days")]
    [InlineData("Address", "Addresses")]
    [InlineData("Order", "Orders")]
    public void Pluralizes(string input, string expected) => Assert.Equal(expected, Humanizer.Pluralize(input));
}
