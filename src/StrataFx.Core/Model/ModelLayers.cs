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
/// A layer read from JSON. Lets admins or deployments change captions, visibility, order and navigation
/// without recompiling. Unknown entity or property names are reported as errors so typos are caught early.
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
            if (e.Icon is not null) entity.Icon = e.Icon;
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
                if (p.Description is not null) prop.Description = p.Description;
                if (p.Group is not null) prop.Group = p.Group;
                if (p.Order is { } o) prop.Order = o;
                if (p.VisibleInList is { } vl) prop.VisibleInList = vl;
                if (p.VisibleInDetail is { } vd) prop.VisibleInDetail = vd;
                if (p.ReadOnly is { } ro) prop.ReadOnly = ro || prop.IsKey;
                if (p.DisplayFormat is not null) prop.DisplayFormat = p.DisplayFormat;
                if (p.Editor is not null) prop.EditorName = p.Editor;
                if (p.Lines is { } lines) prop.Lines = lines;
            }
        }
    }

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
    }
}
