using StrataFx.Changes;
using StrataFx.Commands;
using StrataFx.Hosting;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Sample.Sales;

/// <summary>A module bundles entities, model tweaks, commands, rules and permissions for one feature area.</summary>
public sealed class SalesModule : StrataModule
{
    public override void ConfigureModel(AppModelBuilder model)
    {
        model.AddEntitiesFrom(typeof(SalesModule).Assembly); // classes marked [StrataEntity]
        model.AddEntity<OrderLine>();                        // plain classes can be added directly

        // A code layer: the same kind of change a JSON layer can make, but compiled.
        model.AddLayer(new DelegateModelLayer("Sales defaults", app =>
        {
            var order = app.GetEntity(typeof(Order));
            order.FindProperty(nameof(Order.ShippedOn))!.ReadOnly = true; // set by the Ship command
        }));
    }

    public override void ConfigureServices(StrataFxBuilder fx)
    {
        fx.AddCommand<MarkVipCommand>();
        fx.AddCommand<ShipOrderCommand>();
        fx.AddRule<OrderRules>();
        fx.AddSaveHandler<OrderTotals>();
        fx.AddChangeHandler<OrderLinePricing>();
        fx.AddChangeHandler<OrderLiveTotal>();
    }

    public override void ConfigureSecurity(PermissionPolicy policy)
    {
        policy.Role("SalesViewer")
            .Allow<Customer>(EntityOperation.Read)
            .Allow<Product>(EntityOperation.Read)
            .Allow<Order>(EntityOperation.Read);
        policy.Role("SalesManager").AllowAll();
    }
}

/// <summary>Marks the selected customers as VIP. Works on one or many rows.</summary>
public sealed class MarkVipCommand : StrataCommand<Customer>
{
    public override string Id => "Sales.MarkVip";
    public override string Caption => "Mark as VIP";
    public override string? Icon => MudBlazor.Icons.Material.Filled.Star;
    public override SelectionRequirement Selection => SelectionRequirement.AtLeastOne;

    protected override bool CanExecute(IReadOnlyList<Customer> selection, CommandContext context) =>
        selection.Any(c => !c.IsVip);

    protected override async Task<CommandResult> ExecuteAsync(IReadOnlyList<Customer> selection, CommandContext context, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var customer in selection.Where(c => !c.IsVip))
        {
            customer.IsVip = true;
            await context.Data.SaveAsync(context.Entity, customer, isNew: false, cancellationToken);
            changed++;
        }
        return CommandResult.Ok(changed == 1 ? "Customer marked as VIP." : $"{changed} customers marked as VIP.");
    }
}

/// <summary>Ships an order: sets the status and the shipped date.</summary>
public sealed class ShipOrderCommand : StrataCommand<Order>
{
    public override string Id => "Sales.ShipOrder";
    public override string Caption => "Ship";
    public override string? Icon => MudBlazor.Icons.Material.Filled.LocalShipping;
    public override string? ConfirmationMessage => "Mark this order as shipped today?";

    protected override bool CanExecute(IReadOnlyList<Order> selection, CommandContext context) =>
        selection is [{ Status: OrderStatus.New or OrderStatus.Processing }];

    protected override async Task<CommandResult> ExecuteAsync(IReadOnlyList<Order> selection, CommandContext context, CancellationToken cancellationToken)
    {
        var order = selection[0];
        order.Status = OrderStatus.Shipped;
        order.ShippedOn = DateTime.Now;
        await context.Data.SaveAsync(context.Entity, order, isNew: false, cancellationToken);
        return CommandResult.Ok($"Order {order.Number} shipped.");
    }
}

/// <summary>Business rules that data annotations cannot express.</summary>
public sealed class OrderRules : IEntityRule
{
    public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Order);

    public IEnumerable<ValidationError> Validate(EntityModel entity, object instance)
    {
        var order = (Order)instance;
        if (order.OrderDate.Date > DateTime.Today)
            yield return new ValidationError(nameof(Order.OrderDate), "Order date cannot be in the future.");
        if (order.Status == OrderStatus.Shipped && order.Lines.FirstOrDefault(l => l.Product is { InStock: false }) is { } line)
            yield return new ValidationError(nameof(Order.Lines), $"{line.Product.Name} is out of stock, so the order cannot be shipped.");
    }
}

/// <summary>Keeps the stored order total in step with its lines. The source of truth: runs on every save, with or without a form.</summary>
public sealed class OrderTotals : StrataFx.Data.IEntitySaveHandler
{
    public static decimal Calculate(Order order) => order.Lines.Sum(l => l.LineTotal);

    public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(Order);

    public void BeforeSave(EntityModel entity, object instance, bool isNew) => ((Order)instance).Total = Calculate((Order)instance);
}

/// <summary>
/// Picking a product on an order line fills in its current price. It runs only when the product changes,
/// so a price typed by hand stays until a different product is picked.
/// </summary>
public sealed class OrderLinePricing : EntityChangeHandler<OrderLine>
{
    protected override ValueTask OnChangedAsync(OrderLine line, PropertyChangeContext context)
    {
        if (context.Is(nameof(OrderLine.Product)) && line.Product is { } product)
            line.UnitPrice = product.Price;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Updates the order total in the form as lines are added, removed or changed (OrderTotals sets it again on save).</summary>
public sealed class OrderLiveTotal : EntityChangeHandler<Order>
{
    protected override ValueTask OnChangedAsync(Order order, PropertyChangeContext context)
    {
        if (context.Is(nameof(Order.Lines)))
            order.Total = OrderTotals.Calculate(order);
        return ValueTask.CompletedTask;
    }
}
