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
        [EditorKind.Collection] = typeof(CollectionEditor),
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
    public const string ReturnUrlParameter = "returnUrl";

    public static string List(EntityModel entity) => $"app/{entity.Id}";
    public static string New(EntityModel entity) => $"app/{entity.Id}/new";
    public static string Detail(EntityModel entity, object instance) => $"app/{entity.Id}/{Uri.EscapeDataString(entity.FormatKey(instance))}";

    /// <summary>Appends query parameters, e.g. a reference to pre-fill (?Customer=5) or where to go back to.</summary>
    public static string WithQuery(string url, params (string Name, string? Value)[] parameters)
    {
        var query = string.Join("&", parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value!)}"));
        if (query.Length == 0) return url;
        return url + (url.Contains('?') ? "&" : "?") + query;
    }

    public static Dictionary<string, string> ParseQuery(string uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = uri.IndexOf('?');
        if (start < 0) return result;
        foreach (var pair in uri[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    /// <summary>
    /// Only app-relative paths are accepted as return URLs, so a crafted link cannot send users to another site.
    /// </summary>
    public static string? SafeReturnUrl(string? url) =>
        url is { Length: > 0 } && !url.Contains("://") && !url.StartsWith("//") && !url.StartsWith("\\") && !url.Contains(':')
            ? url.TrimStart('/')
            : null;
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
