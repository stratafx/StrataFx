using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StrataFx.Hosting;
using StrataFx.Settings;

namespace StrataFx.EFCore;

/// <summary>One saved user setting, e.g. a user's layout of the customer list. Map it with <c>modelBuilder.AddStrataUserSettings()</c>.</summary>
public sealed class StrataUserSetting
{
    public int Id { get; set; }

    /// <summary>The user's name, or "*" for the defaults everyone gets.</summary>
    public string UserName { get; set; } = "";

    /// <summary>What the setting is for, e.g. "list/customer".</summary>
    public string Key { get; set; } = "";

    public string Json { get; set; } = "";
    public DateTime UpdatedOn { get; set; }
}

/// <summary>Saves user settings in the application's database, in the StrataUserSetting table.</summary>
public sealed class EfUserSettingsStore<TContext>(IDbContextFactory<TContext> contexts) : IUserSettingsStore where TContext : DbContext
{
    public async Task<string?> GetAsync(string userName, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateAsync(cancellationToken);
        return await db.Set<StrataUserSetting>().AsNoTracking()
            .Where(s => s.UserName == userName && s.Key == key)
            .Select(s => s.Json)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(string userName, string key, string json, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await CreateAsync(cancellationToken);
            var setting = await db.Set<StrataUserSetting>().FirstOrDefaultAsync(s => s.UserName == userName && s.Key == key, cancellationToken);
            if (setting is null)
                db.Add(setting = new StrataUserSetting { UserName = userName, Key = key });
            setting.Json = json;
            setting.UpdatedOn = DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // The same user saved the same setting at the same moment (two tabs); the row exists now, so update it.
            }
        }
    }

    public async Task ResetAsync(string userName, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateAsync(cancellationToken);
        await db.Set<StrataUserSetting>().Where(s => s.UserName == userName && s.Key == key).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<TContext> CreateAsync(CancellationToken cancellationToken)
    {
        var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (db.Model.FindEntityType(typeof(StrataUserSetting)) is null)
        {
            await db.DisposeAsync();
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} does not map user settings. Call modelBuilder.AddStrataUserSettings() in its OnModelCreating.");
        }
        return db;
    }
}

public static class StrataUserSettingsEfCoreExtensions
{
    /// <summary>Maps <see cref="StrataUserSetting"/> (table StrataUserSetting, unique on UserName + Key). Call it from your context's OnModelCreating.</summary>
    public static ModelBuilder AddStrataUserSettings(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StrataUserSetting>(e =>
        {
            e.Property(s => s.UserName).HasMaxLength(256).IsRequired();
            e.Property(s => s.Key).HasMaxLength(256).IsRequired();
            e.Property(s => s.Json).IsRequired();
            e.HasIndex(s => new { s.UserName, s.Key }).IsUnique();
        });
        return modelBuilder;
    }

    /// <summary>
    /// Saves user settings (list layouts and other choices) in <typeparamref name="TContext"/>'s database, so they
    /// follow users across sessions and servers. The context must call <c>modelBuilder.AddStrataUserSettings()</c>.
    /// </summary>
    public static StrataFxBuilder AddEntityFrameworkCoreUserSettings<TContext>(this StrataFxBuilder fx) where TContext : DbContext
    {
        fx.Services.RemoveAll<IUserSettingsStore>();
        fx.Services.AddScoped<IUserSettingsStore, EfUserSettingsStore<TContext>>();
        return fx;
    }
}
