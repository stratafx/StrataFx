using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StrataFx.Changes;
using StrataFx.Commands;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Settings;
using StrataFx.Validation;

namespace StrataFx.Hosting;

/// <summary>
/// A self-contained feature package: entities, model changes, services, commands and permissions.
/// </summary>
public abstract class StrataModule
{
    public virtual string Name => GetType().Name;

    /// <summary>Add entities and code-based model layers.</summary>
    public virtual void ConfigureModel(AppModelBuilder model) { }

    /// <summary>Register services, commands, rules, save handlers and change handlers.</summary>
    public virtual void ConfigureServices(StrataFxBuilder fx) { }

    public virtual void ConfigureSecurity(PermissionPolicy policy) { }
}

/// <summary>Fluent configuration passed to <c>services.AddStrataFx(...)</c>.</summary>
public sealed class StrataFxBuilder
{
    private readonly List<StrataModule> _modules = [];
    private PermissionPolicy? _policy;

    internal StrataFxBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }
    public AppModelBuilder Model { get; } = new();
    public IReadOnlyList<StrataModule> Modules => _modules;

    /// <summary>Options for per-user settings such as list layouts.</summary>
    public UserSettingsOptions UserSettings { get; } = new();

    public string Title
    {
        get => Model.Title;
        set => Model.Title = value;
    }

    public StrataFxBuilder AddModule<TModule>() where TModule : StrataModule, new() => AddModule(new TModule());

    public StrataFxBuilder AddModule(StrataModule module)
    {
        if (_modules.Any(m => m.GetType() == module.GetType())) return this;
        _modules.Add(module);
        var layerCount = Model.Layers.Count;
        module.ConfigureModel(Model);
        foreach (var layer in Model.Layers.Skip(layerCount))
            Model.SetOrigin(layer, module.Name);
        module.ConfigureServices(this);
        if (_policy is not null) module.ConfigureSecurity(_policy);
        return this;
    }

    public StrataFxBuilder AddEntity<T>() where T : class { Model.AddEntity<T>(); return this; }
    public StrataFxBuilder AddEntitiesFrom(Assembly assembly) { Model.AddEntitiesFrom(assembly); return this; }
    public StrataFxBuilder AddModelLayer(IModelLayer layer) { Model.AddLayer(layer); return this; }

    /// <summary>
    /// Adds a JSON model layer read from a file. The file is read whenever the model is built, so changes apply on the
    /// next rebuild (e.g. after the model designer saves). An optional file that doesn't exist is an empty layer.
    /// </summary>
    public StrataFxBuilder AddJsonModelLayer(string path, bool optional = true)
    {
        if (!optional && !File.Exists(path)) throw new FileNotFoundException("Model layer file not found.", path);
        Model.AddLayer(new JsonFileModelLayer(path));
        return this;
    }

    /// <summary>
    /// The database model layer, when enabled with <see cref="AddDatabaseModelLayer"/>. It always runs after every
    /// other model layer.
    /// </summary>
    public DatabaseModelLayer? DatabaseModelLayer { get; private set; }

    /// <summary>
    /// Adds the database model layer: model changes saved in the <see cref="IUserSettingsStore"/> (the model designer
    /// saves there outside development). It runs after every other layer. Its content is read at startup by
    /// <c>AddModelDesigner</c>, or call <see cref="DatabaseModelLayer.LoadAsync"/> and <see cref="IAppModelProvider.Rebuild"/> yourself.
    /// </summary>
    public StrataFxBuilder AddDatabaseModelLayer()
    {
        if (DatabaseModelLayer is null)
        {
            DatabaseModelLayer = new DatabaseModelLayer();
            Services.AddSingleton(DatabaseModelLayer);
        }
        return this;
    }

    public StrataFxBuilder AddCommand<TCommand>() where TCommand : StrataCommand
    {
        Services.AddScoped<StrataCommand, TCommand>();
        return this;
    }

    public StrataFxBuilder AddRule<TRule>() where TRule : class, IEntityRule
    {
        Services.AddScoped<IEntityRule, TRule>();
        return this;
    }

    public StrataFxBuilder AddSaveHandler<THandler>() where THandler : class, Data.IEntitySaveHandler
    {
        Services.AddScoped<Data.IEntitySaveHandler, THandler>();
        return this;
    }

    /// <summary>Adds a handler that reacts to field changes in forms before saving. Handlers run in registration order.</summary>
    public StrataFxBuilder AddChangeHandler<THandler>() where THandler : class, IEntityChangeHandler
    {
        Services.AddScoped<IEntityChangeHandler, THandler>();
        return this;
    }

    /// <summary>Switches from allow-all to role-based permissions.</summary>
    public StrataFxBuilder UsePermissionPolicy(Action<PermissionPolicy> configure)
    {
        if (_policy is null)
        {
            _policy = new PermissionPolicy();
            foreach (var m in _modules) m.ConfigureSecurity(_policy);
        }
        configure(_policy);
        return this;
    }

    internal void Complete()
    {
        // Built now, so a broken layer stops the app at startup. Later rebuilds keep the last good model instead.
        var provider = new AppModelProvider(Model, DatabaseModelLayer is null ? null : [DatabaseModelLayer]);
        Services.AddSingleton<IAppModelProvider>(provider);
        Services.AddTransient(sp => sp.GetRequiredService<IAppModelProvider>().Current);
        Services.TryAddScoped<ICurrentUser, AnonymousUser>();
        Services.AddScoped<EntityValidator>();
        Services.AddScoped<EntityChangeDispatcher>();
        Services.TryAddSingleton<IUserSettingsStore, InMemoryUserSettingsStore>();
        Services.AddSingleton(UserSettings);
        Services.AddScoped<UserSettings>();

        if (_policy is not null)
        {
            Services.AddSingleton(_policy);
            Services.AddScoped<IPermissionService, PolicyPermissionService>();
        }
        else
        {
            Services.TryAddSingleton<IPermissionService, AllowAllPermissionService>();
        }
    }
}

public static class StrataFxServiceCollectionExtensions
{
    /// <summary>Registers StrataFx: builds the application model and core services.</summary>
    public static IServiceCollection AddStrataFx(this IServiceCollection services, Action<StrataFxBuilder> configure)
    {
        var builder = new StrataFxBuilder(services);
        configure(builder);
        builder.Complete();
        return services;
    }
}
