using StrataFx.Model;

namespace StrataFx.Core.Tests;

public class ModelLayerTests
{
    [Fact]
    public void Json_layer_overrides_the_code_model()
    {
        const string json = """
        // comments are allowed
        {
          "title": "My CRM",
          "entities": {
            "contact": {
              "pluralCaption": "Contacts",
              "navigationGroup": "Sales",
              "properties": {
                "FullName": { "caption": "Name", "order": -1 },
                "CreditLimit": { "visibleInList": false, "displayFormat": "C" },
              }
            }
          }
        }
        """;

        var model = new AppModelBuilder().AddEntity<Company>().AddEntity<Contact>()
            .AddLayer(new JsonModelLayer("test", json))
            .Build();
        var contact = model.GetEntity(typeof(Contact));

        Assert.Equal("My CRM", model.Title);
        Assert.Equal("Contacts", contact.PluralCaption);
        Assert.Equal("Sales", contact.NavigationGroup);
        Assert.Equal("Name", contact.FindProperty("FullName")!.Caption);
        Assert.Equal("FullName", contact.ListColumns.First().Name);
        Assert.DoesNotContain(contact.ListColumns, p => p.Name == "CreditLimit");
        Assert.Equal("C", contact.FindProperty("CreditLimit")!.DisplayFormat);
    }

    [Fact]
    public void Later_layers_win()
    {
        var model = new AppModelBuilder().AddEntity<Company>()
            .AddLayer(new DelegateModelLayer("first", m => m.GetEntity(typeof(Company)).Caption = "First"))
            .AddLayer(new JsonModelLayer("second", """{ "entities": { "company": { "caption": "Second" } } }"""))
            .Build();

        Assert.Equal("Second", model.GetEntity(typeof(Company)).Caption);
    }

    [Theory]
    [InlineData("""{ "entities": { "nope": {} } }""", "unknown entity 'nope'")]
    [InlineData("""{ "entities": { "company": { "properties": { "Nope": {} } } } }""", "unknown property 'company.Nope'")]
    public void Json_layer_reports_typos(string json, string expected)
    {
        var builder = new AppModelBuilder().AddEntity<Company>().AddLayer(new JsonModelLayer("bad", json));
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Json_layer_cannot_make_the_key_editable()
    {
        var model = new AppModelBuilder().AddEntity<Company>()
            .AddLayer(new JsonModelLayer("t", """{ "entities": { "company": { "properties": { "Id": { "readOnly": false } } } } }"""))
            .Build();

        Assert.True(model.GetEntity(typeof(Company)).Key.ReadOnly);
    }
}
