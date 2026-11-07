using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StrataFx.EFCore;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Settings;

namespace StrataFx.Core.Tests;

/// <summary>Merging a saved list layout with the model (the user stratum).</summary>
public class ListLayoutTests
{
    private static readonly AppModel Model = CollectionModelTests.Build();
    private static EntityModel Contacts => Model.GetEntity(typeof(Contact));

    private static List<string> Visible(ListViewLayout layout) => layout.VisibleColumns.Select(c => c.Name).ToList();

    private static ListLayoutColumn Column(string name, int order, bool visible = true, int? width = null) =>
        new() { Name = name, Order = order, Visible = visible, Width = width };

    [Fact]
    public void Without_a_saved_layout_the_model_columns_are_shown()
    {
        var layout = ListLayouts.Resolve(Contacts, null);

        Assert.Equal(Contacts.ListColumns.Select(c => c.Name), Visible(layout));
        Assert.Equal(25, layout.PageSize);
        Assert.Null(layout.SortProperty);
    }

    [Fact]
    public void Users_choose_only_from_columns_that_can_be_shown_in_lists()
    {
        var names = ListLayouts.AvailableColumns(Contacts).Select(c => c.Name).ToList();

        Assert.Contains("Active", names);                 // hidden by default ([HideIn(List)]), but may be added
        Assert.DoesNotContain("Id", names);               // key
        Assert.DoesNotContain("CompanyId", names);        // foreign key
        Assert.DoesNotContain("ReferrerId", names);
        Assert.DoesNotContain("Contacts", ListLayouts.AvailableColumns(Model.GetEntity(typeof(Company))).Select(c => c.Name)); // collection
        Assert.DoesNotContain("Company", ListLayouts.AvailableColumns(Contacts, excluded: ["Company"]).Select(c => c.Name)); // a related list's parent
        Assert.False(ListLayouts.CanShowInList(Model.GetEntity(typeof(Company)).FindProperty("Remarks")!)); // multi-line text
    }

    [Fact]
    public void Hidden_and_reordered_columns_are_applied()
    {
        var saved = new ListLayout
        {
            Columns = [Column("Kind", 0), Column("FullName", 1, width: 220), Column("BirthDate", 2, visible: false), Column("Active", 3)],
            SortProperty = "FullName",
            SortDescending = true,
            PageSize = 50,
        };

        var layout = ListLayouts.Resolve(Contacts, saved);

        var visible = Visible(layout);
        Assert.Equal("Kind", visible[0]);
        Assert.True(visible.IndexOf("FullName") < visible.IndexOf("Active"));
        Assert.DoesNotContain("BirthDate", Visible(layout));
        Assert.Equal(220, layout.Columns.Single(c => c.Name == "FullName").Width);
        Assert.Equal("FullName", layout.SortProperty);
        Assert.True(layout.SortDescending);
        Assert.Equal(50, layout.PageSize);
    }

    [Fact]
    public void Unknown_columns_and_invalid_values_are_ignored()
    {
        var saved = new ListLayout
        {
            Columns = [Column("Removed", 0), Column("CompanyId", 1), Column("FullName", 2, width: 5000)],
            SortProperty = "Removed",
            PageSize = 7,
        };

        var layout = ListLayouts.Resolve(Contacts, saved, defaultPageSize: 10);

        Assert.DoesNotContain(layout.Columns, c => c.Name is "Removed" or "CompanyId");
        Assert.Equal(ListLayouts.MaxWidth, layout.Columns.Single(c => c.Name == "FullName").Width);
        Assert.Null(layout.SortProperty);
        Assert.Equal(10, layout.PageSize);
    }

    [Fact]
    public void A_column_added_to_the_model_later_appears_at_its_default_position()
    {
        // Saved before the model had Kind and CreditLimit: FullName and BirthDate swapped, Company and Referrer kept.
        var saved = new ListLayout { Columns = [Column("FullName", 0), Column("BirthDate", 1), Column("Company", 2), Column("Referrer", 3)] };

        var layout = ListLayouts.Resolve(Contacts, saved);
        var order = layout.Columns.Select(c => c.Name).ToList();

        // Model order is BirthDate (Order = 1), FullName, Company, Referrer, Kind, CreditLimit, Active, Computed:
        // the new columns go after their model predecessor, Referrer, and keep the model's visibility.
        Assert.Equal(["FullName", "BirthDate", "Company", "Referrer", "Kind", "CreditLimit", "Active", "Computed"], order);
        Assert.True(layout.Columns.Single(c => c.Name == "Kind").Visible);
        Assert.False(layout.Columns.Single(c => c.Name == "Active").Visible);
    }

    [Fact]
    public void A_layout_with_no_visible_columns_falls_back_to_the_model()
    {
        var saved = new ListLayout { Columns = ListLayouts.AvailableColumns(Contacts).Select((c, i) => Column(c.Name, i, visible: false)).ToList() };

        Assert.Equal(Contacts.ListColumns.Select(c => c.Name), Visible(ListLayouts.Resolve(Contacts, saved)));
    }

    [Fact]
    public void Resolving_does_not_change_the_shared_model()
    {
        var model = CollectionModelTests.Build();
        var contacts = model.GetEntity(typeof(Contact));
        var before = contacts.Properties.Select(p => (p.Name, p.VisibleInList, p.Order)).ToList();

        var layout = ListLayouts.Resolve(contacts, new ListLayout { Columns = [Column("Active", 0), Column("FullName", 1, visible: false)] });
        layout.Columns[0].Visible = false;

        Assert.Equal(before, contacts.Properties.Select(p => (p.Name, p.VisibleInList, p.Order)).ToList());
    }

    [Fact]
    public void A_layout_round_trips_and_compares()
    {
        var layout = ListLayouts.Resolve(Contacts, null);
        layout.Columns.Reverse();
        layout.Columns[0].Width = 150;
        layout.SortProperty = "Kind";

        var again = ListLayouts.Resolve(Contacts, layout.ToLayout());

        Assert.True(again.SameAs(layout));
        Assert.False(again.SameAs(ListLayouts.Resolve(Contacts, null)));
    }

    [Fact]
    public void Embedded_lists_have_their_own_view_key()
    {
        var company = Model.GetEntity(typeof(Company));

        Assert.Equal("contact", ListLayouts.ViewKey(Contacts));
        Assert.Equal("company.Contacts", ListLayouts.ViewKey(Contacts, company.FindProperty("Contacts")));
        Assert.Equal("list/company.Contacts", ListLayouts.SettingKey("company.Contacts"));
    }
}

/// <summary>Saving settings per user: in the database, in memory, and for anonymous users.</summary>
public sealed class UserSettingsStoreTests : IAsyncLifetime
{
    private sealed class SettingsContext(DbContextOptions<SettingsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddStrataUserSettings();
    }

    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext
    {
        public T CreateDbContext() => create();
    }

    private sealed class TestUser(string? name, params string[] roles) : ICurrentUser
    {
        public string? Name => name;
        public bool IsAuthenticated => name is not null;
        public bool IsInRole(string role) => roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<SettingsContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<SettingsContext>().UseSqlite(_connection).Options;
        await using var db = new SettingsContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private EfUserSettingsStore<SettingsContext> EfStore() => new(new Factory<SettingsContext>(() => new SettingsContext(_options)));

    public static TheoryData<string> Stores => ["ef", "memory"];

    private IUserSettingsStore Store(string kind) => kind == "ef" ? EfStore() : new InMemoryUserSettingsStore();

    [Theory, MemberData(nameof(Stores))]
    public async Task Saves_loads_and_resets_per_user_and_key(string kind)
    {
        var store = Store(kind);

        await store.SaveAsync("ann", "list/customer", "{\"a\":1}");
        await store.SaveAsync("ann", "list/customer", "{\"a\":2}"); // update, not a second row
        await store.SaveAsync("ann", "list/order", "{\"b\":1}");
        await store.SaveAsync("bob", "list/customer", "{\"c\":1}");

        Assert.Equal("{\"a\":2}", await store.GetAsync("ann", "list/customer"));
        Assert.Equal("{\"b\":1}", await store.GetAsync("ann", "list/order"));
        Assert.Equal("{\"c\":1}", await store.GetAsync("bob", "list/customer"));
        Assert.Null(await store.GetAsync("cara", "list/customer"));

        await store.ResetAsync("ann", "list/customer");

        Assert.Null(await store.GetAsync("ann", "list/customer"));
        Assert.Equal("{\"b\":1}", await store.GetAsync("ann", "list/order"));
        Assert.Equal("{\"c\":1}", await store.GetAsync("bob", "list/customer"));
    }

    [Fact]
    public async Task The_database_has_one_row_per_user_and_key()
    {
        var store = EfStore();
        await store.SaveAsync("ann", "list/customer", "1");
        await store.SaveAsync("ann", "list/customer", "2");

        await using var db = new SettingsContext(_options);
        var row = Assert.Single(await db.Set<StrataUserSetting>().ToListAsync());
        Assert.Equal(("ann", "list/customer", "2"), (row.UserName, row.Key, row.Json));
        Assert.True(row.UpdatedOn > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task A_context_without_the_mapping_gets_a_clear_message()
    {
        var store = new EfUserSettingsStore<CrmContext>(new Factory<CrmContext>(() =>
            new CrmContext(new DbContextOptionsBuilder<CrmContext>().UseSqlite(_connection).Options)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("ann", "k"));
        Assert.Contains("AddStrataUserSettings", error.Message);
    }

    [Fact]
    public async Task Users_do_not_see_each_others_layouts()
    {
        var store = EfStore();
        var options = new UserSettingsOptions();
        var ann = new UserSettings(new TestUser("ann"), store, options);
        var bob = new UserSettings(new TestUser("bob"), store, options);
        var key = ListLayouts.SettingKey("customer");

        await ann.SaveAsync(key, new ListLayout { SortProperty = "Name", PageSize = 50 });

        Assert.Equal(50, (await ann.GetAsync<ListLayout>(key))!.PageSize);
        Assert.Null(await bob.GetAsync<ListLayout>(key));
        // A new session for the same user (sign out and in again) reads the saved layout.
        Assert.Equal("Name", (await new UserSettings(new TestUser("ann"), store, options).GetAsync<ListLayout>(key))!.SortProperty);
    }

    [Fact]
    public async Task Anonymous_users_keep_settings_for_the_session_only()
    {
        var store = new InMemoryUserSettingsStore();
        var options = new UserSettingsOptions();
        var session = new UserSettings(new AnonymousUser(), store, options);
        var key = ListLayouts.SettingKey("customer");

        await session.SaveAsync(key, new ListLayout { PageSize = 100 });

        Assert.False(session.IsPersistent);
        Assert.Equal(100, (await session.GetAsync<ListLayout>(key))!.PageSize);
        Assert.Null(await new UserSettings(new AnonymousUser(), store, options).GetAsync<ListLayout>(key)); // another visitor
        Assert.Null(await store.GetAsync("", key));

        await session.ResetAsync(key);
        Assert.Null(await session.GetAsync<ListLayout>(key));
    }

    [Fact]
    public async Task Unreadable_settings_count_as_none()
    {
        var store = new InMemoryUserSettingsStore();
        await store.SaveAsync("ann", "list/customer", "not json");

        Assert.Null(await new UserSettings(new TestUser("ann"), store, new UserSettingsOptions()).GetAsync<ListLayout>("list/customer"));
    }

    [Fact]
    public async Task Only_the_shared_defaults_role_may_change_the_default_for_everyone()
    {
        var store = EfStore();
        var options = new UserSettingsOptions { SharedDefaultsRole = "Admin" };
        var admin = new UserSettings(new TestUser("root", "Admin"), store, options);
        var ann = new UserSettings(new TestUser("ann"), store, options);
        var key = ListLayouts.SettingKey("customer");

        Assert.True(admin.CanEditSharedDefaults);
        Assert.False(ann.CanEditSharedDefaults);
        Assert.False(new UserSettings(new TestUser("root", "Admin"), store, new UserSettingsOptions { SharedDefaultsRole = null }).CanEditSharedDefaults);

        await admin.SaveSharedAsync(key, new ListLayout { PageSize = 50 });

        Assert.Equal(50, (await ann.GetSharedAsync<ListLayout>(key))!.PageSize); // everyone reads it
        Assert.Null(await ann.GetAsync<ListLayout>(key));                         // it is not ann's own layout
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ann.SaveSharedAsync(key, new ListLayout()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ann.ResetSharedAsync(key));

        await admin.ResetSharedAsync(key);
        Assert.Null(await ann.GetSharedAsync<ListLayout>(key));
    }
}
