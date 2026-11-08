using System.Reflection;
using MudBlazor;
using StrataFx.Model;

namespace StrataFx.Designer.Components;

/// <summary>Icons for the designer: a searchable catalog of Material icons for entities, and icons for property kinds.</summary>
public static class DesignerIcons
{
    private static readonly Lazy<IReadOnlyList<(string Name, string Svg)>> Catalog = new(() =>
        Read(typeof(Icons.Material.Outlined), "Outlined").Concat(Read(typeof(Icons.Material.Filled), "Filled")).ToList());

    private static readonly Lazy<Dictionary<string, string>> Names = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, svg) in Catalog.Value) names.TryAdd(svg, name);
        return names;
    });

    /// <summary>"Outlined.People" for an icon's SVG, or null for an icon not in the catalog.</summary>
    public static string? NameOf(string? svg) => svg is null ? null : Names.Value.GetValueOrDefault(svg);

    public static IEnumerable<string> Search(string? text, int max = 60) =>
        Catalog.Value
            .Where(i => string.IsNullOrWhiteSpace(text) || i.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .Select(i => i.Svg);

    public static string ForProperty(PropertyModel property) => property.IsKey ? Icons.Material.Outlined.Key : property.EditorKind switch
    {
        EditorKind.Text => Icons.Material.Outlined.TextFields,
        EditorKind.MultilineText => Icons.Material.Outlined.Notes,
        EditorKind.Integer => Icons.Material.Outlined.Numbers,
        EditorKind.Decimal => Icons.Material.Outlined.Calculate,
        EditorKind.Boolean => Icons.Material.Outlined.ToggleOn,
        EditorKind.Date => Icons.Material.Outlined.CalendarToday,
        EditorKind.DateTime => Icons.Material.Outlined.Schedule,
        EditorKind.Enum => Icons.Material.Outlined.List,
        EditorKind.Guid => Icons.Material.Outlined.Fingerprint,
        EditorKind.Reference => Icons.Material.Outlined.Link,
        EditorKind.Collection when property.CollectionKind == CollectionKind.Owned => Icons.Material.Outlined.ViewList,
        EditorKind.Collection => Icons.Material.Outlined.AccountTree,
        _ => Icons.Material.Outlined.Extension,
    };

    /// <summary>What a property is, in a few words: "Text", "Reference to Customer", "Owned Order line items".</summary>
    public static string KindText(PropertyModel property) => property.EditorKind switch
    {
        EditorKind.Reference => $"Reference to {property.ReferenceEntity!.Caption}",
        EditorKind.Collection => $"{property.CollectionKind} {property.ElementEntity!.PluralCaption.ToLowerInvariant()}",
        EditorKind.MultilineText => "Multi-line text",
        EditorKind.DateTime => "Date and time",
        _ => property.EditorKind.ToString(),
    } + (property.IsKey ? " · key" : property.IsForeignKey ? " · foreign key" : "");

    private static IEnumerable<(string, string)> Read(Type type, string prefix) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => ($"{prefix}.{f.Name}", (string)f.GetValue(null)!))
            .OrderBy(i => i.Item1, StringComparer.Ordinal);
}
