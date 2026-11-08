namespace StrataFx.Model;

/// <summary>One layer's effect on a model value: the layer and the value it left.</summary>
/// <param name="LayerIndex">0 for the classes and attributes, then 1, 2, ... for the layers in the order they ran.</param>
public readonly record struct ModelValueStep(int LayerIndex, string Layer, object? Value);

/// <summary>
/// Records which layer set each customizable value while a model is built, so the designer can show where a value
/// comes from and what it would fall back to. Pass one to <see cref="AppModelBuilder.Build(IEnumerable{IModelLayer}, ModelProvenance?)"/>;
/// normal builds don't track anything.
/// </summary>
/// <remarks>
/// It compares a snapshot of every value (<see cref="ModelSettings.Snapshot"/>) before and after each layer, so it
/// works for any layer, including code layers, without their help. A layer that sets a value to what it already was
/// leaves no step: the value still comes from the layer below.
/// </remarks>
public sealed class ModelProvenance
{
    /// <summary>The name of the base stratum: entity classes, their attributes and StrataFx's conventions.</summary>
    public const string CodeLayer = "Classes and attributes";

    private readonly List<string> _layers = [];
    private readonly Dictionary<ModelValueKey, List<ModelValueStep>> _history = [];
    private Dictionary<ModelValueKey, object?>? _last;

    /// <summary>The layers in the order they ran; index 0 is <see cref="CodeLayer"/>.</summary>
    public IReadOnlyList<string> Layers => _layers;

    /// <summary>The steps that changed the value, oldest first. Empty for an unknown key.</summary>
    public IReadOnlyList<ModelValueStep> History(ModelValueKey key) =>
        _history.TryGetValue(key, out var steps) ? steps : [];

    /// <summary>The last step for the value: its current value and the layer that set it.</summary>
    public ModelValueStep? Current(ModelValueKey key) =>
        _history.TryGetValue(key, out var steps) && steps.Count > 0 ? steps[^1] : null;

    /// <summary>The name of the layer that set the value, or null for an unknown key.</summary>
    public string? SourceOf(ModelValueKey key) => Current(key)?.Layer;

    /// <summary>The value as it was before layer <paramref name="layerIndex"/> and every later layer ran.</summary>
    public ModelValueStep? Before(ModelValueKey key, int layerIndex)
    {
        if (!_history.TryGetValue(key, out var steps)) return null;
        for (var i = steps.Count - 1; i >= 0; i--)
            if (steps[i].LayerIndex < layerIndex) return steps[i];
        return null;
    }

    internal void Record(string layer, AppModel model)
    {
        var index = _layers.Count;
        _layers.Add(layer);
        var snapshot = ModelSettings.Snapshot(model);
        foreach (var (key, value) in snapshot)
        {
            if (_last is not null && _last.TryGetValue(key, out var before) && Equals(before, value)) continue;
            if (!_history.TryGetValue(key, out var steps)) _history[key] = steps = [];
            steps.Add(new ModelValueStep(index, layer, value));
        }
        _last = snapshot;
    }
}
