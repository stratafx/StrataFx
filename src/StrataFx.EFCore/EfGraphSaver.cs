using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using StrataFx.Model;
using StrataFx.Validation;

namespace StrataFx.EFCore;

/// <summary>
/// Saves a disconnected record (edited in the UI, loaded by another DbContext) together with its owned collections.
/// <para>
/// References are saved through their foreign keys only: referenced records are never attached, so they are
/// never updated by accident and two copies of the same referenced record cannot clash in the change tracker.
/// </para>
/// <para>
/// Updates load the stored record, copy values onto it, and synchronize owned items by key: matching items are
/// updated, items without a stored match are inserted, and stored items missing from the edited record are deleted.
/// </para>
/// </summary>
internal sealed class EfGraphSaver(DbContext db)
{
    private readonly List<(object Owner, PropertyInfo Navigation, object Value)> _detached = [];
    private readonly List<(object Owner, string Property, object? Value)> _pendingKeys = [];

    public async Task SaveAsync(EntityModel entity, object instance, bool isNew, CancellationToken ct)
    {
        var efType = db.Model.FindEntityType(entity.ClrType)
            ?? throw new InvalidOperationException($"'{entity.ClrType.Name}' is not part of {db.GetType().Name}. Add a DbSet for it.");
        try
        {
            if (isNew) Insert(entity, efType, instance);
            else await UpdateAsync(entity, efType, instance, ct);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            // Give the caller's objects their references back, whatever happened.
            foreach (var (owner, navigation, value) in _detached) navigation.SetValue(owner, value);
        }
    }

    private void Insert(EntityModel entity, IEntityType efType, object instance)
    {
        DetachReferences(efType, instance, skip: null);
        foreach (var (collection, navigation) in OwnedNavigations(entity, efType))
        {
            foreach (var item in collection.GetItems(instance))
                DetachReferences(navigation.TargetEntityType, item, skip: navigation.ForeignKey);
        }
        db.Add(instance); // owned items are reachable from the instance, so they are added too
        ApplyPendingKeys();
    }

    private async Task UpdateAsync(EntityModel entity, IEntityType efType, object instance, CancellationToken ct)
    {
        var key = entity.GetKey(instance);
        var stored = await db.FindAsync(entity.ClrType, [key], ct)
            ?? throw new StrataValidationException([new ValidationError(null, $"This {entity.Caption.ToLowerInvariant()} no longer exists. It may have been deleted by someone else.")]);

        var entry = db.Entry(stored);
        entry.CurrentValues.SetValues(instance);
        CopyReferenceKeys(efType, instance, entry, skip: null);

        foreach (var (collection, navigation) in OwnedNavigations(entity, efType))
        {
            await entry.Collection(collection.Name).LoadAsync(ct);
            var itemEntity = collection.ElementEntity!;
            var itemType = navigation.TargetEntityType;

            var storedItems = collection.GetItems(stored).ToDictionary(i => itemEntity.GetKey(i)!);
            var kept = new HashSet<object>();

            foreach (var item in collection.GetItems(instance))
            {
                var itemKey = itemEntity.GetKey(item);
                if (itemKey is not null && !IsDefault(itemKey) && storedItems.TryGetValue(itemKey, out var storedItem))
                {
                    var itemEntry = db.Entry(storedItem);
                    itemEntry.CurrentValues.SetValues(item);
                    CopyReferenceKeys(itemType, item, itemEntry, skip: navigation.ForeignKey);
                    kept.Add(itemKey);
                }
                else
                {
                    DetachReferences(itemType, item, skip: navigation.ForeignKey);
                    collection.AddItem(stored, item); // found by DetectChanges and inserted, with its parent key set
                }
            }

            foreach (var (storedKey, storedItem) in storedItems)
            {
                if (!kept.Contains(storedKey)) db.Remove(storedItem);
            }
        }

        db.ChangeTracker.DetectChanges();
        ApplyPendingKeys();
    }

    private static IEnumerable<(PropertyModel Collection, INavigation Navigation)> OwnedNavigations(EntityModel entity, IEntityType efType)
    {
        foreach (var collection in entity.OwnedCollections)
        {
            if (efType.FindNavigation(collection.Name) is { IsCollection: true } navigation)
                yield return (collection, navigation);
        }
    }

    /// <summary>Single-record navigations held by the dependent (e.g. Order.Customer), except the one to the parent.</summary>
    private static IEnumerable<INavigation> DependentReferences(IEntityType efType, IForeignKey? skip) =>
        efType.GetNavigations().Where(n => n is { IsCollection: false, IsOnDependent: true, PropertyInfo: not null } && n.ForeignKey != skip);

    /// <summary>
    /// Before a new record is attached: remember the foreign keys its references stand for, then clear the
    /// references so EF does not start tracking (and inserting) the referenced records.
    /// </summary>
    private void DetachReferences(IEntityType efType, object owner, IForeignKey? skip)
    {
        foreach (var navigation in DependentReferences(efType, skip))
        {
            var target = navigation.PropertyInfo!.GetValue(owner);
            foreach (var (property, value) in ForeignKeyValues(navigation.ForeignKey, target))
                _pendingKeys.Add((owner, property, value));

            if (target is not null)
            {
                _detached.Add((owner, navigation.PropertyInfo, target));
                navigation.PropertyInfo.SetValue(owner, null);
            }
        }
    }

    /// <summary>For an already tracked record: set its foreign keys from the edited copy's references.</summary>
    private static void CopyReferenceKeys(IEntityType efType, object source, EntityEntry target, IForeignKey? skip)
    {
        foreach (var navigation in DependentReferences(efType, skip))
        {
            foreach (var (property, value) in ForeignKeyValues(navigation.ForeignKey, navigation.PropertyInfo!.GetValue(source)))
                target.Property(property).CurrentValue = value;
        }
    }

    private static IEnumerable<(string Property, object? Value)> ForeignKeyValues(IForeignKey foreignKey, object? target)
    {
        for (var i = 0; i < foreignKey.Properties.Count; i++)
        {
            var value = target is null ? null : foreignKey.PrincipalKey.Properties[i].PropertyInfo?.GetValue(target);
            if (value is null && !foreignKey.Properties[i].IsNullable) continue; // required reference missing: validation reports it
            yield return (foreignKey.Properties[i].Name, value);
        }
    }

    private void ApplyPendingKeys()
    {
        foreach (var (owner, property, value) in _pendingKeys)
            db.Entry(owner).Property(property).CurrentValue = value;
    }

    private static bool IsDefault(object key) => key switch
    {
        string s => s.Length == 0,
        _ => key.GetType().IsValueType && key.Equals(Activator.CreateInstance(key.GetType())),
    };
}
