using Microsoft.Extensions.DependencyInjection;
using StrataFx.Data;
using StrataFx.Model;

namespace StrataFx.Changes;

/// <summary>What happened to <see cref="PropertyChangeContext.Property"/>.</summary>
public enum PropertyChangeKind
{
    /// <summary>The user changed the property's value.</summary>
    Value,

    /// <summary>A row was added to an owned collection. <see cref="PropertyChangeContext.Item"/> is the new row.</summary>
    ItemAdded,

    /// <summary>A row was removed from an owned collection. <see cref="PropertyChangeContext.Item"/> is the removed row.</summary>
    ItemRemoved,

    /// <summary>A field of a row in an owned collection changed. <see cref="PropertyChangeContext.Item"/> is that row.</summary>
    ItemChanged,
}

/// <summary>Describes one change a user made in a form, before it is saved.</summary>
public sealed class PropertyChangeContext
{
    private readonly IServiceProvider _services;

    internal PropertyChangeContext(IServiceProvider services, EntityModel entity, object instance, PropertyModel property,
        PropertyChangeKind kind, object? item, PropertyModel? parentCollection, object? parentInstance, CancellationToken cancellationToken)
    {
        _services = services;
        Entity = entity;
        Instance = instance;
        Property = property;
        Kind = kind;
        Item = item;
        ParentCollection = parentCollection;
        ParentInstance = parentInstance;
        CancellationToken = cancellationToken;
    }

    /// <summary>The entity whose property changed (for an owned row: the row's entity, e.g. OrderLine).</summary>
    public EntityModel Entity { get; }

    /// <summary>The object being edited. Handlers may change its other properties; the form shows them right away.</summary>
    public object Instance { get; }

    /// <summary>The property that changed. For row added/removed/changed, the parent's collection property (e.g. Order.Lines).</summary>
    public PropertyModel Property { get; }

    public PropertyChangeKind Kind { get; }

    /// <summary>For collection changes: the row that was added, removed or changed.</summary>
    public object? Item { get; }

    /// <summary>When <see cref="Instance"/> is an owned row: the parent's collection that holds it (e.g. Order.Lines).</summary>
    public PropertyModel? ParentCollection { get; }

    /// <summary>When <see cref="Instance"/> is an owned row: the parent being edited (e.g. the Order).</summary>
    public object? ParentInstance { get; }

    /// <summary>When <see cref="Instance"/> is an owned row: the parent's entity.</summary>
    public EntityModel? ParentEntity => ParentCollection?.Owner;

    public CancellationToken CancellationToken { get; }

    public IServiceProvider Services => _services;

    /// <summary>The data store, for lookups (e.g. a product's price). Not available if no data store is registered.</summary>
    public IDataStore Data => _services.GetRequiredService<IDataStore>();

    /// <summary>True when the change is to the property with this name, e.g. <c>context.Is(nameof(OrderLine.Product))</c>.</summary>
    public bool Is(string propertyName) => string.Equals(Property.Name, propertyName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reacts when a user changes a field in a generated form, before saving, e.g. to fill in a price when a product
/// is picked or to keep a total up to date. Register with <c>fx.AddChangeHandler&lt;T&gt;()</c>; handlers are
/// resolved from DI so they can take dependencies.
/// Values a handler sets do not raise further changes. Change handlers make forms responsive; compute values
/// that must be right in an <see cref="IEntitySaveHandler"/> as well, because data can be saved without a form.
/// </summary>
public interface IEntityChangeHandler
{
    bool AppliesTo(EntityModel entity);
    ValueTask OnChangedAsync(PropertyChangeContext context);
}

/// <summary>A change handler for one entity type, with typed access to the changed object.</summary>
public abstract class EntityChangeHandler<TEntity> : IEntityChangeHandler where TEntity : class
{
    public bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(TEntity);

    public ValueTask OnChangedAsync(PropertyChangeContext context) => OnChangedAsync((TEntity)context.Instance, context);

    protected abstract ValueTask OnChangedAsync(TEntity instance, PropertyChangeContext context);
}

/// <summary>
/// Runs the registered <see cref="IEntityChangeHandler"/>s for changes made in forms. A change to an owned row runs the
/// row's handlers first, then raises a change on the parent's collection property, so a parent can recompute
/// from its rows. Adding or removing a row raises a change on the parent's collection property too.
/// Exceptions from handlers are passed to the caller.
/// </summary>
public sealed class EntityChangeDispatcher(IEnumerable<IEntityChangeHandler> handlers, IServiceProvider services)
{
    private readonly IReadOnlyList<IEntityChangeHandler> _handlers = handlers.ToList();

    /// <summary>The user changed <paramref name="property"/> of <paramref name="instance"/>.</summary>
    public Task PropertyChangedAsync(EntityModel entity, object instance, PropertyModel property, CancellationToken cancellationToken = default) =>
        RunAsync(new PropertyChangeContext(services, entity, instance, property, PropertyChangeKind.Value, null, null, null, cancellationToken));

    /// <summary>The user changed <paramref name="property"/> of <paramref name="item"/>, a row of the owned <paramref name="collection"/>.</summary>
    public async Task ItemChangedAsync(PropertyModel collection, object parent, object item, PropertyModel property, CancellationToken cancellationToken = default)
    {
        await RunAsync(new PropertyChangeContext(services, ElementOf(collection), item, property, PropertyChangeKind.Value, null, collection, parent, cancellationToken));
        await CollectionChangedAsync(collection, parent, PropertyChangeKind.ItemChanged, item, cancellationToken);
    }

    /// <summary>The user added <paramref name="item"/> to the owned <paramref name="collection"/>.</summary>
    public Task ItemAddedAsync(PropertyModel collection, object parent, object item, CancellationToken cancellationToken = default) =>
        CollectionChangedAsync(collection, parent, PropertyChangeKind.ItemAdded, item, cancellationToken);

    /// <summary>The user removed <paramref name="item"/> from the owned <paramref name="collection"/>.</summary>
    public Task ItemRemovedAsync(PropertyModel collection, object parent, object item, CancellationToken cancellationToken = default) =>
        CollectionChangedAsync(collection, parent, PropertyChangeKind.ItemRemoved, item, cancellationToken);

    private Task CollectionChangedAsync(PropertyModel collection, object parent, PropertyChangeKind kind, object item, CancellationToken cancellationToken) =>
        RunAsync(new PropertyChangeContext(services, collection.Owner, parent, collection, kind, item, null, null, cancellationToken));

    private static EntityModel ElementOf(PropertyModel collection) => collection.ElementEntity
        ?? throw new ArgumentException($"{collection.Owner.ClrType.Name}.{collection.Name} is not a collection.", nameof(collection));

    private async Task RunAsync(PropertyChangeContext context)
    {
        foreach (var handler in _handlers)
        {
            if (handler.AppliesTo(context.Entity))
                await handler.OnChangedAsync(context);
        }
    }
}
