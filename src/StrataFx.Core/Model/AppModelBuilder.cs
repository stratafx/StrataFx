using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using StrataFx.Annotations;

namespace StrataFx.Model;

/// <summary>
/// Builds the <see cref="AppModel"/>: reads entity classes and their attributes (the base stratum),
/// then applies each <see cref="IModelLayer"/> in order (modules, JSON files, user settings).
/// </summary>
public sealed class AppModelBuilder
{
    private static readonly string[] LookupNameCandidates = ["Name", "Title", "Caption", "Code", "DisplayName"];

    private readonly List<Type> _entityTypes = [];
    private readonly List<IModelLayer> _layers = [];

    public string Title { get; set; } = "StrataFx App";

    public IReadOnlyList<Type> EntityTypes => _entityTypes;
    public IReadOnlyList<IModelLayer> Layers => _layers;

    public AppModelBuilder AddEntity<T>() where T : class => AddEntity(typeof(T));

    public AppModelBuilder AddEntity(Type type)
    {
        if (!type.IsClass || type.IsAbstract)
            throw new ArgumentException($"'{type.Name}' must be a non-abstract class to be a StrataFx entity.", nameof(type));
        if (type.GetConstructor(Type.EmptyTypes) is null)
            throw new ArgumentException($"'{type.Name}' needs a public parameterless constructor.", nameof(type));
        if (!_entityTypes.Contains(type)) _entityTypes.Add(type);
        return this;
    }

    /// <summary>Adds every class in the assembly marked with <see cref="StrataEntityAttribute"/>.</summary>
    public AppModelBuilder AddEntitiesFrom(Assembly assembly)
    {
        foreach (var t in assembly.GetTypes().Where(t => t.GetCustomAttribute<StrataEntityAttribute>() is not null))
            AddEntity(t);
        return this;
    }

    public AppModelBuilder AddLayer(IModelLayer layer)
    {
        _layers.Add(layer);
        return this;
    }

    public AppModel Build()
    {
        var entities = new List<EntityModel>();
        var byType = new Dictionary<Type, EntityModel>();

        foreach (var type in _entityTypes)
        {
            var id = type.Name.ToLowerInvariant();
            if (entities.Any(e => e.Id == id))
                throw new InvalidOperationException($"Two entities resolve to the same id '{id}'. Rename one of them.");
            var entity = CreateEntity(type, id);
            entities.Add(entity);
            byType[type] = entity;
        }

        var nullability = new NullabilityInfoContext();
        foreach (var entity in entities)
            AddProperties(entity, byType, nullability);

        var model = new AppModel(Title, entities);
        foreach (var layer in _layers)
            layer.Apply(model);
        return model;
    }

    private static EntityModel CreateEntity(Type type, string id)
    {
        var attr = type.GetCustomAttribute<StrataEntityAttribute>();
        var caption = attr?.Caption ?? Humanizer.SplitWords(type.Name);
        return new EntityModel(type, id)
        {
            Caption = caption,
            PluralCaption = attr?.PluralCaption ?? Humanizer.Pluralize(caption),
            NavigationGroup = attr?.NavigationGroup ?? "Default",
            Icon = attr?.Icon,
            Order = attr?.Order ?? 0,
        };
    }

    private static void AddProperties(EntityModel entity, Dictionary<Type, EntityModel> entities, NullabilityInfoContext nullability)
    {
        var type = entity.ClrType;
        var infos = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
            .OrderBy(p => p.MetadataToken) // declaration order
            .ToList();

        // Foreign-key scalars that belong to a reference navigation are hidden: the reference editor sets them.
        var foreignKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in infos.Where(p => entities.ContainsKey(p.PropertyType)))
        {
            foreignKeys.Add(p.GetCustomAttribute<ForeignKeyAttribute>()?.Name ?? p.Name + "Id");
        }
        foreach (var p in infos)
        {
            if (p.GetCustomAttribute<ForeignKeyAttribute>() is { } fk && entities.ContainsKey(type.GetProperty(fk.Name)?.PropertyType ?? typeof(void)))
                foreignKeys.Add(p.Name);
        }

        var index = 0;
        foreach (var info in infos)
        {
            index++;
            var kind = ResolveKind(info, entities, out var referenceEntity);
            if (kind is null) continue; // collections and unsupported complex types are not shown (yet)

            var prop = new PropertyModel(entity, info)
            {
                EditorKind = kind.Value,
                ReferenceEntity = referenceEntity,
                Caption = Humanizer.SplitWords(info.Name),
                Order = index * 10,
                ReadOnly = info.SetMethod is null || !info.SetMethod.IsPublic,
            };
            ApplyAnnotations(prop, info);

            if (kind == EditorKind.Reference && !prop.Required)
            {
                var n = nullability.Create(info);
                prop.Required = n.WriteState == NullabilityState.NotNull;
            }
            if (foreignKeys.Contains(info.Name))
            {
                prop.VisibleInList = false;
                prop.VisibleInDetail = false;
            }
            entity.Properties.Add(prop);
        }

        var key = entity.Properties.FirstOrDefault(p => p.Info.GetCustomAttribute<KeyAttribute>() is not null)
                  ?? entity.FindProperty("Id")
                  ?? entity.FindProperty(type.Name + "Id")
                  ?? throw new InvalidOperationException(
                      $"'{type.Name}' has no key. Add a property named Id or {type.Name}Id, or mark one with [Key].");
        entity.Key = key;
        key.ReadOnly = true;
        key.VisibleInList = false;
        key.VisibleInDetail = key.UnderlyingType == typeof(string); // generated numeric/Guid keys are hidden

        var lookupName = type.GetCustomAttribute<LookupTextAttribute>()?.PropertyName;
        entity.LookupProperty = lookupName is not null
            ? entity.FindProperty(lookupName) ?? throw new InvalidOperationException($"[LookupText] on '{type.Name}' names unknown property '{lookupName}'.")
            : LookupNameCandidates.Select(entity.FindProperty).FirstOrDefault(p => p is not null)
              ?? entity.Properties.FirstOrDefault(p => p.UnderlyingType == typeof(string) && !p.IsKey);
    }

    private static EditorKind? ResolveKind(PropertyInfo info, Dictionary<Type, EntityModel> entities, out EntityModel? reference)
    {
        reference = null;
        var t = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;

        if (info.GetCustomAttribute<PropertyEditorAttribute>() is not null) return EditorKind.Custom;
        if (entities.TryGetValue(t, out var refEntity)) { reference = refEntity; return EditorKind.Reference; }
        if (t == typeof(string))
        {
            var dataType = info.GetCustomAttribute<DataTypeAttribute>()?.DataType;
            return info.GetCustomAttribute<MultilineAttribute>() is not null || dataType == DataType.MultilineText
                ? EditorKind.MultilineText
                : EditorKind.Text;
        }
        if (t == typeof(bool)) return EditorKind.Boolean;
        if (t.IsEnum) return EditorKind.Enum;
        if (t == typeof(Guid)) return EditorKind.Guid;
        if (t == typeof(DateOnly)) return EditorKind.Date;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
            return info.GetCustomAttribute<DataTypeAttribute>()?.DataType == DataType.Date ? EditorKind.Date : EditorKind.DateTime;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) return EditorKind.Integer;
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return EditorKind.Decimal;
        if (typeof(IEnumerable).IsAssignableFrom(t)) return null;
        return null;
    }

    private static void ApplyAnnotations(PropertyModel prop, PropertyInfo info)
    {
        if (info.GetCustomAttribute<DisplayAttribute>() is { } display)
        {
            prop.Caption = display.GetName() ?? prop.Caption;
            prop.Description = display.GetDescription();
            prop.Group = display.GetGroupName();
            if (display.GetOrder() is { } order) prop.Order = order;
        }
        else if (info.GetCustomAttribute<DisplayNameAttribute>() is { } displayName)
        {
            prop.Caption = displayName.DisplayName;
        }

        if (info.GetCustomAttribute<RequiredAttribute>() is not null) prop.Required = true;
        if (info.GetCustomAttribute<StringLengthAttribute>() is { } sl) prop.MaxLength = sl.MaximumLength;
        if (info.GetCustomAttribute<MaxLengthAttribute>() is { } ml && ml.Length > 0) prop.MaxLength = ml.Length;
        if (info.GetCustomAttribute<EditableAttribute>() is { AllowEdit: false }) prop.ReadOnly = true;
        if (info.GetCustomAttribute<ReadOnlyAttribute>() is { IsReadOnly: true }) prop.ReadOnly = true;
        if (info.GetCustomAttribute<MultilineAttribute>() is { } multiline) prop.Lines = multiline.Lines;
        else if (prop.EditorKind == EditorKind.MultilineText) prop.Lines = 4;

        if (info.GetCustomAttribute<DisplayFormatStringAttribute>() is { } fmt) prop.DisplayFormat = fmt.Format;
        else if (info.GetCustomAttribute<DisplayFormatAttribute>()?.DataFormatString is { } dataFormat)
            prop.DisplayFormat = UnwrapFormat(dataFormat);

        if (info.GetCustomAttribute<PropertyEditorAttribute>() is { } editor) prop.EditorName = editor.EditorName;

        if (info.GetCustomAttribute<HideInAttribute>() is { } hide)
        {
            if (hide.Views.HasFlag(ViewKinds.List)) prop.VisibleInList = false;
            if (hide.Views.HasFlag(ViewKinds.Detail)) prop.VisibleInDetail = false;
        }
        if (prop.EditorKind == EditorKind.MultilineText) prop.VisibleInList = false;
    }

    /// <summary>"{0:N2}" → "N2" so the format can be passed to IFormattable.ToString.</summary>
    private static string? UnwrapFormat(string format)
    {
        var start = format.IndexOf(":", StringComparison.Ordinal);
        var end = format.LastIndexOf('}');
        return start >= 0 && end > start ? format[(start + 1)..end] : null;
    }
}
