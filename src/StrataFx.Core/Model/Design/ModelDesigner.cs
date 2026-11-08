using System.Text.Json;
using StrataFx.Security;

namespace StrataFx.Model.Design;

/// <summary>
/// Opens model design sessions for the current user. Every operation that reads the model's layers or saves checks
/// the <see cref="StrataPermissions.ModelDesigner"/> permission on the server, whatever the UI shows.
/// </summary>
public sealed class ModelDesigner(IAppModelProvider models, IModelDesignTarget target, IPermissionService permissions)
{
    public bool IsAllowed => permissions.HasPermission(StrataPermissions.ModelDesigner);

    public IModelDesignTarget Target => target;

    /// <summary>Reads the target layer and starts a session on top of the current model.</summary>
    /// <exception cref="UnauthorizedAccessException">The user may not use the designer.</exception>
    public async Task<ModelDesignSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        permissions.Demand(StrataPermissions.ModelDesigner);
        var session = new ModelDesignSession(models, target, permissions);
        await session.LoadAsync(cancellationToken);
        return session;
    }
}

/// <summary>What the designer shows for one value.</summary>
/// <param name="Value">The value with the unsaved changes applied.</param>
/// <param name="Source">The layer the value comes from, with the unsaved changes applied.</param>
/// <param name="IsSetInTarget">True when the target layer (with the unsaved changes) sets the value, so it can be reset.</param>
/// <param name="BelowValue">What the value would be without the target layer: what a reset goes back to.</param>
/// <param name="BelowSource">The layer <paramref name="BelowValue"/> comes from.</param>
/// <param name="IsChanged">True when the value differs from what is saved.</param>
/// <param name="DisabledReason">Why the value can't be changed here, or null.</param>
public sealed record ModelValueInfo(
    ModelValueKey Key,
    ModelSetting Setting,
    object? Value,
    string Source,
    bool IsSetInTarget,
    object? BelowValue,
    string BelowSource,
    bool IsChanged,
    string? DisabledReason);

/// <summary>The outcome of <see cref="ModelDesignSession.SaveAsync"/>.</summary>
public sealed record ModelSaveResult(bool Saved, string? Error);

/// <summary>
/// One person's edit of the model: a draft of the target layer, a preview model built from it, the changes and the
/// text that a save writes. Nothing is published until <see cref="SaveAsync"/>. Not thread-safe (one per circuit).
/// </summary>
public sealed class ModelDesignSession
{
    private readonly IAppModelProvider _models;
    private readonly IModelDesignTarget _target;
    private readonly IPermissionService _permissions;
    private ModelLayerDocument _saved = new();
    private ModelLayerDocument _draft = new();
    private string? _savedText;
    private string? _renderedText;
    private bool _renderedValid;

    internal ModelDesignSession(IAppModelProvider models, IModelDesignTarget target, IPermissionService permissions)
    {
        _models = models;
        _target = target;
        _permissions = permissions;
        Model = models.Current;
        Provenance = new ModelProvenance();
        TargetLayerName = models.Describe(target.Layer);
        TargetIndex = IndexOf(models.Layers, target.Layer) + 1; // 0 is the classes and attributes
    }

    public IModelDesignTarget Target => _target;

    /// <summary>The target layer's name as provenance shows it.</summary>
    public string TargetLayerName { get; }

    /// <summary>The target layer's index in <see cref="ModelProvenance.Layers"/>.</summary>
    public int TargetIndex { get; }

    /// <summary>The model with the unsaved changes applied (the last one that built). Use it for previews.</summary>
    public AppModel Model { get; private set; }

    /// <summary>Which layer set each value of <see cref="Model"/>.</summary>
    public ModelProvenance Provenance { get; private set; }

    /// <summary>Incremented whenever <see cref="Model"/> changes, e.g. to re-create previews.</summary>
    public int Version { get; private set; }

    /// <summary>Why the stored layer can't be read (invalid JSON); the designer can't save until it's fixed.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Why the draft doesn't build, or null.</summary>
    public string? BuildError { get; private set; }

    /// <summary>
    /// Names in the stored layer the model doesn't know. A file with problems must be fixed by hand first; the database
    /// layer is rewritten without them on the next save.
    /// </summary>
    public IReadOnlyList<string> Problems =>
        _target.Layer is DatabaseModelLayer { Problem: { } problem } ? [$"The stored layer was set aside: {problem}", .. _saved.Problems] : _saved.Problems;

    /// <summary>The values that differ from what is saved.</summary>
    public IReadOnlyList<ModelLayerChange> Changes { get; private set; } = [];

    public bool HasChanges => Changes.Count > 0;

    /// <summary>
    /// True when there is something to save and it can be saved. A database layer that was set aside can be saved
    /// without changes, which replaces it with the values that still apply.
    /// </summary>
    public bool CanSave => (HasChanges || _target.Layer is DatabaseModelLayer { Problem: not null })
        && LoadError is null && BuildError is null && (!_target.KeepsComments || _saved.Problems.Count == 0);

    /// <summary>The stored text when the session started or last saved.</summary>
    public string? SavedText => _savedText;

    /// <summary>The text a save writes.</summary>
    public string? RenderedText
    {
        get
        {
            if (!_renderedValid)
            {
                try
                {
                    _renderedText = LoadError is not null ? _savedText : _target.Render(_savedText, Changes, _draft, Model);
                }
                catch (JsonException ex)
                {
                    _renderedText = _savedText;
                    LoadError = ex.Message;
                }
                _renderedValid = true;
            }
            return _renderedText;
        }
    }

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        _savedText = await _target.ReadAsync(cancellationToken);
        LoadError = null;
        // The database may have been changed by another server: use what is stored now.
        if (_target.Layer is DatabaseModelLayer database && database.Problem is null
            && database.Content != (string.IsNullOrWhiteSpace(_savedText) ? null : _savedText))
        {
            database.SetContent(_savedText);
            if (_models.Rebuild() is { } error)
            {
                database.SetAside(error.Message);
                _models.Rebuild();
            }
        }
        try
        {
            _saved = ModelLayerDocument.Parse(_savedText, _models.Current);
        }
        catch (JsonException ex)
        {
            _saved = new ModelLayerDocument();
            LoadError = $"{_target.Layer.Name} is not valid JSON: {ex.Message}";
        }
        _draft = _saved.Clone();
        Refresh();
    }

    /// <summary>Reads the stored layer again, dropping unsaved changes.</summary>
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _permissions.Demand(StrataPermissions.ModelDesigner);
        return LoadAsync(cancellationToken);
    }

    public ModelValueInfo Describe(ModelValueKey key)
    {
        var setting = ModelSettings.Get(key);
        var current = Provenance.Current(key);
        var below = Provenance.Before(key, TargetIndex);
        var setHere = _draft.Contains(key);
        var aboveTarget = current is { } c && c.LayerIndex > TargetIndex ? c.Layer : null;

        string? disabled = aboveTarget is not null
            ? $"Set by {aboveTarget}, which is applied after {TargetLayerName}."
            : LoadError is not null ? "The layer can't be read." : NotEditableReason(key, setting);

        var source = setHere && aboveTarget is null ? TargetLayerName : current?.Layer ?? ModelProvenance.CodeLayer;
        var isChanged = _saved.TryGetValue(key, out var savedValue) != setHere || setHere && !Equals(savedValue, _draft.Values[key]);
        return new ModelValueInfo(key, setting, current?.Value, source, setHere, below?.Value, below?.Layer ?? ModelProvenance.CodeLayer,
            isChanged, disabled);
    }

    private string? NotEditableReason(ModelValueKey key, ModelSetting setting)
    {
        if (key.Scope == ModelSettingScope.App) return null;
        var entity = Model.FindEntity(key.Entity!);
        var property = key.Property is null ? null : entity?.FindProperty(key.Property);
        return setting.NotEditableReason(entity, property);
    }

    /// <summary>
    /// Changes a value in the draft and rebuilds the preview. Setting the value the lower layers give removes it from
    /// the target layer, so the layer stays minimal. Returns why the value was refused, or null.
    /// </summary>
    public string? Set(ModelValueKey key, object? value)
    {
        var info = Describe(key);
        if (info.DisabledReason is { } reason) return reason;
        var setting = info.Setting;
        value = setting.Normalize(value);
        if (setting.Type == ModelSettingType.Text && string.IsNullOrWhiteSpace(value as string)) return $"{setting.Label} can't be empty.";
        if (setting.Key == "lines" && value is < 1 or > 40) return "Lines must be between 1 and 40.";
        if (setting.Key == ModelSettings.LookupPropertyKey && value is string name && Model.FindEntity(key.Entity!)?.FindProperty(name) is null)
            return $"There is no property '{name}'.";

        if (setting.ValueEquals(value, info.BelowValue) && Provenance.Before(key, TargetIndex) is not null) _draft.Remove(key);
        else _draft.Set(key, value);
        Refresh();
        return BuildError;
    }

    /// <summary>Removes the value from the target layer, so it comes from the layer below again.</summary>
    public void Reset(ModelValueKey key)
    {
        if (_draft.Remove(key)) Refresh();
    }

    /// <summary>Drops every unsaved change.</summary>
    public void Discard()
    {
        _draft = _saved.Clone();
        Refresh();
    }

    /// <summary>Writes the target layer and publishes the new model to every user. Keeps the last good model on failure.</summary>
    public async Task<ModelSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        _permissions.Demand(StrataPermissions.ModelDesigner);
        if (!CanSave) return new ModelSaveResult(false, LoadError ?? BuildError ?? (Problems.Count > 0 ? "Fix the problems in the layer first." : "There are no changes."));

        var content = RenderedText;
        try
        {
            await _target.WriteAsync(_savedText, content, cancellationToken);
        }
        catch (Exception ex) when (ex is ModelDesignConflictException or IOException or UnauthorizedAccessException)
        {
            return new ModelSaveResult(false, ex.Message);
        }

        if (_models.Rebuild() is { } error)
        {
            // Another layer broke since the preview was built. Put the old content back so storage and the model agree.
            await _target.WriteAsync(content, _savedText, cancellationToken);
            _models.Rebuild();
            return new ModelSaveResult(false, $"Not saved: the model doesn't build. {error.Message}");
        }

        _savedText = content;
        _saved = _draft.Clone();
        Refresh();
        return new ModelSaveResult(true, null);
    }

    private void Refresh()
    {
        var model = _models.Current;
        Changes = ModelLayerDocument.Diff(_saved, _draft, model);
        _renderedValid = false;
        var provenance = new ModelProvenance();
        try
        {
            Model = _models.Build(new Dictionary<IModelLayer, IModelLayer> { [_target.Layer] = _draft.ToLayer(_target.Layer.Name, model) }, provenance);
            Provenance = provenance;
            BuildError = null;
        }
        catch (ModelLayerException ex)
        {
            BuildError = ex.Message; // the preview keeps the last model that built
        }
        Version++;
    }

    private static int IndexOf(IReadOnlyList<IModelLayer> layers, IModelLayer layer)
    {
        for (var i = 0; i < layers.Count; i++)
            if (ReferenceEquals(layers[i], layer)) return i;
        throw new InvalidOperationException($"The designer's target layer '{layer.Name}' is not one of the model's layers.");
    }
}
