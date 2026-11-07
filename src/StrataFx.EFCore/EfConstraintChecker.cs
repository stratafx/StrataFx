using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StrataFx.Model;
using StrataFx.Validation;

namespace StrataFx.EFCore;

/// <summary>
/// Finds database constraint problems before they happen and describes them with the application model's captions:
/// duplicate values on unique indexes, and deletes blocked by records that still point at the record.
/// <para>
/// The checks read the EF model and query the database, rather than parsing provider error codes, so they behave
/// the same on SQLite, SQL Server and PostgreSQL. String comparisons use the database's collation, like the index.
/// </para>
/// </summary>
internal sealed class EfConstraintChecker(DbContext db, AppModel? model)
{
    private static readonly MethodInfo CountMethod = typeof(EfConstraintChecker).GetMethod(nameof(CountCore), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

    /// <summary>Stands for the key of an owned item's parent that has not been saved yet.</summary>
    private static readonly object NewParent = new();

    // ---- unique values ----

    /// <param name="includeFiltered">
    /// Filtered indexes (e.g. "WHERE Deleted = 0") cannot be evaluated here, so a match may be a false alarm. They are
    /// checked only after the database has already refused the save, when a match is the likely cause.
    /// </param>
    public async Task<List<ValidationError>> FindDuplicatesAsync(EntityModel entity, object instance, bool isNew, bool includeFiltered, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        if (db.Model.FindEntityType(entity.ClrType) is not { } efType) return errors;

        var ownKey = isNew ? null : KeyCondition(efType.FindPrimaryKey(), instance);
        foreach (var index in UniqueIndexes(efType, includeFiltered))
        {
            if (!TryGetValues(efType, index, instance, parent: null, out var values)) continue;
            List<Condition> except = ownKey is null ? [] : [ownKey];
            if (await CountAsync(efType, new Condition(index, values), except, ct) > 0)
                errors.Add(new ValidationError(FieldFor(entity, efType, index, skip: null), DuplicateText(entity, efType, index, instance, skip: null) + "."));
        }

        foreach (var collection in entity.OwnedCollections)
        {
            if (efType.FindNavigation(collection.Name) is { IsCollection: true } navigation)
                await FindItemDuplicatesAsync(collection, navigation, instance, isNew, includeFiltered, errors, ct);
        }
        return errors;
    }

    /// <summary>
    /// Owned items are checked against each other (two lines with the same product) and against the database. Stored
    /// items of the same parent are left out of the database check: this save replaces them.
    /// </summary>
    private async Task FindItemDuplicatesAsync(PropertyModel collection, INavigation navigation, object owner, bool isNew,
        bool includeFiltered, List<ValidationError> errors, CancellationToken ct)
    {
        var itemEntity = collection.ElementEntity!;
        var itemType = navigation.TargetEntityType;
        var parentFk = navigation.ForeignKey;
        var parent = new ParentLink(parentFk, isNew ? null : parentFk.PrincipalKey.Properties.Select(p => p.PropertyInfo?.GetValue(owner)).ToArray());
        var items = collection.GetItems(owner);

        foreach (var index in UniqueIndexes(itemType, includeFiltered))
        {
            var seen = new List<(int Row, object?[] Values)>();
            var touchesParent = index.Any(parentFk.Properties.Contains);
            for (var row = 0; row < items.Count; row++)
            {
                if (!TryGetValues(itemType, index, items[row], parent, out var values)) continue;

                string? where = null;
                if (seen.FirstOrDefault(s => s.Values.SequenceEqual(values)) is { Values: not null } earlier)
                    where = $" in row {earlier.Row + 1}";
                else if (!(touchesParent && parent.Key is null)) // items of a new parent cannot clash with stored items on the parent key
                {
                    List<Condition> except = parent.Key is null ? [] : [new Condition(parentFk.Properties, parent.Key)];
                    if (await CountAsync(itemType, new Condition(index, values), except, ct) > 0) where = "";
                }
                seen.Add((row, values));

                if (where is not null)
                {
                    var text = DuplicateText(itemEntity, itemType, index, items[row], skip: parentFk);
                    errors.Add(new ValidationError(collection.Name, $"{collection.Caption}, row {row + 1}: {text}{where}."));
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<IProperty>> UniqueIndexes(IEntityType efType, bool includeFiltered)
    {
        var primaryKey = efType.FindPrimaryKey()?.Properties;
        foreach (var index in efType.GetIndexes().Where(i => i.IsUnique && (includeFiltered || i.FindAnnotation("Relational:Filter")?.Value is null)))
        {
            if (primaryKey is null || !index.Properties.SequenceEqual(primaryKey)) yield return index.Properties;
        }
        foreach (var key in efType.GetKeys().Where(k => !k.IsPrimaryKey()))
            yield return key.Properties; // alternate keys are unique too
    }

    private sealed record ParentLink(IForeignKey ForeignKey, object?[]? Key);

    /// <summary>
    /// The values a save would write for the index. False when one is null (nulls never clash in a unique index) or
    /// cannot be read from the object (shadow properties).
    /// </summary>
    private static bool TryGetValues(IEntityType efType, IReadOnlyList<IProperty> properties, object instance, ParentLink? parent, out object?[] values)
    {
        values = new object?[properties.Count];
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            if (parent is not null && IndexOf(parent.ForeignKey.Properties, property) is var p and >= 0)
                values[i] = parent.Key is null ? NewParent : parent.Key[p];
            else if (!TryGetValue(efType, property, instance, out values[i]))
                return false;
            if (values[i] is null) return false;
        }
        return true;
    }

    /// <summary>
    /// A foreign key is taken from its reference when there is one (Order.Customer rather than Order.CustomerId),
    /// matching how <see cref="EfGraphSaver"/> saves it.
    /// </summary>
    private static bool TryGetValue(IEntityType efType, IProperty property, object instance, out object? value)
    {
        foreach (var fk in property.GetContainingForeignKeys().Where(f => f.DeclaringEntityType.IsAssignableFrom(efType)))
        {
            if (fk.DependentToPrincipal?.PropertyInfo is not { } navigation) continue;
            if (navigation.GetValue(instance) is { } target)
            {
                value = fk.PrincipalKey.Properties[IndexOf(fk.Properties, property)].PropertyInfo?.GetValue(target);
                return true;
            }
            if (property.IsNullable) { value = null; return true; }
        }
        if (property.PropertyInfo is { } info)
        {
            value = info.GetValue(instance);
            return true;
        }
        value = null;
        return false;
    }

    private static int IndexOf(IReadOnlyList<IProperty> properties, IProperty property)
    {
        for (var i = 0; i < properties.Count; i++)
            if (properties[i] == property) return i;
        return -1;
    }

    private static Condition? KeyCondition(IKey? key, object instance)
    {
        if (key is null) return null;
        var values = key.Properties.Select(p => p.PropertyInfo?.GetValue(instance)).ToArray();
        return values.Any(v => v is null) ? null : new Condition(key.Properties, values);
    }

    /// <summary>"Order # SO-1002 is already used", or "This combination of Company and Full Name is already used".</summary>
    private string DuplicateText(EntityModel entity, IEntityType efType, IReadOnlyList<IProperty> index, object instance, IForeignKey? skip)
    {
        var shown = Shown(entity, efType, index, skip).ToList();
        if (shown.Count == 0) return "This value is already used";
        if (shown.Count > 1) return $"This combination of {JoinAnd(shown.Select(s => s.Caption))} is already used";

        var (caption, model, property) = shown[0];
        var value = model is null ? property.PropertyInfo?.GetValue(instance) : model.GetValue(instance);
        var text = model?.FormatValue(value) ?? Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(text) ? $"This {Lower(caption)} is already used" : $"{caption} {text} is already used";
    }

    /// <summary>The form field an index error belongs to: its first property the user can see.</summary>
    private string? FieldFor(EntityModel entity, IEntityType efType, IReadOnlyList<IProperty> index, IForeignKey? skip) =>
        Shown(entity, efType, index, skip).Select(s => s.Model).FirstOrDefault(m => m is { VisibleInDetail: true })?.Name;

    /// <summary>Index properties as the user knows them: a foreign key appears as its reference (CustomerId → Customer).</summary>
    private static IEnumerable<(string Caption, PropertyModel? Model, IProperty Property)> Shown(EntityModel entity, IEntityType efType, IReadOnlyList<IProperty> index, IForeignKey? skip)
    {
        var done = new HashSet<string>();
        foreach (var property in index)
        {
            if (skip is not null && skip.Properties.Contains(property)) continue;
            var navigation = property.GetContainingForeignKeys()
                .FirstOrDefault(f => f.DeclaringEntityType.IsAssignableFrom(efType) && f.DependentToPrincipal is not null)?.DependentToPrincipal;
            var model = (navigation is null ? null : entity.FindProperty(navigation.Name)) ?? entity.FindProperty(property.Name);
            var name = model?.Name ?? property.Name;
            if (done.Add(name)) yield return (model?.Caption ?? Humanizer.SplitWords(property.Name), model, property);
        }
    }

    // ---- deletes ----

    /// <summary>
    /// Records still pointing at the ones being deleted with a delete behavior that stops the delete.
    /// ClientSetNull and ClientCascade count too: EF applies them only to loaded records, and the store does not
    /// load dependents, so the database refuses the delete.
    /// </summary>
    private static bool Blocks(DeleteBehavior behavior) => behavior is
        DeleteBehavior.Restrict or DeleteBehavior.NoAction or DeleteBehavior.ClientNoAction or
        DeleteBehavior.ClientSetNull or DeleteBehavior.ClientCascade;

    /// <summary>"This product is used on 12 order lines and can't be deleted." One error per blocked record.</summary>
    public async Task<List<ValidationError>> FindBlockedDeletesAsync(EntityModel entity, IReadOnlyList<object> instances, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        if (db.Model.FindEntityType(entity.ClrType) is not { } efType) return errors;

        foreach (var instance in instances)
        {
            var uses = new List<string>();
            foreach (var fk in efType.GetReferencingForeignKeys().Where(f => Blocks(f.DeleteBehavior) && !f.DeclaringEntityType.IsOwned()))
            {
                if (KeyCondition(fk.PrincipalKey, instance) is not { } principal) continue;
                // Records of the same type that are being deleted too do not count (a parent deleted with its children).
                var except = fk.DeclaringEntityType.IsAssignableFrom(efType) || efType.IsAssignableFrom(fk.DeclaringEntityType)
                    ? instances.Select(i => KeyCondition(fk.DeclaringEntityType.FindPrimaryKey(), i)).OfType<Condition>().ToList()
                    : [];
                var count = await CountAsync(fk.DeclaringEntityType, new Condition(fk.Properties, principal.Values), except, ct);
                if (count > 0) uses.Add(CountText(count, fk.DeclaringEntityType));
            }
            if (uses.Count == 0) continue;

            var subject = instances.Count == 1 ? $"This {Lower(entity.Caption)}" : DisplayName(entity, instance);
            errors.Add(new ValidationError(null, $"{subject} is used on {JoinAnd(uses)} and can't be deleted."));
        }
        if (errors.Count > 0 && instances.Count > 1)
            errors.Add(new ValidationError(null, "Nothing was deleted."));
        return errors;
    }

    /// <summary>"This will also delete 3 orders and their lines." Null when nothing else is deleted.</summary>
    public async Task<string?> DescribeCascadeAsync(EntityModel entity, IReadOnlyList<object> instances, CancellationToken ct)
    {
        if (db.Model.FindEntityType(entity.ClrType) is not { } efType) return null;

        var parts = new List<string>();
        foreach (var fk in Cascading(efType))
        {
            var principals = instances.Select(i => KeyCondition(fk.PrincipalKey, i)).OfType<Condition>().ToList();
            if (principals.Count == 0) continue;
            var count = await CountAsync(fk.DeclaringEntityType, principals.Select(p => new Condition(fk.Properties, p.Values)).ToList(), [], ct);
            if (count == 0) continue;

            var text = CountText(count, fk.DeclaringEntityType);
            var nested = Cascading(fk.DeclaringEntityType).Select(n => CollectionName(n)).Distinct().ToList();
            if (nested.Count > 0) text += $" and their {JoinAnd(nested)}";
            parts.Add(text);
        }
        return parts.Count == 0 ? null : $"This will also delete {JoinAnd(parts)}.";
    }

    private static IEnumerable<IForeignKey> Cascading(IEntityType efType) =>
        efType.GetReferencingForeignKeys().Where(f => f.DeleteBehavior == DeleteBehavior.Cascade && !f.DeclaringEntityType.IsOwned() && f.DeclaringEntityType != efType);

    /// <summary>"lines" for Order.Lines; the dependent's plural caption when the principal has no collection for it.</summary>
    private string CollectionName(IForeignKey fk)
    {
        if (fk.PrincipalToDependent is { } navigation)
            return Lower(model?.FindEntity(fk.PrincipalEntityType.ClrType)?.FindProperty(navigation.Name)?.Caption ?? Humanizer.SplitWords(navigation.Name));
        return Captions(fk.DeclaringEntityType).Plural;
    }

    // ---- captions ----

    private string CountText(int count, IEntityType efType)
    {
        var (singular, plural) = Captions(efType);
        return $"{count} {(count == 1 ? singular : plural)}";
    }

    private (string Singular, string Plural) Captions(IEntityType efType)
    {
        if (model?.FindEntity(efType.ClrType) is { } registered)
            return (Lower(registered.Caption), Lower(registered.PluralCaption));
        var caption = Humanizer.SplitWords(efType.ClrType.Name);
        return (Lower(caption), Lower(Humanizer.Pluralize(caption)));
    }

    private static string DisplayName(EntityModel entity, object instance)
    {
        var text = entity.GetDisplayText(instance);
        return string.IsNullOrWhiteSpace(text) ? $"{entity.Caption} {entity.FormatKey(instance)}" : text;
    }

    /// <summary>Lower-cases a caption for use mid-sentence, keeping acronyms: "Order Line" → "order line", "VIP Level" → "VIP level".</summary>
    internal static string Lower(string caption) =>
        string.Join(' ', caption.Split(' ').Select(w => w.Length > 1 && w.All(c => !char.IsLower(c)) ? w : w.ToLowerInvariant()));

    private static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    // ---- queries ----

    /// <summary>Property values to match: (A = 1 AND B = 'x').</summary>
    private sealed record Condition(IReadOnlyList<IProperty> Properties, object?[] Values);

    private Task<int> CountAsync(IEntityType efType, Condition match, IReadOnlyList<Condition> except, CancellationToken ct) =>
        CountAsync(efType, [match], except, ct);

    /// <summary>Counts rows matching any of <paramref name="matches"/> and none of <paramref name="except"/>.</summary>
    private Task<int> CountAsync(IEntityType efType, IReadOnlyList<Condition> matches, IReadOnlyList<Condition> except, CancellationToken ct)
    {
        var x = Expression.Parameter(efType.ClrType, "x");
        var body = matches.Select(m => Match(x, m)).Aggregate(Expression.OrElse);
        foreach (var condition in except)
            body = Expression.AndAlso(body, Expression.Not(Match(x, condition)));
        return (Task<int>)CountMethod.MakeGenericMethod(efType.ClrType).Invoke(null, [db, Expression.Lambda(body, x), ct])!;
    }

    private static Task<int> CountCore<T>(DbContext db, Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class =>
        db.Set<T>().AsNoTracking().CountAsync(predicate, ct);

    private static Expression Match(ParameterExpression x, Condition condition) =>
        condition.Properties
            .Select((p, i) => (Expression)Expression.Equal(
                Expression.Call(PropertyMethod.MakeGenericMethod(p.ClrType), x, Expression.Constant(p.Name)),
                Parameter(condition.Values[i], p.ClrType)))
            .Aggregate(Expression.AndAlso);

    /// <summary>A value EF sends as a query parameter (a member of a captured object) rather than inlining it into the SQL.</summary>
    private static Expression Parameter(object? value, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (value is not null && !target.IsInstanceOfType(value))
            value = target.IsEnum ? Enum.ToObject(target, value) : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        var box = Activator.CreateInstance(typeof(StrongBox<>).MakeGenericType(type), value)!;
        return Expression.Field(Expression.Constant(box), nameof(StrongBox<object>.Value));
    }
}
