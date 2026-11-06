using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Core.Tests;

public class SecurityTests
{
    private sealed class TestUser(params string[] roles) : ICurrentUser
    {
        public string? Name => "test";
        public bool IsAuthenticated => true;
        public bool IsInRole(string role) => roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    private static PermissionPolicy Policy()
    {
        var policy = new PermissionPolicy();
        policy.Role("Viewer").Allow<Contact>(EntityOperation.Read);
        policy.Role("Editor").Allow<Contact>(EntityOperation.Read | EntityOperation.Update);
        policy.Role("Admin").AllowAll();
        policy.Role(PermissionPolicy.AnonymousRole).Allow<Company>(EntityOperation.Read);
        return policy;
    }

    [Fact]
    public void Role_permissions_are_checked_per_type_and_operation()
    {
        var viewer = new PolicyPermissionService(Policy(), new TestUser("viewer"));

        Assert.True(viewer.IsAllowed(typeof(Contact), EntityOperation.Read));
        Assert.False(viewer.IsAllowed(typeof(Contact), EntityOperation.Update));
        Assert.False(viewer.IsAllowed(typeof(Company), EntityOperation.Read));
    }

    [Fact]
    public void Roles_combine()
    {
        var user = new PolicyPermissionService(Policy(), new TestUser("Viewer", "Editor"));
        Assert.True(user.IsAllowed(typeof(Contact), EntityOperation.Read | EntityOperation.Update));
        Assert.False(user.IsAllowed(typeof(Contact), EntityOperation.Delete));
    }

    [Fact]
    public void Admin_can_do_everything_and_unknown_roles_nothing()
    {
        Assert.True(new PolicyPermissionService(Policy(), new TestUser("Admin")).IsAllowed(typeof(Company), EntityOperation.All));
        Assert.False(new PolicyPermissionService(Policy(), new TestUser("Guest")).IsAllowed(typeof(Contact), EntityOperation.Read));
    }

    [Fact]
    public void Anonymous_users_get_the_anonymous_role()
    {
        var anonymous = new PolicyPermissionService(Policy(), new AnonymousUser());
        Assert.True(anonymous.IsAllowed(typeof(Company), EntityOperation.Read));
        Assert.False(anonymous.IsAllowed(typeof(Contact), EntityOperation.Read));
    }

    [Fact]
    public void Demand_throws_access_denied()
    {
        var viewer = new PolicyPermissionService(Policy(), new TestUser("Viewer"));
        var ex = Assert.Throws<StrataAccessDeniedException>(() => viewer.Demand(typeof(Contact), EntityOperation.Delete));
        Assert.Equal(EntityOperation.Delete, ex.Operation);
    }
}

public class ValidationTests
{
    private static readonly AppModel Model = new AppModelBuilder().AddEntity<Company>().AddEntity<Contact>().Build();

    private sealed class NoLeadsWithCredit : IEntityRule
    {
        public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Contact);

        public IEnumerable<ValidationError> Validate(EntityModel entity, object instance)
        {
            if (instance is Contact { Kind: ContactKind.Lead, CreditLimit: > 0 })
                yield return new ValidationError(nameof(Contact.CreditLimit), "Leads cannot have credit.");
        }
    }

    [Fact]
    public void Reports_data_annotation_errors()
    {
        var errors = new EntityValidator().Validate(Model.GetEntity(typeof(Company)), new Company { Name = new string('x', 60) });
        Assert.Contains(errors, e => e.PropertyName == "Name");
    }

    [Fact]
    public void Required_reference_is_enforced_without_an_attribute()
    {
        var errors = new EntityValidator().Validate(Model.GetEntity(typeof(Contact)), new Contact { FullName = "Ann" });
        var error = Assert.Single(errors);
        Assert.Equal("Company", error.PropertyName);
        Assert.Equal("Company is required.", error.Message);
    }

    [Fact]
    public void Custom_rules_run()
    {
        var contact = new Contact { FullName = "Ann", Company = new Company { Name = "C" }, Kind = ContactKind.Lead, CreditLimit = 10 };
        var validator = new EntityValidator([new NoLeadsWithCredit()]);

        Assert.Equal("CreditLimit", Assert.Single(validator.Validate(Model.GetEntity(typeof(Contact)), contact)).PropertyName);
        Assert.Throws<StrataValidationException>(() => validator.ThrowIfInvalid(Model.GetEntity(typeof(Contact)), contact));
    }

    [Fact]
    public void Valid_instance_has_no_errors()
    {
        var contact = new Contact { FullName = "Ann", Company = new Company { Name = "C" } };
        Assert.Empty(new EntityValidator().Validate(Model.GetEntity(typeof(Contact)), contact));
    }
}
