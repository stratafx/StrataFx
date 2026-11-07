using System.Reflection;

namespace StrataFx.Model;

/// <summary>How a property is edited and displayed. The UI layer maps each kind to a control.</summary>
public enum EditorKind
{
    Text,
    MultilineText,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Enum,
    Guid,
    Reference,
    Collection,
    Custom,
}

/// <summary>How a collection property relates to its parent.</summary>
public enum CollectionKind
{
    /// <summary>Separate records that point to the parent (a customer's orders). Shown as a linked list.</summary>
    Related,

    /// <summary>Items that belong to the parent (order lines). Edited in the parent's form and saved with it.</summary>
    Owned,
}

/// <summary>
/// The application model: every entity StrataFx knows about, plus the navigation tree.
/// Built once at startup from entity classes, then layered with overrides (code, JSON, user settings).
/// </summary>
public sealed class AppModel
{
    private readonly Dictionary<string, EntityModel> _byId;
    private readonly Dictionary<Type, EntityModel> _byType;

    public AppModel(string title, IReadOnlyList<EntityModel> entities)
    {
        Title = title;
        Entities = entities;
        _byId = entities.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        _byType = entities.ToDictionary(e => e.ClrType);
    }

    public string Title { get; set; }
    public IReadOnlyList<EntityModel> Entities { get; }

    public EntityModel? FindEntity(string id) => _byId.GetValueOrDefault(id);
    public EntityModel? FindEntity(Type type) => _byType.GetValueOrDefault(type);
    public EntityModel GetEntity(Type type) =>
        FindEntity(type) ?? throw new InvalidOperationException($"'{type.Name}' is not registered as a StrataFx entity.");

    /// <summary>Entities grouped for navigation, ordered by group then by entity order.</summary>
    public IEnumerable<IGrouping<string, EntityModel>> NavigationGroups() =>
        Entities.Where(e => e.VisibleInNavigation)
                .OrderBy(e => e.NavigationGroup)
                .ThenBy(e => e.Order)
                .ThenBy(e => e.PluralCaption)
                .GroupBy(e => e.NavigationGroup);
}

/// <summary>Metadata for one entity type.</summary>
public sealed class EntityModel
{
    internal EntityModel(Type clrType, string id) { ClrType = clrType; Id = id; }

    public Type ClrType { get; }

    /// <summary>URL-safe identifier used in routes, e.g. "customer".</summary>
    public string Id { get; }

    public string Caption { get; set; } = "";
    public string PluralCaption { get; set; } = "";
    public string NavigationGroup { get; set; } = "Default";
    public string? Icon { get; set; }
    public int Order { get; set; }
    public bool VisibleInNavigation { get; set; } = true;

    public PropertyModel Key { get; internal set; } = null!;
    public PropertyModel? LookupProperty { get; set; }

    public List<PropertyModel> Properties { get; } = [];

    public PropertyModel? FindProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<PropertyModel> ListColumns =>
        Properties.Where(p => p.VisibleInList).OrderBy(p => p.Order);

    public IEnumerable<PropertyModel> DetailItems =>
        Properties.Where(p => p.VisibleInDetail).OrderBy(p => p.Order);

    public IEnumerable<PropertyModel> OwnedCollections =>
        Properties.Where(p => p.EditorKind == EditorKind.Collection && p.CollectionKind == CollectionKind.Owned);

    public IEnumerable<PropertyModel> References =>
        Properties.Where(p => p.EditorKind == EditorKind.Reference);

    public object CreateInstance() => Activator.CreateInstance(ClrType)
        ?? throw new InvalidOperationException($"Cannot create an instance of '{ClrType.Name}'.");

    public object? GetKey(object instance) => Key.GetValue(instance);

    /// <summary>Converts a key from its URL form back to the key's CLR type.</summary>
    public object ParseKey(string text) =>
        System.ComponentModel.TypeDescriptor.GetConverter(Key.UnderlyingType).ConvertFromInvariantString(text)
        ?? throw new FormatException($"'{text}' is not a valid key for {ClrType.Name}.");

    /// <summary>True when the instance has not been saved yet (its key is empty).</summary>
    public bool IsNew(object instance) => GetKey(instance) switch
    {
        null => true,
        string s => s.Length == 0,
        var k => k.GetType().IsValueType && k.Equals(Activator.CreateInstance(k.GetType())),
    };

    /// <summary>Formats a key for use in URLs.</summary>
    public string FormatKey(object instance) =>
        Convert.ToString(GetKey(instance), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>Text that represents an instance in lookups, titles and reference columns.</summary>
    public string GetDisplayText(object? instance)
    {
        if (instance is null) return "";
        var value = LookupProperty?.GetValue(instance) ?? GetKey(instance);
        return value?.ToString() ?? "";
    }

    public override string ToString() => $"Entity {Id} ({ClrType.Name})";
}

/// <summary>Metadata for one property of an entity.</summary>
public sealed class PropertyModel
{
    internal PropertyModel(EntityModel owner, PropertyInfo info)
    {
        Owner = owner;
        Info = info;
        Name = info.Name;
        PropertyType = info.PropertyType;
        UnderlyingType = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
    }

    public EntityModel Owner { get; }
    public PropertyInfo Info { get; }
    public string Name { get; }
    public Type PropertyType { get; }

    /// <summary>The property type with Nullable&lt;T&gt; removed.</summary>
    public Type UnderlyingType { get; }

    public bool IsNullable => !PropertyType.IsValueType || Nullable.GetUnderlyingType(PropertyType) is not null;

    public string Caption { get; set; } = "";
    public string? Description { get; set; }
    public string? Group { get; set; }
    public int Order { get; set; }
    public bool VisibleInList { get; set; } = true;
    public bool VisibleInDetail { get; set; } = true;
    public bool ReadOnly { get; set; }
    public bool Required { get; set; }
    public int? MaxLength { get; set; }
    public int Lines { get; set; } = 1;
    public string? DisplayFormat { get; set; }
    public EditorKind EditorKind { get; set; }

    /// <summary>Name of a custom editor registered in the UI layer (used when EditorKind is Custom or to override).</summary>
    public string? EditorName { get; set; }

    /// <summary>For EditorKind.Reference: the referenced entity.</summary>
    public EntityModel? ReferenceEntity { get; internal set; }

    /// <summary>For EditorKind.Collection: the entity of the items.</summary>
    public EntityModel? ElementEntity { get; internal set; }

    /// <summary>For EditorKind.Collection: whether items are owned by the parent or separate related records.</summary>
    public CollectionKind CollectionKind { get; set; }

    /// <summary>
    /// For EditorKind.Collection: the reference on the item that points back to the parent
    /// (e.g. OrderLine.Order for Order.Lines). Null when the item has no such property.
    /// </summary>
    public PropertyModel? InverseReference { get; internal set; }

    public bool IsKey => ReferenceEquals(Owner.Key, this);

    /// <summary>True for a foreign-key scalar behind a reference (e.g. CustomerId for Customer). Hidden; the reference editor sets it.</summary>
    public bool IsForeignKey { get; internal set; }

    // ---- collection helpers (work with List<T>, ICollection<T> and other IList implementations) ----

    public IReadOnlyList<object> GetItems(object instance) =>
        GetValue(instance) is System.Collections.IEnumerable items ? items.Cast<object>().ToList() : [];

    public void AddItem(object instance, object item)
    {
        var collection = GetOrCreateCollection(instance);
        if (collection is System.Collections.IList list) list.Add(item);
        else CollectionMethod(collection, "Add").Invoke(collection, [item]);
    }

    public void RemoveItem(object instance, object item)
    {
        var collection = GetOrCreateCollection(instance);
        if (collection is System.Collections.IList list) list.Remove(item);
        else CollectionMethod(collection, "Remove").Invoke(collection, [item]);
    }

    private object GetOrCreateCollection(object instance)
    {
        if (GetValue(instance) is { } existing) return existing;
        var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(ElementEntity!.ClrType))!;
        SetValue(instance, list);
        return list;
    }

    private MethodInfo CollectionMethod(object collection, string name) =>
        typeof(ICollection<>).MakeGenericType(ElementEntity!.ClrType).GetMethod(name)
        ?? throw new InvalidOperationException($"{Owner.ClrType.Name}.{Name} does not support {name}.");

    public object? GetValue(object instance) => Info.GetValue(instance);

    /// <summary>
    /// True when two values of this property are the same. References are compared by key, because lookups
    /// can return a different object for the same record.
    /// </summary>
    public bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (ReferenceEntity is { } target && target.ClrType.IsInstanceOfType(a) && target.ClrType.IsInstanceOfType(b))
            return !target.IsNew(a) && !target.IsNew(b) && Equals(target.GetKey(a), target.GetKey(b));
        return Equals(a, b);
    }

    public void SetValue(object instance, object? value)
    {
        if (!Info.CanWrite) throw new InvalidOperationException($"{Owner.ClrType.Name}.{Name} is read-only.");
        Info.SetValue(instance, value);
    }

    /// <summary>Formats a value for list views and read-only display.</summary>
    public string FormatValue(object? value)
    {
        if (value is null) return "";
        if (ReferenceEntity is not null) return ReferenceEntity.GetDisplayText(value);
        if (EditorKind == EditorKind.Collection && value is System.Collections.IEnumerable items)
        {
            var count = items.Cast<object>().Count();
            return count == 1 ? "1 item" : $"{count} items";
        }
        if (value is bool b) return b ? "Yes" : "No";
        if (DisplayFormat is not null && value is IFormattable f) return f.ToString(DisplayFormat, null);
        return EditorKind switch
        {
            EditorKind.Date when value is DateTime d => d.ToShortDateString(),
            EditorKind.Date when value is DateOnly d => d.ToShortDateString(),
            EditorKind.Enum => Humanizer.SplitWords(value.ToString() ?? ""),
            _ => value.ToString() ?? "",
        };
    }

    public override string ToString() => $"{Owner.ClrType.Name}.{Name} ({EditorKind})";
}
