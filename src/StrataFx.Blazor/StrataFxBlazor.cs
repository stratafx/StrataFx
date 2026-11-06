using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using StrataFx.Blazor.Editors;
using StrataFx.Model;

namespace StrataFx.Blazor;

/// <summary>Maps properties to editor components. Register custom editors to change how a type or a named property is edited.</summary>
public sealed class PropertyEditorRegistry
{
    private readonly Dictionary<EditorKind, Type> _byKind = new()
    {
        [EditorKind.Text] = typeof(TextEditor),
        [EditorKind.MultilineText] = typeof(TextEditor),
        [EditorKind.Guid] = typeof(GuidEditor),
        [EditorKind.Integer] = typeof(IntegerEditor),
        [EditorKind.Decimal] = typeof(DecimalEditor),
        [EditorKind.Boolean] = typeof(BooleanEditor),
        [EditorKind.Date] = typeof(DateEditor),
        [EditorKind.DateTime] = typeof(DateTimeEditor),
        [EditorKind.Enum] = typeof(EnumEditor),
        [EditorKind.Reference] = typeof(ReferenceEditor),
    };

    private readonly Dictionary<string, Type> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the default editor for a kind of property.</summary>
    public PropertyEditorRegistry Register<TEditor>(EditorKind kind) where TEditor : PropertyEditorBase
    {
        _byKind[kind] = typeof(TEditor);
        return this;
    }

    /// <summary>Adds a named editor, picked with <c>[PropertyEditor("name")]</c> or a model layer's "editor" setting.</summary>
    public PropertyEditorRegistry Register<TEditor>(string name) where TEditor : PropertyEditorBase
    {
        _byName[name] = typeof(TEditor);
        return this;
    }

    public Type Resolve(PropertyModel property)
    {
        if (property.EditorName is { } name)
            return _byName.GetValueOrDefault(name)
                ?? throw new InvalidOperationException($"No editor named '{name}' is registered (used by {property.Owner.ClrType.Name}.{property.Name}).");
        return _byKind.GetValueOrDefault(property.EditorKind) ?? typeof(TextEditor);
    }
}

public static class StrataRoutes
{
    public static string List(EntityModel entity) => $"app/{entity.Id}";
    public static string New(EntityModel entity) => $"app/{entity.Id}/new";
    public static string Detail(EntityModel entity, object instance) => $"app/{entity.Id}/{Uri.EscapeDataString(entity.FormatKey(instance))}";
}

public static class StrataFxBlazorExtensions
{
    /// <summary>The assembly with StrataFx's routable pages. Pass it to the Router's AdditionalAssemblies and AddAdditionalAssemblies.</summary>
    public static Assembly PagesAssembly => typeof(StrataFxBlazorExtensions).Assembly;

    /// <summary>Adds the MudBlazor-based StrataFx UI.</summary>
    public static IServiceCollection AddStrataFxBlazor(this IServiceCollection services, Action<PropertyEditorRegistry>? configureEditors = null)
    {
        services.AddMudServices();
        var registry = new PropertyEditorRegistry();
        configureEditors?.Invoke(registry);
        services.AddSingleton(registry);
        return services;
    }
}
