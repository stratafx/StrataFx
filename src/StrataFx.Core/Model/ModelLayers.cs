using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrataFx.Model;

/// <summary>
/// One stratum of model customization, applied on top of the model built from code.
/// Layers run in registration order, so later layers win.
/// </summary>
public interface IModelLayer
{
    string Name { get; }
    void Apply(AppModel model);
}

/// <summary>A layer written in code, e.g. from a module.</summary>
public sealed class DelegateModelLayer(string name, Action<AppModel> apply) : IModelLayer
{
    public string Name { get; } = name;
    public void Apply(AppModel model) => apply(model);
}

/// <summary>
/// A layer read from JSON text. Lets admins or deployments change captions, visibility, order and navigation
/// without recompiling. Unknown entity or property names are reported as errors so typos are caught early.
/// Empty text clears optional values (description, group, display format, icon, editor).
/// </summary>
/// <example>
/// {
///   "title": "Contoso CRM",
///   "entities": {
///     "customer": {
///       "pluralCaption": "Clients",
///       "navigationGroup": "Sales",
///       "properties": { "Email": { "caption": "E-mail", "visibleInList": false } }
///     }
///   }
/// }
/// </example>
public sealed class JsonModelLayer : IModelLayer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ModelDiff _diff;

    public JsonModelLayer(string name, string json)
    {
        Name = name;
        _diff = JsonSerializer.Deserialize<ModelDiff>(json, Options) ?? new ModelDiff();
    }

    public string Name { get; }

    public static JsonModelLayer FromFile(string path) => new(Path.GetFileName(path), File.ReadAllText(path));

    public void Apply(AppModel model)
    {
        if (_diff.Title is not null) model.Title = _diff.Title;

        foreach (var (entityId, e) in _diff.Entities ?? [])
        {
            var entity = model.FindEntity(entityId)
                ?? throw new InvalidOperationException($"Model layer '{Name}': unknown entity '{entityId}'.");

            if (e.Caption is not null) entity.Caption = e.Caption;
            if (e.PluralCaption is not null) entity.PluralCaption = e.PluralCaption;
            if (e.NavigationGroup is not null) entity.NavigationGroup = e.NavigationGroup;
            if (e.Icon is not null) entity.Icon = NullIfEmpty(e.Icon);
            if (e.Order is { } order) entity.Order = order;
            if (e.VisibleInNavigation is { } nav) entity.VisibleInNavigation = nav;
            if (e.LookupProperty is not null)
                entity.LookupProperty = entity.FindProperty(e.LookupProperty)
                    ?? throw new InvalidOperationException($"Model layer '{Name}': unknown property '{entityId}.{e.LookupProperty}'.");

            foreach (var (propName, p) in e.Properties ?? [])
            {
                var prop = entity.FindProperty(propName)
                    ?? throw new InvalidOperationException($"Model layer '{Name}': unknown property '{entityId}.{propName}'.");

                if (p.Caption is not null) prop.Caption = p.Caption;
                if (p.Description is not null) prop.Description = NullIfEmpty(p.Description);
                if (p.Group is not null) prop.Group = NullIfEmpty(p.Group);
                if (p.Order is { } o) prop.Order = o;
                if (p.VisibleInList is { } vl) prop.VisibleInList = vl;
                if (p.VisibleInDetail is { } vd) prop.VisibleInDetail = vd;
                if (p.ReadOnly is { } ro) prop.ReadOnly = ro || prop.IsKey;
                if (p.DisplayFormat is not null) prop.DisplayFormat = NullIfEmpty(p.DisplayFormat);
                if (p.Editor is not null) prop.EditorName = NullIfEmpty(p.Editor); // "" goes back to the type's editor
                if (p.Lines is { } lines) prop.Lines = lines;
                if (p.CollectionKind is { } collectionKind)
                {
                    if (prop.EditorKind != EditorKind.Collection)
                        throw new InvalidOperationException($"Model layer '{Name}': '{entityId}.{propName}' is not a collection.");
                    prop.CollectionKind = collectionKind;
                    if (collectionKind == CollectionKind.Owned) prop.ElementEntity!.VisibleInNavigation = false;
                }
            }
        }
    }

    /// <summary>Empty text clears an optional value ("group": "" means no group), so a later layer can undo an earlier one.</summary>
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed class ModelDiff
    {
        public string? Title { get; set; }
        public Dictionary<string, EntityDiff>? Entities { get; set; }
    }

    private sealed class EntityDiff
    {
        public string? Caption { get; set; }
        public string? PluralCaption { get; set; }
        public string? NavigationGroup { get; set; }
        public string? Icon { get; set; }
        public int? Order { get; set; }
        public bool? VisibleInNavigation { get; set; }
        public string? LookupProperty { get; set; }
        public Dictionary<string, PropertyDiff>? Properties { get; set; }
    }

    private sealed class PropertyDiff
    {
        public string? Caption { get; set; }
        public string? Description { get; set; }
        public string? Group { get; set; }
        public int? Order { get; set; }
        public bool? VisibleInList { get; set; }
        public bool? VisibleInDetail { get; set; }
        public bool? ReadOnly { get; set; }
        public string? DisplayFormat { get; set; }
        public string? Editor { get; set; }
        public int? Lines { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter<CollectionKind>))]
        public CollectionKind? CollectionKind { get; set; }
    }
}

/// <summary>A model layer failed while the model was built, e.g. a JSON layer names an unknown property.</summary>
public sealed class ModelLayerException(string layerName, Exception inner)
    : InvalidOperationException(inner.Message.StartsWith("Model layer ", StringComparison.Ordinal) ? inner.Message : $"Model layer '{layerName}': {inner.Message}", inner)
{
    /// <summary>The <see cref="IModelLayer.Name"/> of the layer that failed.</summary>
    public string LayerName { get; } = layerName;
}

/// <summary>
/// A JSON layer backed by a file (model.json), read each time the model is built, so a rebuild picks up changes
/// written by the model designer or by hand. A missing file is an empty layer.
/// </summary>
public sealed class JsonFileModelLayer : IModelLayer
{
    private readonly object _gate = new();
    private string? _text;
    private JsonModelLayer? _parsed;

    public JsonFileModelLayer(string path, string? name = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = name ?? System.IO.Path.GetFileName(path);
    }

    public string Name { get; }

    /// <summary>The full path of the file.</summary>
    public string Path { get; }

    public void Apply(AppModel model)
    {
        var text = File.Exists(Path) ? File.ReadAllText(Path) : null;
        if (string.IsNullOrWhiteSpace(text)) return;
        JsonModelLayer layer;
        lock (_gate)
        {
            if (_parsed is null || text != _text)
            {
                try
                {
                    _parsed = new JsonModelLayer(Name, text);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Model layer '{Name}' is not valid JSON: {ex.Message}", ex);
                }
                _text = text;
            }
            layer = _parsed;
        }
        layer.Apply(model);
    }
}

/// <summary>
/// The database layer: model changes saved by the model designer outside development, stored as JSON (the same
/// format as model.json) in the <see cref="Settings.IUserSettingsStore"/> under <see cref="Settings.UserSettings.SharedUserName"/>
/// and <see cref="SettingKey"/>. It is always the last model layer, so it wins over code, modules and JSON files;
/// each user's own list layouts still apply on top of it. Its content is loaded at startup and replaced on save.
/// </summary>
public sealed class DatabaseModelLayer : IModelLayer
{
    public const string SettingKey = "strata/model";
    public const string LayerName = "Database";

    private volatile JsonModelLayer? _layer;

    public string Name => LayerName;

    /// <summary>The JSON last loaded or saved; null when the database has none.</summary>
    public string? Content { get; private set; }

    /// <summary>Why the stored content was set aside (it doesn't apply to the model), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Replaces the content. Invalid JSON is kept and reported when the model is built.</summary>
    public void SetContent(string? json)
    {
        Content = string.IsNullOrWhiteSpace(json) ? null : json;
        Problem = null;
        _layer = null;
    }

    /// <summary>
    /// Sets the stored content aside because it doesn't apply (e.g. it names a property that was renamed in code), so
    /// the app runs without it until the designer saves a new one.
    /// </summary>
    public void SetAside(string problem)
    {
        Content = null;
        _layer = null;
        Problem = problem;
    }

    public void Apply(AppModel model)
    {
        if (Content is not { } json) return;
        var layer = _layer;
        if (layer is null)
        {
            try
            {
                _layer = layer = new JsonModelLayer(Name, json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Model layer '{Name}' is not valid JSON: {ex.Message}", ex);
            }
        }
        layer.Apply(model);
    }

    /// <summary>Reads the saved content from the store.</summary>
    public async Task LoadAsync(Settings.IUserSettingsStore store, CancellationToken cancellationToken = default) =>
        SetContent(await store.GetAsync(Settings.UserSettings.SharedUserName, SettingKey, cancellationToken));
}
