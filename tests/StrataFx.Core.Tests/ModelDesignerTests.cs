using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StrataFx.Designer;
using StrataFx.EFCore;
using StrataFx.Hosting;
using StrataFx.Model;
using StrataFx.Model.Design;
using StrataFx.Security;
using StrataFx.Settings;

namespace StrataFx.Core.Tests;

/// <summary>Shared helpers for the model designer tests.</summary>
public abstract class DesignerTestBase : IDisposable
{
    protected readonly string Folder = Directory.CreateTempSubdirectory("strata-designer-").FullName;

    protected static AppModelBuilder Builder() =>
        new AppModelBuilder().AddEntity<Company>().AddEntity<Contact>().AddEntity<Invoice>().AddEntity<InvoiceLine>();

    protected string WriteFile(string name, string text)
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    protected sealed class TestPermissions(bool designer) : IPermissionService
    {
        public bool Designer { get; set; } = designer;
        public bool IsAllowed(Type entityType, EntityOperation operation) => true;
        public bool HasPermission(string permission) => Designer && permission == StrataPermissions.ModelDesigner;
    }

    protected static ModelValueKey Key<T>(string setting) where T : class => new(typeof(T).Name.ToLowerInvariant(), null, setting);
    protected static ModelValueKey Key<T>(string property, string setting) where T : class => new(typeof(T).Name.ToLowerInvariant(), property, setting);

    protected virtual void Cleanup() { }

    public void Dispose()
    {
        Cleanup();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}

public class ModelProvenanceTests : DesignerTestBase
{
    private sealed class CrmModule : StrataModule
    {
        public override void ConfigureModel(AppModelBuilder model)
        {
            model.AddEntity<Company>().AddEntity<Contact>();
            model.AddLayer(new DelegateModelLayer("Crm defaults", m => m.GetEntity(typeof(Company)).Caption = "Firm"));
        }
    }

    [Fact]
    public void Each_value_records_the_layer_that_set_it()
    {
        var services = new ServiceCollection();
        services.AddStrataFx(fx =>
        {
            fx.AddModule<CrmModule>();
            fx.AddJsonModelLayer(WriteFile("first.json", """{ "entities": { "company": { "caption": "Organisation", "pluralCaption": "Organisations" } } }"""));
            fx.AddJsonModelLayer(WriteFile("second.json", """{ "entities": { "contact": { "properties": { "FullName": { "caption": "Name" } } } } }"""));
        });
        var models = services.BuildServiceProvider().GetRequiredService<IAppModelProvider>();

        var provenance = new ModelProvenance();
        models.Build(provenance: provenance);

        Assert.Equal([ModelProvenance.CodeLayer, "Crm defaults (CrmModule)", "first.json", "second.json"], provenance.Layers);
        Assert.Equal("first.json", provenance.SourceOf(Key<Company>("caption")));
        Assert.Equal(["Company", "Firm", "Organisation"], provenance.History(Key<Company>("caption")).Select(s => s.Value));
        Assert.Equal("Firm", provenance.Before(Key<Company>("caption"), 2)!.Value.Value);       // below first.json: the module
        Assert.Equal("second.json", provenance.SourceOf(Key<Contact>("FullName", "caption")));
        Assert.Equal(ModelProvenance.CodeLayer, provenance.SourceOf(Key<Contact>("FullName", "visibleInList")));
        Assert.Equal("Crm", provenance.Current(Key<Company>("navigationGroup"))!.Value.Value); // from [StrataEntity]
    }

    [Fact]
    public void A_layer_that_repeats_a_value_does_not_become_its_source()
    {
        var provenance = new ModelProvenance();
        Builder().Build([new JsonModelLayer("same", """{ "entities": { "company": { "caption": "Company" } } }""")], provenance);

        Assert.Equal(ModelProvenance.CodeLayer, provenance.SourceOf(Key<Company>("caption")));
    }

    [Fact]
    public void Normal_builds_track_nothing_and_return_new_models()
    {
        var builder = Builder();
        Assert.NotSame(builder.Build(), builder.Build());
    }
}

public class JsonLayerTextTests : DesignerTestBase
{
    private static readonly AppModel Model = Builder().Build();

    private const string Original = """
        // Overrides for the CRM. Keep this comment.
        {
          "$schema": "./model.schema.json",
          "title": "CRM",
          "entities": {
            "company": {
              "pluralCaption": "Firms", // shown in the menu
              "properties": {
                "Website": { "visibleInList": false },
                /* the remarks are long */
                "Remarks": {
                  "caption": "Notes",
                  "lines": 6
                }
              }
            },
            "invoice": {
              "navigationGroup": "Money",
            }
          },
          "custom": { "keep": [1, 2, 3] }
        }
        """;

    private static string Apply(string? text, params (ModelValueKey Key, object? Value, bool Set)[] edits)
    {
        var before = ModelLayerDocument.Parse(text, Model);
        var after = before.Clone();
        foreach (var (key, value, set) in edits)
        {
            if (set) after.Set(key, value);
            else after.Remove(key);
        }
        return JsonLayerText.Apply(text, ModelLayerDocument.Diff(before, after, Model), Model);
    }

    [Fact]
    public void Load_edit_save_reload_gives_the_same_model_and_keeps_everything_else()
    {
        var saved = ModelLayerDocument.Parse(Original, Model);
        var draft = saved.Clone();
        draft.Set(ModelValueKey.ForApp("title"), "Contoso CRM");                     // change at the root
        draft.Set(Key<Company>("pluralCaption"), "Organisations");                   // change next to a comment
        draft.Set(Key<Company>("Website", "caption"), "Web site");                   // add to a one-line object
        draft.Remove(Key<Company>("Remarks", "lines"));                              // remove from a multi-line object
        draft.Set(Key<Contact>("FullName", "caption"), "Name");                      // a new entity
        draft.Set(Key<Invoice>("Total", "displayFormat"), "C");                      // a new properties object
        draft.Remove(Key<Invoice>("navigationGroup"));                               // leaves an empty entity object

        var text = JsonLayerText.Apply(Original, ModelLayerDocument.Diff(saved, draft, Model), Model);
        var reloaded = ModelLayerDocument.Parse(text, Model);

        // Reloading gives the same values and the same model as the draft.
        Assert.Equal(draft.Values.OrderBy(v => v.Key.ToString()), reloaded.Values.OrderBy(v => v.Key.ToString()));
        Assert.Equal(
            ModelSettings.Snapshot(Builder().Build([draft.ToLayer("draft", Model)])),
            ModelSettings.Snapshot(Builder().Build([new JsonModelLayer("file", text)])));

        // Only the edited spots changed: comments, unknown keys, formatting and order are untouched.
        Assert.Equal("""
            // Overrides for the CRM. Keep this comment.
            {
              "$schema": "./model.schema.json",
              "title": "Contoso CRM",
              "entities": {
                "company": {
                  "pluralCaption": "Organisations", // shown in the menu
                  "properties": {
                    "Website": { "caption": "Web site", "visibleInList": false },
                    /* the remarks are long */
                    "Remarks": {
                      "caption": "Notes"
                    }
                  }
                },
                "contact": {
                  "properties": {
                    "FullName": { "caption": "Name" }
                  }
                },
                "invoice": {
                  "properties": {
                    "Total": { "displayFormat": "C" }
                  }
                }
              },
              "custom": { "keep": [1, 2, 3] }
            }
            """.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Changing_one_value_changes_one_line()
    {
        var text = Apply(Original, (Key<Company>("Remarks", "caption"), "Comments", true));

        var changed = TextDiff.Lines(Original, text).Where(l => l.Kind != TextDiffKind.Same).ToList();
        Assert.Equal(2, changed.Count); // one line out, one line in
        Assert.Equal(changed.Single(l => l.Kind == TextDiffKind.Removed).Text.Replace("Notes", "Comments"),
            changed.Single(l => l.Kind == TextDiffKind.Added).Text);
    }

    [Fact]
    public void New_values_go_in_model_and_setting_order_with_the_siblings_indentation()
    {
        var text = Apply(Original,
            (Key<Company>("caption"), "Firm", true),                 // before pluralCaption
            (Key<Company>("Name", "caption"), "Firm name", true),    // Name comes before Website in the class
            (Key<Contact>("caption"), "Person", true));              // contact comes between company and invoice

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        int Line(string fragment) => Array.FindIndex(lines, l => l.Contains(fragment, StringComparison.Ordinal));

        Assert.Equal("      \"caption\": \"Firm\",", lines[Line("\"Firm\"")]);
        Assert.True(Line("\"Firm\"") < Line("pluralCaption"));
        Assert.True(Line("Firm name") < Line("\"Website\""));
        Assert.True(Line("\"company\"") < Line("\"contact\"") && Line("\"contact\"") < Line("\"invoice\""));
    }

    [Fact]
    public void Removing_keeps_comments_above_and_fixes_commas()
    {
        const string text = """
            {
              "entities": {
                "company": {
                  // why Firms
                  "pluralCaption": "Firms",
                  "order": 3 // last
                }
              }
            }
            """;

        var result = Apply(text, (Key<Company>("order"), null, false));

        Assert.Equal("""
            {
              "entities": {
                "company": {
                  // why Firms
                  "pluralCaption": "Firms"
                }
              }
            }
            """.ReplaceLineEndings("\n"), result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_new_file_is_created_sorted()
    {
        var text = Apply(null, (Key<Contact>("FullName", "caption"), "Name", true), (ModelValueKey.ForApp("title"), "CRM", true));

        Assert.Equal("""
            {
              "title": "CRM",
              "entities": {
                "contact": {
                  "properties": {
                    "FullName": { "caption": "Name" }
                  }
                }
              }
            }

            """.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Removing_the_last_value_removes_empty_objects()
    {
        var text = Apply(Original, (Key<Company>("Website", "visibleInList"), null, false));

        Assert.DoesNotContain("Website", text);
        Assert.Contains("/* the remarks are long */", text);
    }

    [Fact]
    public void Line_endings_are_kept()
    {
        var crlf = Original.ReplaceLineEndings("\r\n");
        var text = Apply(crlf, (Key<Contact>("caption"), "Person", true));

        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }
}

public class AppModelProviderTests : DesignerTestBase
{
    [Fact]
    public void Rebuild_publishes_a_new_model_and_leaves_the_old_one_alone()
    {
        var path = WriteFile("model.json", """{ "entities": { "company": { "caption": "Firm" } } }""");
        var provider = new AppModelProvider(Builder().AddLayer(new JsonFileModelLayer(path)));
        var old = provider.Current;
        AppModel? published = null;
        provider.Changed += m => published = m;

        File.WriteAllText(path, """{ "entities": { "company": { "caption": "Organisation" } } }""");
        Assert.Null(provider.Rebuild());

        Assert.NotSame(old, provider.Current);
        Assert.Same(provider.Current, published);
        Assert.Equal("Organisation", provider.Current.GetEntity(typeof(Company)).Caption);
        Assert.Equal("Firm", old.GetEntity(typeof(Company)).Caption);
    }

    [Theory]
    [InlineData("""{ "entities": { "compnay": { "caption": "Typo" } } }""", "unknown entity 'compnay'")]
    [InlineData("""{ "entities": { "company": { "caption": "Firm" """, "not valid JSON")]
    public void A_broken_layer_keeps_the_last_good_model(string broken, string expected)
    {
        var path = WriteFile("model.json", """{ "entities": { "company": { "caption": "Firm" } } }""");
        var provider = new AppModelProvider(Builder().AddLayer(new JsonFileModelLayer(path)));
        var good = provider.Current;
        var changed = false;
        provider.Changed += _ => changed = true;

        File.WriteAllText(path, broken);
        var error = provider.Rebuild();

        Assert.NotNull(error);
        Assert.Contains(expected, error.Message);
        Assert.Equal("model.json", error.Layer);
        Assert.Same(good, provider.Current);
        Assert.Same(error, provider.LastError);
        Assert.False(changed);

        File.WriteAllText(path, """{ "entities": { "company": { "caption": "Fixed" } } }""");
        Assert.Null(provider.Rebuild());
        Assert.Null(provider.LastError);
        Assert.Equal("Fixed", provider.Current.GetEntity(typeof(Company)).Caption);
    }

    [Fact]
    public void A_broken_layer_at_startup_stops_the_app()
    {
        var path = WriteFile("model.json", """{ "entities": { "nope": {} } }""");
        var ex = Assert.Throws<ModelLayerException>(() => new AppModelProvider(Builder().AddLayer(new JsonFileModelLayer(path))));
        Assert.Equal("model.json", ex.LayerName);
    }

    [Fact]
    public void Injected_AppModel_is_the_current_model()
    {
        var path = WriteFile("model.json", """{ "title": "One" }""");
        var services = new ServiceCollection();
        services.AddStrataFx(fx => { fx.AddEntity<Company>(); fx.AddJsonModelLayer(path); });
        var sp = services.BuildServiceProvider();

        Assert.Equal("One", sp.GetRequiredService<AppModel>().Title);
        File.WriteAllText(path, """{ "title": "Two" }""");
        sp.GetRequiredService<IAppModelProvider>().Rebuild();
        Assert.Equal("Two", sp.GetRequiredService<AppModel>().Title);
    }
}

public class ModelDesignSessionTests : DesignerTestBase
{
    private (AppModelProvider Provider, JsonFileDesignTarget Target, TestPermissions Permissions) FileSetup(string json, DatabaseModelLayer? database = null)
    {
        var layer = new JsonFileModelLayer(WriteFile("model.json", json));
        var builder = Builder().AddLayer(new DelegateModelLayer("Code tweaks", m => m.GetEntity(typeof(Contact)).FindProperty("Kind")!.Caption = "Type"))
            .AddLayer(layer);
        var provider = new AppModelProvider(builder, database is null ? null : [database]);
        return (provider, new JsonFileDesignTarget(layer), new TestPermissions(true));
    }

    [Fact]
    public async Task Values_show_their_source_and_reset_to_the_layer_below()
    {
        var (provider, target, permissions) = FileSetup("""{ "entities": { "contact": { "properties": { "Kind": { "caption": "Category" } } } } }""");
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();
        var kind = Key<Contact>("Kind", "caption");

        var info = session.Describe(kind);
        Assert.Equal("Category", info.Value);
        Assert.Equal("model.json", info.Source);
        Assert.True(info.IsSetInTarget);
        Assert.Equal("Type", info.BelowValue);
        Assert.Equal("Code tweaks", info.BelowSource);

        session.Reset(kind);
        Assert.Equal("Type", session.Describe(kind).Value);
        Assert.Equal("Code tweaks", session.Describe(kind).Source);
        Assert.Equal("Type", session.Model.GetEntity(typeof(Contact)).FindProperty("Kind")!.Caption); // the preview model
        Assert.Equal("Category", provider.Current.GetEntity(typeof(Contact)).FindProperty("Kind")!.Caption); // not published
        Assert.True(session.Changes.Single().IsRemoved);
    }

    [Fact]
    public async Task Setting_the_value_from_below_keeps_the_layer_minimal()
    {
        var (provider, target, permissions) = FileSetup("{}");
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();

        session.Set(Key<Company>("caption"), "Firm");
        Assert.True(session.Describe(Key<Company>("caption")).IsSetInTarget);
        session.Set(Key<Company>("caption"), "Company");
        Assert.False(session.Describe(Key<Company>("caption")).IsSetInTarget);
        Assert.False(session.HasChanges);
    }

    [Fact]
    public async Task Unsafe_values_are_refused_with_a_reason()
    {
        var (provider, target, permissions) = FileSetup("{}");
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();

        Assert.Equal("The key is never editable.", session.Describe(Key<Company>("Id", "readOnly")).DisabledReason);
        Assert.NotNull(session.Set(Key<Company>("Id", "readOnly"), false));
        Assert.Contains("public setter", session.Describe(Key<Contact>("Computed", "readOnly")).DisabledReason);
        Assert.Contains("Company reference", session.Describe(Key<Contact>("CompanyId", "visibleInDetail")).DisabledReason);
        Assert.Equal("Caption can't be empty.", session.Set(Key<Company>("caption"), " "));
        Assert.False(session.HasChanges);
    }

    [Fact]
    public async Task Save_writes_the_file_and_publishes_the_model()
    {
        var (provider, target, permissions) = FileSetup("// keep me\n{\n  \"title\": \"CRM\"\n}\n");
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();
        session.Set(Key<Company>("pluralCaption"), "Firms");
        session.Set(Key<Contact>("Kind", "visibleInList"), false);

        var result = await session.SaveAsync();

        Assert.True(result.Saved, result.Error);
        var text = await File.ReadAllTextAsync(((JsonFileModelLayer)target.Layer).Path);
        Assert.StartsWith("// keep me", text);
        Assert.Equal("Firms", provider.Current.GetEntity(typeof(Company)).PluralCaption);
        Assert.False(provider.Current.GetEntity(typeof(Contact)).FindProperty("Kind")!.VisibleInList);
        Assert.False(session.HasChanges);
    }

    [Fact]
    public async Task Save_refuses_when_the_file_changed_meanwhile()
    {
        var (provider, target, permissions) = FileSetup("{}");
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();
        session.Set(Key<Company>("caption"), "Firm");
        await File.WriteAllTextAsync(((JsonFileModelLayer)target.Layer).Path, """{ "title": "Someone else" }""");

        var result = await session.SaveAsync();

        Assert.False(result.Saved);
        Assert.Contains("changed after the designer read it", result.Error);
        Assert.Equal("Company", provider.Current.GetEntity(typeof(Company)).Caption);
    }

    [Fact]
    public async Task A_file_with_typos_shows_them_and_cannot_be_saved()
    {
        var (provider, target, permissions) = FileSetup("{}");
        await File.WriteAllTextAsync(((JsonFileModelLayer)target.Layer).Path, """{ "entities": { "company": { "properties": { "Webiste": { "caption": "x" } } } } }""");
        Assert.NotNull(provider.Rebuild()); // the app keeps the last good model

        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();
        session.Set(Key<Company>("caption"), "Firm");

        Assert.Contains("Unknown property 'company.Webiste'.", session.Problems);
        Assert.False(session.CanSave);
        Assert.False((await session.SaveAsync()).Saved);
    }

    [Fact]
    public async Task Values_set_by_a_later_layer_cannot_be_edited_in_an_earlier_one()
    {
        var database = new DatabaseModelLayer();
        database.SetContent("""{ "entities": { "company": { "caption": "From the database" } } }""");
        var (provider, target, permissions) = FileSetup("{}", database);
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();

        var info = session.Describe(Key<Company>("caption"));
        Assert.Equal("Database", info.Source);
        Assert.Contains("applied after model.json", info.DisabledReason);
    }
}

public class DatabaseModelLayerTests : DesignerTestBase
{
    private sealed class SettingsContext(DbContextOptions<SettingsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddStrataUserSettings();
    }

    private sealed class Factory(DbContextOptions<SettingsContext> options) : IDbContextFactory<SettingsContext>
    {
        public SettingsContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private IServiceProvider Services(string jsonPath, out DatabaseModelLayer layer)
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<SettingsContext>().UseSqlite(_connection).Options;
        using (var db = new SettingsContext(options)) db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<SettingsContext>>(new Factory(options));
        services.AddStrataFx(fx =>
        {
            fx.AddEntity<Company>().AddEntity<Contact>();
            fx.AddJsonModelLayer(jsonPath);
            fx.AddEntityFrameworkCoreUserSettings<SettingsContext>();
            fx.AddDatabaseModelLayer();
        });
        var sp = services.BuildServiceProvider();
        layer = sp.GetRequiredService<DatabaseModelLayer>();
        return sp;
    }

    [Fact]
    public async Task Saved_changes_are_stored_reloaded_and_win_over_json_but_not_over_user_layouts()
    {
        var json = WriteFile("model.json", """{ "entities": { "contact": { "pluralCaption": "Contacts", "properties": { "Kind": { "visibleInList": true } } } } }""");
        var sp = Services(json, out var layer);
        var provider = sp.GetRequiredService<IAppModelProvider>();
        var target = new DatabaseDesignTarget(layer, sp.GetRequiredService<IServiceScopeFactory>());

        var session = await new ModelDesigner(provider, target, new TestPermissions(true)).OpenAsync();
        Assert.Equal("model.json", session.Describe(Key<Contact>("pluralCaption")).Source);
        session.Set(Key<Contact>("pluralCaption"), "Customers");
        session.Set(Key<Contact>("Kind", "visibleInList"), false);
        Assert.True((await session.SaveAsync()).Saved);

        // Stored as sorted JSON with the user settings.
        await using (var scope = sp.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IUserSettingsStore>().GetAsync(UserSettings.SharedUserName, DatabaseModelLayer.SettingKey);
            Assert.Contains("\"pluralCaption\": \"Customers\"", stored);
        }

        // A restart: a new layer loads it from the store, and it runs after model.json.
        var restarted = new DatabaseModelLayer();
        await using (var scope = sp.CreateAsyncScope())
            await restarted.LoadAsync(scope.ServiceProvider.GetRequiredService<IUserSettingsStore>());
        var model = new AppModelBuilder().AddEntity<Company>().AddEntity<Contact>()
            .AddLayer(new JsonFileModelLayer(json)).AddLayer(restarted).Build();
        var contact = model.GetEntity(typeof(Contact));
        Assert.Equal("Customers", contact.PluralCaption);
        Assert.False(contact.FindProperty("Kind")!.VisibleInList);

        // Each user's own columns still apply on top.
        var mine = new ListLayout { Columns = [new ListLayoutColumn { Name = "Kind", Visible = true, Order = 0 }] };
        Assert.Contains("Kind", ListLayouts.Resolve(contact, mine).VisibleColumns.Select(c => c.Name));
        Assert.DoesNotContain("Kind", ListLayouts.Resolve(contact, null).VisibleColumns.Select(c => c.Name));
    }

    [Fact]
    public async Task Resetting_everything_removes_the_stored_layer()
    {
        var sp = Services(WriteFile("model.json", "{}"), out var layer);
        var provider = sp.GetRequiredService<IAppModelProvider>();
        var target = new DatabaseDesignTarget(layer, sp.GetRequiredService<IServiceScopeFactory>());
        var designer = new ModelDesigner(provider, target, new TestPermissions(true));

        var session = await designer.OpenAsync();
        session.Set(Key<Company>("caption"), "Firm");
        await session.SaveAsync();
        session.Reset(Key<Company>("caption"));
        await session.SaveAsync();

        Assert.Null(await target.ReadAsync());
        Assert.Equal("Company", provider.Current.GetEntity(typeof(Company)).Caption);
    }

    [Fact]
    public async Task A_stored_layer_that_no_longer_applies_is_set_aside_and_replaced_on_save()
    {
        var sp = Services(WriteFile("model.json", "{}"), out var layer);
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUserSettingsStore>().SaveAsync(UserSettings.SharedUserName, DatabaseModelLayer.SettingKey,
                """{ "entities": { "company": { "caption": "Firm", "properties": { "Renamed": { "caption": "x" } } } } }""");
        var provider = sp.GetRequiredService<IAppModelProvider>();
        var target = new DatabaseDesignTarget(layer, sp.GetRequiredService<IServiceScopeFactory>());

        var session = await new ModelDesigner(provider, target, new TestPermissions(true)).OpenAsync();

        Assert.Null(provider.LastError);                 // the app runs without the stored layer
        Assert.Equal("Company", provider.Current.GetEntity(typeof(Company)).Caption);
        Assert.Contains(session.Problems, p => p.Contains("company.Renamed"));
        Assert.True(session.CanSave);

        Assert.True((await session.SaveAsync()).Saved);
        Assert.Equal("Firm", provider.Current.GetEntity(typeof(Company)).Caption);
        Assert.DoesNotContain("Renamed", await target.ReadAsync());
    }

    protected override void Cleanup() => _connection.Dispose();
}

public class ModelDesignerPermissionTests : DesignerTestBase
{
    private sealed class TestUser(params string[] roles) : ICurrentUser
    {
        public string? Name => "user";
        public bool IsAuthenticated => true;
        public bool IsInRole(string role) => roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_roles_granted_the_designer_have_it()
    {
        var policy = new PermissionPolicy();
        policy.Role("Admin").AllowAll().AllowModelDesigner();
        policy.Role("Manager").AllowAll();

        Assert.True(new PolicyPermissionService(policy, new TestUser("admin")).HasPermission(StrataPermissions.ModelDesigner));
        Assert.False(new PolicyPermissionService(policy, new TestUser("Manager")).HasPermission(StrataPermissions.ModelDesigner)); // AllowAll is not enough
        Assert.False(new AllowAllPermissionService().HasPermission(StrataPermissions.ModelDesigner));                             // no policy, no designer
        Assert.False(new PolicyPermissionService(policy, new AnonymousUserForTest()).HasPermission(StrataPermissions.ModelDesigner));
    }

    private sealed class AnonymousUserForTest : ICurrentUser
    {
        public string? Name => null;
        public bool IsAuthenticated => false;
        public bool IsInRole(string role) => true; // even a confused user object gets nothing while signed out
    }

    [Fact]
    public async Task The_server_refuses_to_open_or_save_without_the_permission()
    {
        var layer = new JsonFileModelLayer(WriteFile("model.json", "{}"));
        var provider = new AppModelProvider(Builder().AddLayer(layer));
        var target = new JsonFileDesignTarget(layer);

        var viewer = new ModelDesigner(provider, target, new TestPermissions(false));
        Assert.False(viewer.IsAllowed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.OpenAsync());

        var permissions = new TestPermissions(true);
        var session = await new ModelDesigner(provider, target, permissions).OpenAsync();
        session.Set(Key<Company>("caption"), "Firm");
        permissions.Designer = false; // e.g. the role was taken away while the page was open
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.SaveAsync());
        Assert.Equal("{}", await File.ReadAllTextAsync(layer.Path));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Development", typeof(JsonFileDesignTarget))]
    [InlineData("Production", typeof(DatabaseDesignTarget))]
    public void The_designer_saves_to_model_json_in_development_and_to_the_database_elsewhere(string environment, Type expected)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddStrataFx(fx =>
        {
            fx.AddEntity<Company>();
            fx.AddJsonModelLayer(Path.Combine(Folder, "model.json"));
            fx.AddModelDesigner();
        });
        var sp = services.BuildServiceProvider();

        Assert.IsType(expected, sp.GetRequiredService<IModelDesignTarget>());
        Assert.Same(sp.GetRequiredService<DatabaseModelLayer>(), sp.GetRequiredService<IAppModelProvider>().Layers[^1]);
    }
}
