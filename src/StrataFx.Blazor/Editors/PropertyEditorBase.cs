using Microsoft.AspNetCore.Components;
using StrataFx.Model;

namespace StrataFx.Blazor.Editors;

/// <summary>
/// Base class for property editors. An editor reads and writes one property of <see cref="Instance"/>
/// and raises <see cref="Changed"/> after writing a new value. Inherit from it to build custom editors.
/// The form runs change handlers (<see cref="StrataFx.Changes.IEntityChangeHandler"/>) when <see cref="Changed"/> is raised.
/// </summary>
public abstract class PropertyEditorBase : ComponentBase
{
    [Parameter, EditorRequired] public PropertyModel Property { get; set; } = null!;
    [Parameter, EditorRequired] public object Instance { get; set; } = null!;
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback<PropertyModel> Changed { get; set; }

    /// <summary>True inside grids (owned collections): no label, tighter spacing. The column header shows the caption.</summary>
    [Parameter] public bool Compact { get; set; }

    protected bool IsReadOnly => ReadOnly || Property.ReadOnly;
    protected bool HasError => Error is not null;
    protected string? Label => Compact ? null : Property.Required && !IsReadOnly ? Property.Caption + " *" : Property.Caption;
    /// <summary>Editors are always dense; forms stay compact and grid cells line up with form fields.</summary>
    protected MudBlazor.Margin EditorMargin => MudBlazor.Margin.Dense;
    protected string? HelperText => Compact ? null : Property.Description;

    protected object? Value => Property.GetValue(Instance);

    /// <summary>Writes the value and raises <see cref="Changed"/>. Does nothing when the value is unchanged.</summary>
    protected async Task SetValueAsync(object? value)
    {
        if (Property.ValueEquals(Property.GetValue(Instance), value)) return;
        Property.SetValue(Instance, value);
        await Changed.InvokeAsync(Property);
    }

    /// <summary>Converts a value to the property's type; null becomes the type's default when it is not nullable.</summary>
    protected object? ConvertToPropertyType(object? value)
    {
        if (value is null)
            return Property.IsNullable ? null : Activator.CreateInstance(Property.UnderlyingType);
        return Convert.ChangeType(value, Property.UnderlyingType, System.Globalization.CultureInfo.InvariantCulture);
    }
}
