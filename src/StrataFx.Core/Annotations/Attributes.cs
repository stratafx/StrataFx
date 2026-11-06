namespace StrataFx.Annotations;

/// <summary>
/// Marks a class as an entity that StrataFx generates list and detail views for.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StrataEntityAttribute : Attribute
{
    /// <summary>Singular caption, e.g. "Customer". Defaults to the class name split into words.</summary>
    public string? Caption { get; set; }

    /// <summary>Plural caption used in navigation and list titles, e.g. "Customers".</summary>
    public string? PluralCaption { get; set; }

    /// <summary>Navigation group the entity appears under, e.g. "Sales".</summary>
    public string? NavigationGroup { get; set; }

    /// <summary>Icon for navigation. The UI layer decides how to interpret it (MudBlazor uses SVG path strings).</summary>
    public string? Icon { get; set; }

    /// <summary>Sort order inside the navigation group.</summary>
    public int Order { get; set; }
}

/// <summary>
/// Names the property whose value represents an instance in lookups and references (e.g. "Name").
/// When omitted, StrataFx uses a property called Name, Title, Caption or Code, or the first string property.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class LookupTextAttribute(string propertyName) : Attribute
{
    public string PropertyName { get; } = propertyName;
}

/// <summary>Views a property can be hidden from.</summary>
[Flags]
public enum ViewKinds
{
    None = 0,
    List = 1,
    Detail = 2,
    All = List | Detail,
}

/// <summary>Hides a property from generated views.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HideInAttribute(ViewKinds views = ViewKinds.All) : Attribute
{
    public ViewKinds Views { get; } = views;
}

/// <summary>Renders a string property as a multi-line text area in detail views.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MultilineAttribute(int lines = 4) : Attribute
{
    public int Lines { get; } = lines;
}

/// <summary>Display format for list views and read-only values, e.g. "N2", "d", "C".</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DisplayFormatStringAttribute(string format) : Attribute
{
    public string Format { get; } = format;
}

/// <summary>
/// Marks a collection whose items belong to the parent (e.g. order lines). Items are edited inside the
/// parent's form and saved, added and deleted together with it. Collections without this attribute are
/// "related": shown as a linked list of separate records (e.g. a customer's orders).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OwnedCollectionAttribute : Attribute;

/// <summary>Picks a named custom property editor registered in the UI layer.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PropertyEditorAttribute(string editorName) : Attribute
{
    public string EditorName { get; } = editorName;
}
