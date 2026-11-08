using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StrataFx.Settings;

namespace StrataFx.Model.Design;

/// <summary>Someone else saved the layer after the designer read it.</summary>
public sealed class ModelDesignConflictException(string message) : InvalidOperationException(message);

/// <summary>Where the model designer saves: one model layer and the storage behind it.</summary>
public interface IModelDesignTarget
{
    /// <summary>The model layer the designer edits. Its values are replaced by the designer's draft in previews.</summary>
    IModelLayer Layer { get; }

    /// <summary>Says where changes go, e.g. "model.json (C:\app\model.json)" or "the database".</summary>
    string Description { get; }

    /// <summary>True when comments and formatting in the stored text survive a save.</summary>
    bool KeepsComments { get; }

    /// <summary>The stored text, or null when nothing is stored yet.</summary>
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>The text to store for the changes: <paramref name="original"/> edited, or written anew.</summary>
    string Render(string? original, IReadOnlyList<ModelLayerChange> changes, ModelLayerDocument draft, AppModel model);

    /// <summary>
    /// Stores <paramref name="content"/> (null or empty removes the layer's content), unless the stored text is no longer
    /// <paramref name="expected"/>: then it throws <see cref="ModelDesignConflictException"/>.
    /// </summary>
    Task WriteAsync(string? expected, string? content, CancellationToken cancellationToken = default);
}

/// <summary>Saves to a JSON layer file (model.json), editing only the changed values so comments and formatting stay.</summary>
public sealed class JsonFileDesignTarget(JsonFileModelLayer layer) : IModelDesignTarget
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public IModelLayer Layer => layer;
    public string Description => $"{layer.Name} ({layer.Path})";
    public bool KeepsComments => true;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
        File.Exists(layer.Path) ? await File.ReadAllTextAsync(layer.Path, cancellationToken) : null;

    public string Render(string? original, IReadOnlyList<ModelLayerChange> changes, ModelLayerDocument draft, AppModel model) =>
        JsonLayerText.Apply(original, changes, model);

    public async Task WriteAsync(string? expected, string? content, CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(cancellationToken);
        if (!string.Equals(Normalize(current), Normalize(expected), StringComparison.Ordinal))
            throw new ModelDesignConflictException($"{layer.Name} was changed after the designer read it. Reload to see the changes, then make yours again.");

        // Write next to the file and move it over, so a crash never leaves half a file. Keep a byte order mark if there was one.
        var hadBom = File.Exists(layer.Path) && (await File.ReadAllBytesAsync(layer.Path, cancellationToken)).AsSpan().StartsWith(Bom);
        var temp = layer.Path + ".tmp";
        await File.WriteAllTextAsync(temp, content ?? JsonLayerText.Empty, new UTF8Encoding(hadBom), cancellationToken);
        File.Move(temp, layer.Path, overwrite: true);
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

/// <summary>
/// Saves to the <see cref="DatabaseModelLayer"/>: canonical JSON in the <see cref="IUserSettingsStore"/>. Nothing
/// but the designer writes there, so the JSON is written anew each time, sorted predictably.
/// </summary>
public sealed class DatabaseDesignTarget(DatabaseModelLayer layer, IServiceScopeFactory scopes) : IModelDesignTarget
{
    public IModelLayer Layer => layer;
    public string Description => "the database (the model layer stored with user settings)";
    public bool KeepsComments => false;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        return await store.GetAsync(UserSettings.SharedUserName, DatabaseModelLayer.SettingKey, cancellationToken);
    }

    public string Render(string? original, IReadOnlyList<ModelLayerChange> changes, ModelLayerDocument draft, AppModel model) =>
        draft.Count == 0 ? "" : draft.ToJson(model);

    public async Task WriteAsync(string? expected, string? content, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var current = await store.GetAsync(UserSettings.SharedUserName, DatabaseModelLayer.SettingKey, cancellationToken);
        if (!string.Equals(Normalize(current), Normalize(expected), StringComparison.Ordinal))
            throw new ModelDesignConflictException("The model was changed in the database after the designer read it. Reload to see the changes, then make yours again.");

        if (string.IsNullOrWhiteSpace(content))
            await store.ResetAsync(UserSettings.SharedUserName, DatabaseModelLayer.SettingKey, cancellationToken);
        else
            await store.SaveAsync(UserSettings.SharedUserName, DatabaseModelLayer.SettingKey, content, cancellationToken);
        layer.SetContent(content);
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
