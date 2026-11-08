using System.Text.Encodings.Web;
using System.Text.Json;

namespace StrataFx.Model.Design;

/// <summary>One value that differs between two versions of a layer.</summary>
/// <param name="OldValue">The value before, or null when the layer didn't set it.</param>
/// <param name="NewValue">The value after, or null when the layer no longer sets it.</param>
public sealed record ModelLayerChange(ModelValueKey Key, bool WasSet, object? OldValue, bool IsSet, object? NewValue)
{
    public bool IsAdded => !WasSet && IsSet;
    public bool IsRemoved => WasSet && !IsSet;
}

/// <summary>
/// The values one JSON-format layer sets (model.json or the database layer), keyed by <see cref="ModelValueKey"/>
/// with the model's spelling of entity and property names. Only values the model designer manages are read;
/// anything else in the JSON is left alone. Names the model doesn't know are listed in <see cref="Problems"/>.
/// </summary>
public sealed class ModelLayerDocument
{
    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "Société" stays readable
    };

    private readonly Dictionary<ModelValueKey, object?> _values = [];
    private readonly List<string> _problems = [];

    public IReadOnlyDictionary<ModelValueKey, object?> Values => _values;

    /// <summary>Entities, properties or values the model doesn't know (typos, or renamed in code).</summary>
    public IReadOnlyList<string> Problems => _problems;

    public int Count => _values.Count;

    public bool Contains(ModelValueKey key) => _values.ContainsKey(key);

    public bool TryGetValue(ModelValueKey key, out object? value) => _values.TryGetValue(key, out value);

    public void Set(ModelValueKey key, object? value) => _values[key] = ModelSettings.Get(key).Normalize(value);

    public bool Remove(ModelValueKey key) => _values.Remove(key);

    public ModelLayerDocument Clone()
    {
        var copy = new ModelLayerDocument();
        foreach (var (key, value) in _values) copy._values[key] = value;
        copy._problems.AddRange(_problems);
        return copy;
    }

    /// <summary>Reads the values a JSON layer sets. Empty or missing JSON is an empty document.</summary>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static ModelLayerDocument Parse(string? json, AppModel model)
    {
        var document = new ModelLayerDocument();
        if (string.IsNullOrWhiteSpace(json)) return document;

        using var parsed = JsonDocument.Parse(json, ReadOptions);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A model layer must be a JSON object.");

        foreach (var member in root.EnumerateObject())
        {
            if (Is(member, ModelSettings.TitleKey)) document.Read(ModelValueKey.ForApp(ModelSettings.TitleKey), member.Value);
            else if (Is(member, "entities") && member.Value.ValueKind == JsonValueKind.Object)
                foreach (var entityMember in member.Value.EnumerateObject())
                    document.ReadEntity(model, entityMember);
        }
        document.Canonicalize(model);
        return document;
    }

    private void ReadEntity(AppModel model, JsonProperty member)
    {
        var entity = model.FindEntity(member.Name);
        if (entity is null)
        {
            _problems.Add($"Unknown entity '{member.Name}'.");
            return;
        }
        if (member.Value.ValueKind != JsonValueKind.Object) return;

        foreach (var value in member.Value.EnumerateObject())
        {
            if (Is(value, "properties") && value.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyMember in value.Value.EnumerateObject())
                {
                    var property = entity.FindProperty(propertyMember.Name);
                    if (property is null)
                    {
                        _problems.Add($"Unknown property '{entity.Id}.{propertyMember.Name}'.");
                        continue;
                    }
                    if (propertyMember.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var setting in propertyMember.Value.EnumerateObject())
                        if (ModelSettings.Find(ModelSettingScope.Property, setting.Name) is { } s)
                            Read(ModelValueKey.For(property, s.Key), setting.Value);
                }
            }
            else if (ModelSettings.Find(ModelSettingScope.Entity, value.Name) is { } s)
            {
                Read(ModelValueKey.For(entity, s.Key), value.Value);
                if (s.Key == ModelSettings.LookupPropertyKey && value.Value.ValueKind == JsonValueKind.String
                    && entity.FindProperty(value.Value.GetString()!) is null)
                    _problems.Add($"Unknown property '{entity.Id}.{value.Value.GetString()}' as the lookup property.");
            }
        }
    }

    private void Read(ModelValueKey key, JsonElement element)
    {
        var setting = ModelSettings.Get(key);
        object? value = (setting.Type, element.ValueKind) switch
        {
            (ModelSettingType.Text or ModelSettingType.OptionalText or ModelSettingType.Choice, JsonValueKind.String) => element.GetString(),
            (ModelSettingType.Integer, JsonValueKind.Number) when element.TryGetInt32(out var i) => i,
            (ModelSettingType.Boolean, JsonValueKind.True) => true,
            (ModelSettingType.Boolean, JsonValueKind.False) => false,
            (_, JsonValueKind.Null) => Skip.Value, // null means "not set" to a JSON layer
            _ => Invalid.Value,
        };
        if (value is Invalid)
        {
            _problems.Add($"'{key}' has a value of the wrong type ({element.ValueKind}).");
            return;
        }
        if (value is Skip) return;
        _values[key] = setting.Normalize(value);
    }

    /// <summary>Uses the model's spelling for names stored as values (the lookup property).</summary>
    private void Canonicalize(AppModel model)
    {
        foreach (var key in _values.Keys.Where(k => k.Setting == ModelSettings.LookupPropertyKey).ToList())
            if (_values[key] is string name && model.FindEntity(key.Entity!)?.FindProperty(name) is { } property)
                _values[key] = property.Name;
    }

    private sealed class Skip { public static readonly Skip Value = new(); }
    private sealed class Invalid { public static readonly Invalid Value = new(); }

    private static bool Is(JsonProperty member, string name) => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The document as JSON, sorted predictably: title, then entities in model order, each with its settings in the
    /// order of <see cref="ModelSettings"/> and its properties in model order. Used by the database layer and for new files.
    /// </summary>
    public string ToJson(AppModel model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = WriteOptions.Encoder }))
        {
            writer.WriteStartObject();
            if (_values.TryGetValue(ModelValueKey.ForApp(ModelSettings.TitleKey), out var title))
                WriteValue(writer, ModelSettings.TitleKey, title);

            var entities = _values.Keys.Where(k => k.Entity is not null).Select(k => k.Entity!).Distinct()
                .OrderBy(id => EntityRank(model, id)).ThenBy(id => id, StringComparer.Ordinal).ToList();
            if (entities.Count > 0)
            {
                writer.WriteStartObject("entities");
                foreach (var id in entities)
                {
                    writer.WriteStartObject(id);
                    foreach (var setting in ModelSettings.Entity)
                        if (_values.TryGetValue(new ModelValueKey(id, null, setting.Key), out var value))
                            WriteValue(writer, setting.Key, value);

                    var properties = _values.Keys.Where(k => k.Entity == id && k.Property is not null).Select(k => k.Property!).Distinct()
                        .OrderBy(name => PropertyRank(model, id, name)).ThenBy(name => name, StringComparer.Ordinal).ToList();
                    if (properties.Count > 0)
                    {
                        writer.WriteStartObject("properties");
                        foreach (var name in properties)
                        {
                            writer.WriteStartObject(name);
                            foreach (var setting in ModelSettings.Property)
                                if (_values.TryGetValue(new ModelValueKey(id, name, setting.Key), out var value))
                                    WriteValue(writer, setting.Key, value);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    /// <summary>A JSON layer with exactly these values, e.g. to preview unsaved changes.</summary>
    public IModelLayer ToLayer(string name, AppModel model) => new JsonModelLayer(name, ToJson(model));

    /// <summary>The values that differ between two documents, in a predictable order.</summary>
    public static IReadOnlyList<ModelLayerChange> Diff(ModelLayerDocument before, ModelLayerDocument after, AppModel model)
    {
        var changes = new List<ModelLayerChange>();
        foreach (var key in before._values.Keys.Union(after._values.Keys))
        {
            var wasSet = before._values.TryGetValue(key, out var oldValue);
            var isSet = after._values.TryGetValue(key, out var newValue);
            if (wasSet == isSet && Equals(oldValue, newValue)) continue;
            changes.Add(new ModelLayerChange(key, wasSet, oldValue, isSet, newValue));
        }
        return changes.OrderBy(c => SortKey(model, c.Key)).ToList();
    }

    internal static (int, int, int, int, string) SortKey(AppModel model, ModelValueKey key) => (
        key.Entity is null ? -1 : EntityRank(model, key.Entity),
        key.Property is null ? -1 : PropertyRank(model, key.Entity!, key.Property),
        SettingRank(key),
        0,
        key.ToString());

    internal static int EntityRank(AppModel model, string id) =>
        model.FindEntity(id) is { } e ? IndexOf(model.Entities, e) : int.MaxValue;

    internal static int PropertyRank(AppModel model, string entityId, string name) =>
        model.FindEntity(entityId) is { } e && e.FindProperty(name) is { } p ? e.Properties.IndexOf(p) : int.MaxValue;

    internal static int SettingRank(ModelValueKey key) =>
        ModelSettings.For(key.Scope).Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Key == key.Setting, (null!, int.MaxValue)).Item2;

    private static int IndexOf(IReadOnlyList<EntityModel> list, EntityModel entity)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], entity)) return i;
        return int.MaxValue;
    }

    internal static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        writer.WritePropertyName(name);
        WriteLiteral(writer, value);
    }

    internal static void WriteLiteral(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteStringValue(""); break; // a cleared optional value
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            default: writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    /// <summary>A value as JSON text, e.g. <c>"E-mail"</c>, <c>false</c> or <c>10</c>.</summary>
    public static string ToJsonLiteral(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = WriteOptions.Encoder }))
            WriteLiteral(writer, value);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
