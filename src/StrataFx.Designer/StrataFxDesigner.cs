using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StrataFx.Hosting;
using StrataFx.Model;
using StrataFx.Model.Design;
using StrataFx.Settings;

namespace StrataFx.Designer;

/// <summary>Where the model designer saves.</summary>
public enum ModelDesignerSaveTarget
{
    /// <summary>The JSON layer file (model.json) in Development, the database everywhere else.</summary>
    Auto,

    /// <summary>Always the JSON layer file. Use it when the file is writable and kept in source control.</summary>
    JsonFile,

    /// <summary>Always the database model layer.</summary>
    Database,
}

public sealed class ModelDesignerOptions
{
    public ModelDesignerSaveTarget SaveTo { get; set; } = ModelDesignerSaveTarget.Auto;

    /// <summary>
    /// The JSON layer file to save to, by file name or full path, when there is more than one. Defaults to the last
    /// JSON layer added with <c>fx.AddJsonModelLayer(...)</c>.
    /// </summary>
    public string? JsonFile { get; set; }
}

public static class StrataDesignerRoutes
{
    public const string Designer = "strata/designer";
}

public static class StrataFxDesignerExtensions
{
    /// <summary>The assembly with the designer page. Add it to the Router's AdditionalAssemblies and AddAdditionalAssemblies.</summary>
    public static Assembly PagesAssembly => typeof(StrataFxDesignerExtensions).Assembly;

    /// <summary>
    /// Adds the model designer at /strata/designer. Only users whose role has <c>AllowModelDesigner()</c> in the permission
    /// policy can open it or save. Also adds the database model layer and loads it at startup.
    /// </summary>
    public static StrataFxBuilder AddModelDesigner(this StrataFxBuilder fx, Action<ModelDesignerOptions>? configure = null)
    {
        var options = new ModelDesignerOptions();
        configure?.Invoke(options);
        fx.AddDatabaseModelLayer();

        var services = fx.Services;
        services.AddSingleton(options);
        services.AddSingleton<IModelDesignTarget>(sp => CreateTarget(sp, options));
        services.AddScoped<ModelDesigner>();
        services.AddHostedService<DatabaseModelLayerLoader>();
        return fx;
    }

    private static IModelDesignTarget CreateTarget(IServiceProvider services, ModelDesignerOptions options)
    {
        var models = services.GetRequiredService<IAppModelProvider>();
        var files = models.Layers.OfType<JsonFileModelLayer>().ToList();
        var file = options.JsonFile is null
            ? files.LastOrDefault()
            : files.LastOrDefault(f => string.Equals(f.Path, Path.GetFullPath(options.JsonFile), StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(f.Name, options.JsonFile, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"The model designer should save to '{options.JsonFile}', but no JSON model layer reads that file.");

        var useFile = options.SaveTo switch
        {
            ModelDesignerSaveTarget.JsonFile => true,
            ModelDesignerSaveTarget.Database => false,
            _ => file is not null && services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        };
        if (!useFile)
            return new DatabaseDesignTarget(services.GetRequiredService<DatabaseModelLayer>(), services.GetRequiredService<IServiceScopeFactory>());
        return new JsonFileDesignTarget(file ?? throw new InvalidOperationException(
            "The model designer should save to a JSON file, but there is no JSON model layer. Add one with fx.AddJsonModelLayer(...)."));
    }
}

/// <summary>
/// Reads the database model layer when the app starts and publishes the model with it. A broken or unreadable layer
/// is logged and shown in the designer; the app runs on the model without it.
/// </summary>
internal sealed class DatabaseModelLayerLoader(
    DatabaseModelLayer layer, IAppModelProvider models, IServiceScopeFactory scopes, ILogger<DatabaseModelLayerLoader> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await layer.LoadAsync(scope.ServiceProvider.GetRequiredService<IUserSettingsStore>(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The database model layer could not be read. The app runs without it.");
            return;
        }
        if (layer.Content is null) return;
        if (models.Rebuild() is { } error)
        {
            layer.SetAside(error.Message);
            models.Rebuild();
            logger.LogError("The database model layer doesn't apply: {Error} The app runs without it; fix it in the model designer.", error.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
