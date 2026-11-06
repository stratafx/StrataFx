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
    Custom,
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

    public object CreateInstance() => Activator.CreateInstance(ClrType)
        ?? throw new InvalidOperationException($"Cannot create an instance of '{ClrType.Name}'.");

    public object? GetKey(object instance) => Key.GetValue(instance);

    /// <summary>Converts a key from its URL form back to the key's CLR type.</summary>
    public object ParseKey(string text) =>
        System.ComponentModel.TypeDescriptor.GetConverter(Key.UnderlyingType).ConvertFromInvariantString(text)
        ?? throw new FormatException($"'{text}' is not a valid key for {ClrType.Name}.");

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

    public bool IsKey => ReferenceEquals(Owner.Key, this);

    public object? GetValue(object instance) => Info.GetValue(instance);

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
