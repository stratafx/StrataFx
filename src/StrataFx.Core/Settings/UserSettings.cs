using System.Collections.Concurrent;
using System.Text.Json;
using StrataFx.Security;

namespace StrataFx.Settings;

/// <summary>
/// Saves per-user settings (the user stratum) as JSON, keyed by user name and a setting key such as
/// "list/customer". StrataFx.EFCore stores them in the application's database
/// (<c>fx.AddEntityFrameworkCoreUserSettings&lt;TContext&gt;()</c>); without a store they are kept in memory
/// until the app restarts.
/// </summary>
public interface IUserSettingsStore
{
    Task<string?> GetAsync(string userName, string key, CancellationToken cancellationToken = default);
    Task SaveAsync(string userName, string key, string json, CancellationToken cancellationToken = default);

    /// <summary>Removes the setting, so the user gets the default again.</summary>
    Task ResetAsync(string userName, string key, CancellationToken cancellationToken = default);
}

/// <summary>Keeps settings in memory, for every user, until the app restarts. The default when no store is registered.</summary>
public sealed class InMemoryUserSettingsStore : IUserSettingsStore
{
    private readonly ConcurrentDictionary<(string User, string Key), string> _values = new();

    public Task<string?> GetAsync(string userName, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.GetValueOrDefault(Normalize(userName, key)));

    public Task SaveAsync(string userName, string key, string json, CancellationToken cancellationToken = default)
    {
        _values[Normalize(userName, key)] = json;
        return Task.CompletedTask;
    }

    public Task ResetAsync(string userName, string key, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(Normalize(userName, key), out _);
        return Task.CompletedTask;
    }

    // User names and keys ignore case, like the unique index of the EF Core store on most databases.
    private static (string, string) Normalize(string userName, string key) => (userName.ToUpperInvariant(), key.ToUpperInvariant());
}

public sealed class UserSettingsOptions
{
    /// <summary>
    /// Users in this role may save a layout as the default for everyone (a shared layer between model.json and each
    /// user's own settings). Null turns the option off. Defaults to "Admin".
    /// </summary>
    public string? SharedDefaultsRole { get; set; } = "Admin";
}

/// <summary>
/// The current user's settings. Signed-in users' settings go to the <see cref="IUserSettingsStore"/>; anonymous
/// users' settings live only as long as this service (the Blazor circuit), so they never mix with anyone else's.
/// Unreadable JSON (e.g. written by an older version) counts as no setting, so it can never break a view.
/// </summary>
public sealed class UserSettings(ICurrentUser user, IUserSettingsStore store, UserSettingsOptions options)
{
    /// <summary>The user name the shared defaults are stored under. Identity user names cannot contain '*'.</summary>
    public const string SharedUserName = "*";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, string> _session = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when settings are saved for the user; false for anonymous users (kept for this session only).</summary>
    public bool IsPersistent => user.IsAuthenticated && !string.IsNullOrEmpty(user.Name);

    /// <summary>True when the user may change the defaults everyone gets (<see cref="UserSettingsOptions.SharedDefaultsRole"/>).</summary>
    public bool CanEditSharedDefaults => IsPersistent && options.SharedDefaultsRole is { } role && user.IsInRole(role);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class =>
        Deserialize<T>(IsPersistent
            ? await store.GetAsync(user.Name!, key, cancellationToken)
            : _session.GetValueOrDefault(key));

    public Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (IsPersistent) return store.SaveAsync(user.Name!, key, json, cancellationToken);
        _session[key] = json;
        return Task.CompletedTask;
    }

    public Task ResetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (IsPersistent) return store.ResetAsync(user.Name!, key, cancellationToken);
        _session.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>The default for everyone, if one was saved.</summary>
    public async Task<T?> GetSharedAsync<T>(string key, CancellationToken cancellationToken = default) where T : class =>
        Deserialize<T>(await store.GetAsync(SharedUserName, key, cancellationToken));

    public Task SaveSharedAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        DemandSharedDefaults();
        return store.SaveAsync(SharedUserName, key, JsonSerializer.Serialize(value, Json), cancellationToken);
    }

    public Task ResetSharedAsync(string key, CancellationToken cancellationToken = default)
    {
        DemandSharedDefaults();
        return store.ResetAsync(SharedUserName, key, cancellationToken);
    }

    private void DemandSharedDefaults()
    {
        if (!CanEditSharedDefaults)
            throw new UnauthorizedAccessException("You do not have permission to change the defaults for everyone.");
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
