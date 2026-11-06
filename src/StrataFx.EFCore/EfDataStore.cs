using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using StrataFx.Data;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.EFCore;

/// <summary>
/// <see cref="IDataStore"/> on EF Core. Each operation uses its own short-lived DbContext from
/// <see cref="IDbContextFactory{TContext}"/>, which is the recommended pattern for Blazor Server.
/// Permissions and validation are enforced here, so they hold no matter which UI calls the store.
/// </summary>
public sealed class EfDataStore<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IPermissionService permissions,
    EntityValidator validator) : IDataStore
    where TContext : DbContext
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly MethodInfo QueryMethod = typeof(EfDataStore<TContext>).GetMethod(nameof(QueryCore), Private)!;
    private static readonly MethodInfo FindMethod = typeof(EfDataStore<TContext>).GetMethod(nameof(FindCore), Private)!;
    private static readonly MethodInfo LookupMethod = typeof(EfDataStore<TContext>).GetMethod(nameof(LookupCore), Private)!;

    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    public Task<PagedResult> QueryAsync(EntityModel entity, QueryRequest request, CancellationToken cancellationToken = default)
    {
        permissions.Demand(entity.ClrType, EntityOperation.Read);
        return Invoke<PagedResult>(QueryMethod, entity, entity, request, cancellationToken);
    }

    public Task<object?> FindAsync(EntityModel entity, object key, CancellationToken cancellationToken = default)
    {
        permissions.Demand(entity.ClrType, EntityOperation.Read);
        return Invoke<object?>(FindMethod, entity, entity, key, cancellationToken);
    }

    public Task<IReadOnlyList<object>> LookupAsync(EntityModel entity, string? search, int take = 20, CancellationToken cancellationToken = default)
    {
        permissions.Demand(entity.ClrType, EntityOperation.Read);
        return Invoke<IReadOnlyList<object>>(LookupMethod, entity, entity, search, take, cancellationToken);
    }

    public async Task SaveAsync(EntityModel entity, object instance, bool isNew, CancellationToken cancellationToken = default)
    {
        permissions.Demand(entity.ClrType, isNew ? EntityOperation.Create : EntityOperation.Update);
        validator.ThrowIfInvalid(entity, instance);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = isNew ? db.Add(instance) : db.Update(instance);
        AttachReferencesAsUnchanged(db, entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(EntityModel entity, IEnumerable<object> instances, CancellationToken cancellationToken = default)
    {
        permissions.Demand(entity.ClrType, EntityOperation.Delete);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var instance in instances)
        {
            var tracked = await db.FindAsync(entity.ClrType, [entity.GetKey(instance)], cancellationToken);
            if (tracked is not null) db.Remove(tracked);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- generic implementations, invoked with the entity's CLR type ----

    private async Task<PagedResult> QueryCore<T>(EntityModel entity, QueryRequest request, CancellationToken ct) where T : class
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var efType = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"'{typeof(T).Name}' is not part of {typeof(TContext).Name}. Add a DbSet for it.");
        var query = IncludeReferences(db.Set<T>().AsNoTracking(), entity);

        if (!string.IsNullOrWhiteSpace(request.Search) && BuildSearch<T>(entity, efType, request.Search) is { } predicate)
            query = query.Where(predicate);

        var total = await query.CountAsync(ct);
        var sortProperty = request.SortProperty is { } name && IsMapped(efType, entity.FindProperty(name)) ? name : null;
        query = ApplySort(query, entity, sortProperty, request.SortDescending);
        var items = await query.Skip(request.Skip).Take(request.Take).ToListAsync(ct);
        return new PagedResult(items, total);
    }

    private async Task<object?> FindCore<T>(EntityModel entity, object key, CancellationToken ct) where T : class
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var x = Expression.Parameter(typeof(T), "x");
        var equals = Expression.Equal(
            Expression.Property(x, entity.Key.Info),
            Expression.Constant(key, entity.Key.PropertyType));
        return await IncludeReferences(db.Set<T>().AsNoTracking(), entity)
            .FirstOrDefaultAsync(Expression.Lambda<Func<T, bool>>(equals, x), ct);
    }

    private async Task<IReadOnlyList<object>> LookupCore<T>(EntityModel entity, string? search, int take, CancellationToken ct) where T : class
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<T> query = db.Set<T>().AsNoTracking();
        if (entity.LookupProperty is { } lookup)
        {
            if (!string.IsNullOrWhiteSpace(search) && lookup.UnderlyingType == typeof(string))
            {
                var x = Expression.Parameter(typeof(T), "x");
                query = query.Where(Expression.Lambda<Func<T, bool>>(Like(Expression.Property(x, lookup.Info), search), x));
            }
            query = ApplySort(query, entity, lookup.Name, descending: false);
        }
        return await query.Take(take).ToListAsync(ct);
    }

    // ---- helpers ----

    private Task<TResult> Invoke<TResult>(MethodInfo method, EntityModel entity, params object?[] args) =>
        (Task<TResult>)method.MakeGenericMethod(entity.ClrType).Invoke(this, args)!;

    private static IQueryable<T> IncludeReferences<T>(IQueryable<T> query, EntityModel entity) where T : class
    {
        foreach (var reference in entity.Properties.Where(p => p.EditorKind == EditorKind.Reference))
            query = query.Include(reference.Name);
        return query;
    }

    /// <summary>OR of LIKE '%term%' over visible text columns and the lookup text of references.</summary>
    private static Expression<Func<T, bool>>? BuildSearch<T>(EntityModel entity, IEntityType efType, string term)
    {
        var x = Expression.Parameter(typeof(T), "x");
        Expression? body = null;
        foreach (var prop in entity.ListColumns.Where(p => IsMapped(efType, p)))
        {
            Expression? member = prop switch
            {
                { EditorKind: EditorKind.Text } => Expression.Property(x, prop.Info),
                { EditorKind: EditorKind.Reference, ReferenceEntity.LookupProperty: { } lp } when lp.UnderlyingType == typeof(string)
                    => Expression.Property(Expression.Property(x, prop.Info), lp.Info),
                _ => null,
            };
            if (member is null) continue;
            var like = Like(member, term);
            body = body is null ? like : Expression.OrElse(body, like);
        }
        return body is null ? null : Expression.Lambda<Func<T, bool>>(body, x);
    }

    /// <summary>Computed or ignored properties can be shown but not searched or sorted in the database.</summary>
    private static bool IsMapped(IEntityType efType, PropertyModel? property) =>
        property is not null && (efType.FindProperty(property.Name) is not null || efType.FindNavigation(property.Name) is not null);

    private static Expression Like(Expression member, string term)
    {
        // Escape LIKE wildcards with '\' so a search for "50%" matches literally on every provider.
        var escaped = term.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        return Expression.Call(LikeMethod, Expression.Constant(EF.Functions), member,
            Expression.Constant($"%{escaped}%"), Expression.Constant(@"\"));
    }

    private static IQueryable<T> ApplySort<T>(IQueryable<T> query, EntityModel entity, string? propertyName, bool descending)
    {
        var x = Expression.Parameter(typeof(T), "x");
        var prop = propertyName is null ? null : entity.FindProperty(propertyName);

        Expression? sortKey = prop switch
        {
            null => null,
            { EditorKind: EditorKind.Reference, ReferenceEntity.LookupProperty: { } lp } => Expression.Property(Expression.Property(x, prop.Info), lp.Info),
            { EditorKind: EditorKind.Reference } => null,
            _ => Expression.Property(x, prop.Info),
        };
        var keyExpr = Expression.Property(x, entity.Key.Info);

        if (sortKey is null)
            return OrderBy(query, keyExpr, x, descending, thenBy: false);

        var ordered = OrderBy(query, sortKey, x, descending, thenBy: false);
        return OrderBy(ordered, keyExpr, x, descending: false, thenBy: true); // stable paging
    }

    private static IQueryable<T> OrderBy<T>(IQueryable<T> query, Expression key, ParameterExpression x, bool descending, bool thenBy)
    {
        var name = (thenBy, descending) switch
        {
            (false, false) => nameof(Queryable.OrderBy),
            (false, true) => nameof(Queryable.OrderByDescending),
            (true, false) => nameof(Queryable.ThenBy),
            (true, true) => nameof(Queryable.ThenByDescending),
        };
        var lambda = Expression.Lambda(key, x);
        var method = typeof(Queryable).GetMethods()
            .Single(m => m.Name == name && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(T), key.Type);
        return (IQueryable<T>)method.Invoke(null, [query, lambda])!;
    }

    /// <summary>
    /// The edited instance carries referenced records loaded by another context. Mark them Unchanged so EF
    /// does not update them, and sync foreign keys from the navigation (also when it was cleared).
    /// </summary>
    private static void AttachReferencesAsUnchanged(DbContext db, EntityEntry entry)
    {
        foreach (var reference in entry.References)
        {
            if (reference.Metadata is not INavigation { IsOnDependent: true } navigation) continue;

            var target = reference.CurrentValue;
            if (target is not null) db.Entry(target).State = EntityState.Unchanged;

            var fk = navigation.ForeignKey;
            for (var i = 0; i < fk.Properties.Count; i++)
            {
                var fkProperty = entry.Property(fk.Properties[i].Name);
                if (target is not null)
                    fkProperty.CurrentValue = db.Entry(target).Property(fk.PrincipalKey.Properties[i].Name).CurrentValue;
                else if (fk.Properties[i].IsNullable)
                    fkProperty.CurrentValue = null;
            }
        }
    }
}
