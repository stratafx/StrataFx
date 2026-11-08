namespace StrataFx.Model;

/// <summary>Why the last rebuild failed. The app keeps running on the last good model.</summary>
/// <param name="Layer">The name of the layer that failed, when known.</param>
public sealed record ModelBuildError(string? Layer, string Message, DateTimeOffset At);

/// <summary>
/// Publishes the application model. The model is built at startup and can be rebuilt while the app runs (the model
/// designer does this after saving); each rebuild creates a new <see cref="AppModel"/> and swaps it in, so a model
/// that a page is reading is never changed underneath it.
/// </summary>
/// <remarks>
/// Components and services that inject <see cref="AppModel"/> get <see cref="Current"/> at the time they are created.
/// StrataFx's pages read <see cref="Current"/> when they are navigated to; long-lived components (navigation, the app
/// bar title) listen to <see cref="Changed"/>.
/// </remarks>
public interface IAppModelProvider
{
    /// <summary>The model in use: the last one that built without errors.</summary>
    AppModel Current { get; }

    /// <summary>The error of the last rebuild, or null when it succeeded.</summary>
    ModelBuildError? LastError { get; }

    /// <summary>The model layers in the order they run (the classes and attributes come before all of them).</summary>
    IReadOnlyList<IModelLayer> Layers { get; }

    /// <summary>The layer's name with the module that added it, e.g. "Sales defaults (SalesModule)".</summary>
    string Describe(IModelLayer layer);

    /// <summary>Raised after a new model is published, on the thread that rebuilt it.</summary>
    event Action<AppModel>? Changed;

    /// <summary>
    /// Builds the model again from all layers and publishes it. When a layer fails, the current model stays in use
    /// and the error is returned and kept in <see cref="LastError"/>.
    /// </summary>
    ModelBuildError? Rebuild();

    /// <summary>
    /// Builds a model without publishing it, optionally with some layers replaced (e.g. a designer's unsaved changes in
    /// place of model.json) and recording which layer set each value.
    /// </summary>
    AppModel Build(IReadOnlyDictionary<IModelLayer, IModelLayer>? replacements = null, ModelProvenance? provenance = null);
}

/// <summary>The default <see cref="IAppModelProvider"/>, registered by <c>AddStrataFx</c>.</summary>
public sealed class AppModelProvider : IAppModelProvider
{
    private readonly AppModelBuilder _builder;
    private readonly IReadOnlyList<IModelLayer> _layers;
    private readonly object _rebuild = new();
    private volatile AppModel _current;
    private volatile ModelBuildError? _lastError;

    /// <summary>Builds the first model. Throws when a layer fails, because there is no good model to fall back to.</summary>
    public AppModelProvider(AppModelBuilder builder, IEnumerable<IModelLayer>? extraLayers = null)
    {
        _builder = builder;
        _layers = [.. builder.Layers, .. extraLayers ?? []];
        _current = _builder.Build(_layers);
    }

    public AppModel Current => _current;
    public ModelBuildError? LastError => _lastError;
    public IReadOnlyList<IModelLayer> Layers => _layers;
    public event Action<AppModel>? Changed;

    public string Describe(IModelLayer layer) => _builder.Describe(layer);

    public AppModel Build(IReadOnlyDictionary<IModelLayer, IModelLayer>? replacements = null, ModelProvenance? provenance = null) =>
        _builder.Build(replacements is null ? _layers : _layers.Select(l => replacements.GetValueOrDefault(l, l)), provenance);

    public ModelBuildError? Rebuild()
    {
        AppModel model;
        lock (_rebuild)
        {
            try
            {
                model = Build();
            }
            catch (Exception ex)
            {
                return _lastError = new ModelBuildError((ex as ModelLayerException)?.LayerName, ex.Message, DateTimeOffset.Now);
            }
            _current = model;
            _lastError = null;
        }
        // One listener that throws (e.g. a disposed component) must not keep the others from hearing about the model.
        foreach (var listener in Changed?.GetInvocationList().Cast<Action<AppModel>>() ?? [])
        {
            try
            {
                listener(model);
            }
            catch (Exception)
            {
                // The model is published; a listener's failure is its own.
            }
        }
        return null;
    }
}
