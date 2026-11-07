using StrataFx.Model;

namespace StrataFx.Data;

/// <summary>A page of data requested by a list view.</summary>
public sealed record QueryRequest
{
    public int Skip { get; init; }
    public int Take { get; init; } = 25;

    /// <summary>Free-text search across the entity's visible text properties.</summary>
    public string? Search { get; init; }

    public string? SortProperty { get; init; }
    public bool SortDescending { get; init; }

    /// <summary>Equality filters, ANDed together. Used e.g. to list a customer's orders.</summary>
    public IReadOnlyList<PropertyFilter> Filters { get; init; } = [];
}

/// <summary>
/// Matches records whose property equals <paramref name="Value"/>. For a reference property,
/// the value is the referenced record's key (e.g. Customer = 5 matches orders of customer 5).
/// </summary>
public sealed record PropertyFilter(string PropertyName, object? Value);

/// <summary>
/// Runs before validation on every save, e.g. to compute totals or stamp dates. Register with <c>fx.AddSaveHandler&lt;T&gt;()</c>.
/// </summary>
public interface IEntitySaveHandler
{
    bool AppliesTo(EntityModel entity);
    void BeforeSave(EntityModel entity, object instance, bool isNew);
}

public sealed record PagedResult(IReadOnlyList<object> Items, int TotalCount);

/// <summary>
/// Storage-agnostic data access the generated views use. StrataFx.EFCore implements it on EF Core;
/// other back ends (Dapper, REST APIs) can implement it too.
/// Implementations must enforce <see cref="Security.IPermissionService"/> and validation.
/// </summary>
public interface IDataStore
{
    Task<PagedResult> QueryAsync(EntityModel entity, QueryRequest request, CancellationToken cancellationToken = default);

    Task<object?> FindAsync(EntityModel entity, object key, CancellationToken cancellationToken = default);

    /// <summary>Candidates for a reference editor, matched on the lookup text.</summary>
    Task<IReadOnlyList<object>> LookupAsync(EntityModel entity, string? search, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates an instance, together with its owned collections (items added, changed and removed).
    /// Throws <see cref="Validation.StrataValidationException"/> if it is invalid.
    /// For updates, pass an instance loaded with <see cref="FindAsync"/>: list results do not load owned
    /// collections, and saving one would remove its items.
    /// </summary>
    Task SaveAsync(EntityModel entity, object instance, bool isNew, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the instances, all or none. Throws <see cref="Validation.StrataValidationException"/> when other
    /// records still use one of them, e.g. "This product is used on 12 order lines and can't be deleted."
    /// </summary>
    Task DeleteAsync(EntityModel entity, IEnumerable<object> instances, CancellationToken cancellationToken = default);

    /// <summary>
    /// What else a delete removes, for the confirmation, e.g. "This will also delete 3 orders and their lines."
    /// Null when nothing else is deleted or the store cannot tell.
    /// </summary>
    Task<string?> GetDeleteImpactAsync(EntityModel entity, IReadOnlyList<object> instances, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
