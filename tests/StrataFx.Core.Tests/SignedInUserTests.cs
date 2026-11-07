using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StrataFx.Blazor.Security;
using StrataFx.Commands;
using StrataFx.Data;
using StrataFx.EFCore;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Core.Tests;

internal static class Principals
{
    public static ClaimsPrincipal SignedIn(string name, params string[] roles) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "Test"));

    /// <summary>No authentication type: not signed in, even though it carries a role claim.</summary>
    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")]));

    public static PermissionPolicy Policy()
    {
        var policy = new PermissionPolicy();
        policy.Role("Viewer").Allow<Contact>(EntityOperation.Read).Allow<Company>(EntityOperation.Read);
        policy.Role("Admin").AllowAll();
        return policy;
    }
}

public class ClaimsPrincipalUserTests
{
    [Fact]
    public void Reads_name_and_roles_from_claims()
    {
        var user = new ClaimsPrincipalUser(Principals.SignedIn("ann", "Viewer", "Sales"));

        Assert.True(user.IsAuthenticated);
        Assert.Equal("ann", user.Name);
        Assert.True(user.IsInRole("Viewer"));
        Assert.True(user.IsInRole("Sales"));
        Assert.False(user.IsInRole("Admin"));
    }

    [Fact]
    public void Unauthenticated_principal_has_no_name_or_roles()
    {
        var user = new ClaimsPrincipalUser(Principals.Anonymous());

        Assert.False(user.IsAuthenticated);
        Assert.Null(user.Name);
        Assert.False(user.IsInRole("Admin"));
        Assert.False(new PolicyPermissionService(Principals.Policy(), user).IsAllowed(typeof(Contact), EntityOperation.Read));
    }
}

public class AuthenticationStateCurrentUserTests
{
    private sealed class TestProvider : AuthenticationStateProvider
    {
        public Task<AuthenticationState>? State { get; set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            State ?? throw new InvalidOperationException("No state yet.");

        public void Set(Task<AuthenticationState> state)
        {
            State = state;
            NotifyAuthenticationStateChanged(state);
        }
    }

    private static Task<AuthenticationState> StateOf(ClaimsPrincipal principal) => Task.FromResult(new AuthenticationState(principal));

    [Fact]
    public void Uses_the_authentication_state_user()
    {
        var provider = new TestProvider { State = StateOf(Principals.SignedIn("ann", "Viewer")) };
        using var user = new AuthenticationStateCurrentUser(provider);

        Assert.True(user.IsAuthenticated);
        Assert.Equal("ann", user.Name);
        Assert.True(user.IsInRole("viewer"));
        Assert.False(user.IsInRole("Admin"));
    }

    [Fact]
    public void Follows_authentication_state_changes()
    {
        var provider = new TestProvider { State = StateOf(Principals.SignedIn("ann", "Admin")) };
        using var user = new AuthenticationStateCurrentUser(provider);
        Assert.True(user.IsInRole("Admin"));

        provider.Set(StateOf(Principals.Anonymous())); // e.g. revalidation signed the user out

        Assert.False(user.IsAuthenticated);
        Assert.False(user.IsInRole("Admin"));
    }

    [Fact]
    public void Is_anonymous_when_there_is_no_state_yet()
    {
        var provider = new TestProvider();
        using var user = new AuthenticationStateCurrentUser(provider);
        Assert.False(user.IsAuthenticated);

        provider.State = StateOf(Principals.SignedIn("ann")); // the state is read again until there is one
        Assert.True(user.IsAuthenticated);
    }

    [Fact]
    public async Task Is_anonymous_until_a_pending_state_completes()
    {
        var pending = new TaskCompletionSource<AuthenticationState>();
        var provider = new TestProvider { State = pending.Task };
        using var user = new AuthenticationStateCurrentUser(provider);

        Assert.False(user.IsAuthenticated); // does not block

        pending.SetResult(new AuthenticationState(Principals.SignedIn("ann", "Viewer")));
        await pending.Task;
        Assert.True(user.IsInRole("Viewer"));
    }
}

public class CommandPermissionTests
{
    private static readonly AppModel Model = CollectionModelTests.Build();

    private sealed class ArchiveContact : StrataCommand<Contact>
    {
        public override string Id => "Archive";
        public override string Caption => "Archive";

        protected override Task<CommandResult> ExecuteAsync(IReadOnlyList<Contact> selection, CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok());
    }

    private sealed class PrintContact : StrataCommand<Contact>
    {
        public override string Id => "Print";
        public override string Caption => "Print";
        public override EntityOperation RequiredOperation => EntityOperation.Read;
        public override CommandTarget Target => CommandTarget.Detail;

        protected override Task<CommandResult> ExecuteAsync(IReadOnlyList<Contact> selection, CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok());
    }

    private static readonly StrataCommand[] Commands = [new ArchiveContact(), new PrintContact()];

    private static IPermissionService For(params string[] roles) =>
        new PolicyPermissionService(Principals.Policy(), new ClaimsPrincipalUser(Principals.SignedIn("ann", roles)));

    private static List<string> Available(IPermissionService permissions, CommandTarget target, Type entity) =>
        Commands.Available(Model.GetEntity(entity), target, permissions).Select(c => c.Id).ToList();

    [Fact]
    public void Commands_need_update_by_default()
    {
        Assert.Equal(EntityOperation.Update, new ArchiveContact().RequiredOperation);
        Assert.Equal(["Print"], Available(For("Viewer"), CommandTarget.Detail, typeof(Contact)));
        Assert.Equal(["Archive", "Print"], Available(For("Admin"), CommandTarget.Detail, typeof(Contact)));
    }

    [Fact]
    public void Target_and_entity_still_apply()
    {
        Assert.Equal(["Archive"], Available(For("Admin"), CommandTarget.List, typeof(Contact)));
        Assert.Empty(Available(For("Admin"), CommandTarget.Detail, typeof(Company)));
    }

    [Fact]
    public void Users_without_roles_see_no_commands() =>
        Assert.Empty(Available(For(), CommandTarget.Detail, typeof(Contact)));
}

/// <summary>The store enforces the policy for a signed-in viewer, whatever the UI shows.</summary>
public sealed class ViewerStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppModel _model = CollectionModelTests.Build();
    private DbContextOptions<CrmContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<CrmContext>().UseSqlite(_connection).Options;
        await using var db = new CrmContext(_options);
        await db.Database.EnsureCreatedAsync();
        db.Add(new Contact { FullName = "Ann Lee", Company = new Company { Name = "Contoso" } });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private EfDataStore<CrmContext> StoreFor(params string[] roles) => new(
        new Factory(_options),
        new PolicyPermissionService(Principals.Policy(), new ClaimsPrincipalUser(Principals.SignedIn("ann", roles))),
        new EntityValidator());

    private sealed class Factory(DbContextOptions<CrmContext> options) : IDbContextFactory<CrmContext>
    {
        public CrmContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_save_or_delete()
    {
        var contacts = _model.GetEntity(typeof(Contact));
        var store = StoreFor("Viewer");

        var ann = Assert.Single((await store.QueryAsync(contacts, new QueryRequest())).Items);
        ((Contact)ann).FullName = "Changed";

        var update = await Assert.ThrowsAsync<StrataAccessDeniedException>(() => store.SaveAsync(contacts, ann, isNew: false));
        Assert.Equal(EntityOperation.Update, update.Operation);
        var create = await Assert.ThrowsAsync<StrataAccessDeniedException>(() =>
            store.SaveAsync(_model.GetEntity(typeof(Company)), new Company { Name = "New" }, isNew: true));
        Assert.Equal(EntityOperation.Create, create.Operation);
        var delete = await Assert.ThrowsAsync<StrataAccessDeniedException>(() => store.DeleteAsync(contacts, [ann]));
        Assert.Equal(EntityOperation.Delete, delete.Operation);

        await using var db = new CrmContext(_options);
        Assert.Equal("Ann Lee", Assert.Single(await db.Contacts.ToListAsync()).FullName);
    }

    [Fact]
    public async Task Admin_can_save()
    {
        var contacts = _model.GetEntity(typeof(Contact));
        var store = StoreFor("Admin");
        var ann = (await store.QueryAsync(contacts, new QueryRequest())).Items.Single();
        ((Contact)ann).FullName = "Ann Changed";

        await store.SaveAsync(contacts, ann, isNew: false);

        await using var db = new CrmContext(_options);
        Assert.Equal("Ann Changed", Assert.Single(await db.Contacts.ToListAsync()).FullName);
    }

    [Fact]
    public async Task Signed_out_user_cannot_even_read()
    {
        var store = new EfDataStore<CrmContext>(new Factory(_options),
            new PolicyPermissionService(Principals.Policy(), new ClaimsPrincipalUser(Principals.Anonymous())), new EntityValidator());
        await Assert.ThrowsAsync<StrataAccessDeniedException>(() =>
            store.QueryAsync(_model.GetEntity(typeof(Contact)), new QueryRequest()));
    }
}
