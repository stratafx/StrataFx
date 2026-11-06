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

    /// <summary>Inserts or updates an instance. Throws <see cref="Validation.StrataValidationException"/> if it is invalid.</summary>
    Task SaveAsync(EntityModel entity, object instance, bool isNew, CancellationToken cancellationToken = default);

    Task DeleteAsync(EntityModel entity, IEnumerable<object> instances, CancellationToken cancellationToken = default);
}
