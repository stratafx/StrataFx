using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StrataFx.Commands;
using StrataFx.Model;
using StrataFx.Security;
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

    /// <summary>Register services and commands.</summary>
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
        module.ConfigureModel(Model);
        module.ConfigureServices(this);
        if (_policy is not null) module.ConfigureSecurity(_policy);
        return this;
    }

    public StrataFxBuilder AddEntity<T>() where T : class { Model.AddEntity<T>(); return this; }
    public StrataFxBuilder AddEntitiesFrom(Assembly assembly) { Model.AddEntitiesFrom(assembly); return this; }
    public StrataFxBuilder AddModelLayer(IModelLayer layer) { Model.AddLayer(layer); return this; }

    /// <summary>Adds a JSON model layer if the file exists (so an optional overrides file can ship empty).</summary>
    public StrataFxBuilder AddJsonModelLayer(string path, bool optional = true)
    {
        if (File.Exists(path)) Model.AddLayer(JsonModelLayer.FromFile(path));
        else if (!optional) throw new FileNotFoundException("Model layer file not found.", path);
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
        var model = Model.Build();
        Services.AddSingleton(model);
        Services.TryAddScoped<ICurrentUser, AnonymousUser>();
        Services.AddScoped<EntityValidator>();

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
