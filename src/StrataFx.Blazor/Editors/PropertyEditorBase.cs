using Microsoft.AspNetCore.Components;
using StrataFx.Model;

namespace StrataFx.Blazor.Editors;

/// <summary>
/// Base class for property editors. An editor reads and writes one property of <see cref="Instance"/>
/// and raises <see cref="Changed"/> after writing. Inherit from it to build custom editors.
/// </summary>
public abstract class PropertyEditorBase : ComponentBase
{
    [Parameter, EditorRequired] public PropertyModel Property { get; set; } = null!;
    [Parameter, EditorRequired] public object Instance { get; set; } = null!;
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback<PropertyModel> Changed { get; set; }

    protected bool IsReadOnly => ReadOnly || Property.ReadOnly;
    protected bool HasError => Error is not null;
    protected string Label => Property.Required && !IsReadOnly ? Property.Caption + " *" : Property.Caption;

    protected object? Value => Property.GetValue(Instance);

    protected async Task SetValueAsync(object? value)
    {
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
