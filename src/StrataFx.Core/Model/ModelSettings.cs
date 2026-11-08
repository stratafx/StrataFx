namespace StrataFx.Model;

/// <summary>What a customizable model value belongs to.</summary>
public enum ModelSettingScope
{
    App,
    Entity,
    Property,
}

/// <summary>The kind of value a model setting holds, which decides how it is edited.</summary>
public enum ModelSettingType
{
    /// <summary>Text that must not be empty (captions).</summary>
    Text,

    /// <summary>Text that may be empty; empty means "none" (description, group, format, icon, editor).</summary>
    OptionalText,

    Integer,
    Boolean,

    /// <summary>One of a fixed set of names (collection kind, lookup property).</summary>
    Choice,
}

/// <summary>
/// Identifies one customizable value of the model: the app title, a setting of an entity, or a setting of a property.
/// Entity ids and property names are the model's own spelling ("customer", "Email"), so keys compare ordinally.
/// </summary>
public readonly record struct ModelValueKey(string? Entity, string? Property, string Setting)
{
    public static ModelValueKey ForApp(string setting) => new(null, null, setting);
    public static ModelValueKey For(EntityModel entity, string setting) => new(entity.Id, null, setting);
    public static ModelValueKey For(PropertyModel property, string setting) => new(property.Owner.Id, property.Name, setting);

    public ModelSettingScope Scope => Property is not null ? ModelSettingScope.Property
        : Entity is not null ? ModelSettingScope.Entity
        : ModelSettingScope.App;

    public override string ToString() => string.Join(".", new[] { Entity, Property, Setting }.Where(s => s is not null));
}

/// <summary>
/// One value that model layers (JSON, the database layer, the designer) may change. The key is the name used in
/// JSON layers, e.g. "pluralCaption". Values are normalized: text is a string (or null), integers are int,
/// booleans are bool and choices are their name.
/// </summary>
public sealed class ModelSetting
{
    private readonly Func<AppModel, EntityModel?, PropertyModel?, object?> _read;
    private readonly Func<PropertyModel, bool>? _appliesTo;
    private readonly Func<EntityModel?, PropertyModel?, string?>? _notEditable;

    internal ModelSetting(string key, string label, ModelSettingScope scope, ModelSettingType type,
        Func<AppModel, EntityModel?, PropertyModel?, object?> read, string? help = null,
        Func<PropertyModel, bool>? appliesTo = null, Func<EntityModel?, PropertyModel?, string?>? notEditable = null)
    {
        Key = key;
        Label = label;
        Scope = scope;
        Type = type;
        Help = help;
        _read = read;
        _appliesTo = appliesTo;
        _notEditable = notEditable;
    }

    /// <summary>The name in JSON layers, e.g. "visibleInList".</summary>
    public string Key { get; }

    public string Label { get; }
    public string? Help { get; }
    public ModelSettingScope Scope { get; }
    public ModelSettingType Type { get; }

    /// <summary>True when the setting means something for the property (e.g. collection kind only for collections).</summary>
    public bool AppliesTo(PropertyModel property) => _appliesTo?.Invoke(property) ?? true;

    /// <summary>Why the value can't be changed safely, or null when it can.</summary>
    public string? NotEditableReason(EntityModel? entity, PropertyModel? property) => _notEditable?.Invoke(entity, property);

    /// <summary>The current, normalized value in a model.</summary>
    public object? Read(AppModel model, EntityModel? entity = null, PropertyModel? property = null) => _read(model, entity, property);

    /// <summary>Normalizes a value for comparison and storage: empty optional text is null.</summary>
    public object? Normalize(object? value) => value switch
    {
        string { Length: 0 } when Type == ModelSettingType.OptionalText => null,
        string s when Type == ModelSettingType.Choice && Key == ModelSettings.CollectionKindKey
            && Enum.TryParse<CollectionKind>(s, ignoreCase: true, out var kind) => kind.ToString(),
        long l when Type == ModelSettingType.Integer => (int)l,
        _ => value,
    };

    /// <summary>True when two values mean the same (null and empty optional text are the same).</summary>
    public bool ValueEquals(object? a, object? b) => Equals(Normalize(a), Normalize(b));

    public override string ToString() => $"{Scope} {Key}";
}

/// <summary>
/// The model values that layers can customize: the same set a JSON layer understands. Used by provenance (which layer
/// set what), the database layer and the model designer.
/// </summary>
public static class ModelSettings
{
    public const string TitleKey = "title";
    public const string CollectionKindKey = "collectionKind";
    public const string LookupPropertyKey = "lookupProperty";
    public const string EditorKey = "editor";

    public static ModelSetting Title { get; } = new(TitleKey, "Application title", ModelSettingScope.App, ModelSettingType.Text,
        (m, _, _) => m.Title, "Shown in the app bar and the browser tab.");

    public static IReadOnlyList<ModelSetting> Entity { get; } =
    [
        new("caption", "Caption", ModelSettingScope.Entity, ModelSettingType.Text, (_, e, _) => e!.Caption,
            "The name of one record, e.g. \"Customer\"."),
        new("pluralCaption", "Plural caption", ModelSettingScope.Entity, ModelSettingType.Text, (_, e, _) => e!.PluralCaption,
            "The list title and navigation link, e.g. \"Customers\"."),
        new("navigationGroup", "Navigation group", ModelSettingScope.Entity, ModelSettingType.Text, (_, e, _) => e!.NavigationGroup),
        new("icon", "Icon", ModelSettingScope.Entity, ModelSettingType.OptionalText, (_, e, _) => e!.Icon),
        new("order", "Order", ModelSettingScope.Entity, ModelSettingType.Integer, (_, e, _) => e!.Order,
            "Position in its navigation group; lower comes first."),
        new("visibleInNavigation", "Visible in navigation", ModelSettingScope.Entity, ModelSettingType.Boolean, (_, e, _) => e!.VisibleInNavigation),
        new(LookupPropertyKey, "Lookup property", ModelSettingScope.Entity, ModelSettingType.Choice, (_, e, _) => e!.LookupProperty?.Name,
            "The text that stands for a record in lookups, titles and reference columns."),
    ];

    public static IReadOnlyList<ModelSetting> Property { get; } =
    [
        new("caption", "Caption", ModelSettingScope.Property, ModelSettingType.Text, (_, _, p) => p!.Caption),
        new("description", "Description", ModelSettingScope.Property, ModelSettingType.OptionalText, (_, _, p) => p!.Description),
        new("group", "Group", ModelSettingScope.Property, ModelSettingType.OptionalText, (_, _, p) => p!.Group,
            "Fields with the same group are shown together under a heading in forms."),
        new("order", "Order", ModelSettingScope.Property, ModelSettingType.Integer, (_, _, p) => p!.Order,
            "Position in lists and forms; lower comes first."),
        new("visibleInList", "Visible in list", ModelSettingScope.Property, ModelSettingType.Boolean, (_, _, p) => p!.VisibleInList,
            "Whether lists show the column by default. Users can still change their own columns.",
            notEditable: (_, p) => p!.EditorKind == EditorKind.Collection ? "Collections can't be list columns."
                : p.IsForeignKey ? ForeignKeyReason(p) : null),
        new("visibleInDetail", "Visible in form", ModelSettingScope.Property, ModelSettingType.Boolean, (_, _, p) => p!.VisibleInDetail,
            notEditable: (_, p) => p!.IsForeignKey ? ForeignKeyReason(p) : null),
        new("readOnly", "Read-only", ModelSettingScope.Property, ModelSettingType.Boolean, (_, _, p) => p!.ReadOnly,
            notEditable: (_, p) => p!.IsKey ? "The key is never editable."
                : p.EditorKind != EditorKind.Collection && p.Info.SetMethod is not { IsPublic: true } ? "The property has no public setter, so it can't be edited."
                : p.IsForeignKey ? ForeignKeyReason(p) : null),
        new("displayFormat", "Display format", ModelSettingScope.Property, ModelSettingType.OptionalText, (_, _, p) => p!.DisplayFormat,
            "A .NET format string such as N2, C or yyyy-MM-dd.",
            appliesTo: p => p.EditorKind is EditorKind.Integer or EditorKind.Decimal or EditorKind.Date or EditorKind.DateTime),
        new(EditorKey, "Editor", ModelSettingScope.Property, ModelSettingType.OptionalText, (_, _, p) => p!.EditorName,
            "A named editor registered in the UI. Empty uses the editor for the property's type."),
        new("lines", "Lines", ModelSettingScope.Property, ModelSettingType.Integer, (_, _, p) => p!.Lines,
            "Height of the text box in forms.",
            appliesTo: p => p.EditorKind is EditorKind.Text or EditorKind.MultilineText),
        new(CollectionKindKey, "Collection kind", ModelSettingScope.Property, ModelSettingType.Choice, (_, _, p) => p!.CollectionKind.ToString(),
            "Owned items are edited in the parent's form and saved with it; related records are separate. The database mapping must match.",
            appliesTo: p => p.EditorKind == EditorKind.Collection),
    ];

    /// <summary>Every setting for a scope, in the order JSON layers and the designer list them.</summary>
    public static IReadOnlyList<ModelSetting> For(ModelSettingScope scope) => scope switch
    {
        ModelSettingScope.App => [Title],
        ModelSettingScope.Entity => Entity,
        _ => Property,
    };

    /// <summary>Finds a setting by its JSON name, ignoring case like JSON layers do.</summary>
    public static ModelSetting? Find(ModelSettingScope scope, string key) =>
        For(scope).FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    public static ModelSetting Get(ModelValueKey key) =>
        Find(key.Scope, key.Setting) ?? throw new ArgumentException($"'{key.Setting}' is not a model setting.", nameof(key));

    /// <summary>The value of a key in a model, or null when the entity or property doesn't exist.</summary>
    public static bool TryRead(AppModel model, ModelValueKey key, out object? value)
    {
        value = null;
        var setting = Find(key.Scope, key.Setting);
        if (setting is null) return false;
        if (key.Scope == ModelSettingScope.App)
        {
            value = setting.Read(model);
            return true;
        }
        var entity = model.FindEntity(key.Entity!);
        if (entity is null) return false;
        if (key.Scope == ModelSettingScope.Entity)
        {
            value = setting.Read(model, entity);
            return true;
        }
        var property = entity.FindProperty(key.Property!);
        if (property is null || !setting.AppliesTo(property)) return false;
        value = setting.Read(model, entity, property);
        return true;
    }

    /// <summary>Every customizable value of the model, keyed by <see cref="ModelValueKey"/>.</summary>
    public static Dictionary<ModelValueKey, object?> Snapshot(AppModel model)
    {
        var values = new Dictionary<ModelValueKey, object?> { [ModelValueKey.ForApp(TitleKey)] = model.Title };
        foreach (var entity in model.Entities)
        {
            foreach (var setting in Entity)
                values[ModelValueKey.For(entity, setting.Key)] = setting.Read(model, entity);
            foreach (var property in entity.Properties)
                foreach (var setting in Property.Where(s => s.AppliesTo(property)))
                    values[ModelValueKey.For(property, setting.Key)] = setting.Read(model, entity, property);
        }
        return values;
    }

    private static string ForeignKeyReason(PropertyModel foreignKey)
    {
        var reference = foreignKey.Owner.References.FirstOrDefault(r =>
            string.Equals(r.Name + "Id", foreignKey.Name, StringComparison.OrdinalIgnoreCase));
        return reference is null
            ? "Foreign keys are set through their reference; change the reference instead."
            : $"Set through the {reference.Caption} reference; change that instead.";
    }
}
