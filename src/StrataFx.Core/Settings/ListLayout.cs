using StrataFx.Model;

namespace StrataFx.Settings;

/// <summary>
/// A user's choices for one list view, as saved: which columns are shown, in what order and how wide, plus the
/// sort and page size. It is applied on top of the model (code, modules, model.json), never changing the shared
/// <see cref="AppModel"/>; see <see cref="ListLayouts.Resolve"/>.
/// </summary>
public sealed class ListLayout
{
    public List<ListLayoutColumn> Columns { get; set; } = [];
    public string? SortProperty { get; set; }
    public bool SortDescending { get; set; }
    public int? PageSize { get; set; }
}

public sealed class ListLayoutColumn
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int Order { get; set; }

    /// <summary>Width in pixels; null means automatic.</summary>
    public int? Width { get; set; }
}

/// <summary>One column a list can show, with the current user's choices for it.</summary>
public sealed class ListColumnState(PropertyModel property, bool visible, int? width)
{
    public PropertyModel Property { get; } = property;
    public string Name => Property.Name;
    public bool Visible { get; set; } = visible;
    public int? Width { get; set; } = width;
}

/// <summary>
/// The layout a list view shows: every column the user can choose from, in order, with visibility and width,
/// plus sort and page size. Mutable, so the view can change it and save it back with <see cref="ToLayout"/>.
/// </summary>
public sealed class ListViewLayout
{
    public required List<ListColumnState> Columns { get; init; }
    public string? SortProperty { get; set; }
    public bool SortDescending { get; set; }
    public int PageSize { get; set; }

    public IEnumerable<ListColumnState> VisibleColumns => Columns.Where(c => c.Visible);

    public ListLayout ToLayout() => new()
    {
        Columns = Columns.Select((c, i) => new ListLayoutColumn { Name = c.Name, Visible = c.Visible, Order = i, Width = c.Width }).ToList(),
        SortProperty = SortProperty,
        SortDescending = SortProperty is not null && SortDescending,
        PageSize = PageSize,
    };

    /// <summary>True when both show the same columns, order, widths, sort and page size.</summary>
    public bool SameAs(ListViewLayout other) =>
        PageSize == other.PageSize
        && string.Equals(SortProperty, other.SortProperty, StringComparison.OrdinalIgnoreCase)
        && (SortProperty is null || SortDescending == other.SortDescending)
        && Columns.Select(c => (c.Name, c.Visible, c.Width)).SequenceEqual(other.Columns.Select(c => (c.Name, c.Visible, c.Width)));
}

/// <summary>Merges saved list layouts with the model, and names the settings they are saved under.</summary>
public static class ListLayouts
{
    public static readonly IReadOnlyList<int> PageSizes = [10, 25, 50, 100];

    public const int MinWidth = 48;
    public const int MaxWidth = 1200;

    /// <summary>
    /// Identifies a list view: the entity id ("customer"), or for a related list inside a form, the parent's
    /// collection ("customer.Orders"), so an embedded list and the full list have separate layouts.
    /// </summary>
    public static string ViewKey(EntityModel entity, PropertyModel? parentCollection = null) =>
        parentCollection is null ? entity.Id : $"{parentCollection.Owner.Id}.{parentCollection.Name}";

    /// <summary>The <see cref="UserSettings"/> key a list view's layout is saved under.</summary>
    public static string SettingKey(string viewKey) => "list/" + viewKey;

    /// <summary>
    /// True for properties users may add to a list: not keys, foreign keys, collections or multi-line text.
    /// </summary>
    public static bool CanShowInList(PropertyModel property) =>
        !property.IsKey && !property.IsForeignKey && property.EditorKind is not (EditorKind.Collection or EditorKind.MultilineText);

    /// <summary>
    /// The columns a user can choose from, in model order: the model's list columns, plus every property that can be
    /// shown in a list. <paramref name="excluded"/> leaves some out, e.g. the parent reference of a related list.
    /// </summary>
    public static IReadOnlyList<PropertyModel> AvailableColumns(EntityModel entity, IEnumerable<string>? excluded = null)
    {
        var skip = new HashSet<string>(excluded ?? [], StringComparer.OrdinalIgnoreCase);
        return entity.Properties
            .Where(p => (p.VisibleInList || CanShowInList(p)) && !skip.Contains(p.Name))
            .OrderBy(p => p.Order)
            .ToList();
    }

    /// <summary>
    /// Applies a saved layout to the entity's columns. Saved columns that no longer exist (or may not be shown) are
    /// ignored; columns the saved layout doesn't know (added to the model later) appear where the model puts them,
    /// visible or not as the model says. A sort or page size that is no longer valid falls back to the default.
    /// So changing the model never breaks a saved layout.
    /// </summary>
    public static ListViewLayout Resolve(EntityModel entity, ListLayout? saved, int defaultPageSize = 25, IEnumerable<string>? excluded = null)
    {
        var available = AvailableColumns(entity, excluded);
        var savedByName = new Dictionary<string, ListLayoutColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in saved?.Columns ?? [])
            savedByName.TryAdd(column.Name, column);

        // Known columns in the saved order, then each new column after its nearest model-order predecessor.
        var ordered = available.Where(p => savedByName.ContainsKey(p.Name))
            .OrderBy(p => savedByName[p.Name].Order)
            .ToList();
        for (var i = 0; i < available.Count; i++)
        {
            var property = available[i];
            if (savedByName.ContainsKey(property.Name)) continue;
            var after = -1;
            for (var j = i - 1; j >= 0 && after < 0; j--)
                after = ordered.IndexOf(available[j]);
            ordered.Insert(after + 1, property);
        }

        var columns = ordered.Select(p => savedByName.TryGetValue(p.Name, out var s)
                ? new ListColumnState(p, s.Visible, ClampWidth(s.Width))
                : new ListColumnState(p, p.VisibleInList, null))
            .ToList();

        // A list with no columns is useless; show the model's columns instead.
        if (!columns.Any(c => c.Visible))
            foreach (var c in columns) c.Visible = c.Property.VisibleInList;

        var sort = saved?.SortProperty is { } name ? available.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
        return new ListViewLayout
        {
            Columns = columns,
            SortProperty = sort?.Name,
            SortDescending = sort is not null && saved!.SortDescending,
            PageSize = saved?.PageSize is { } size && PageSizes.Contains(size) ? size : defaultPageSize,
        };
    }

    public static int? ClampWidth(int? width) => width is { } w ? Math.Clamp(w, MinWidth, MaxWidth) : null;
}
