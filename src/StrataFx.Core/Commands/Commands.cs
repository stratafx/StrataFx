using StrataFx.Data;
using StrataFx.Model;
using StrataFx.Security;

namespace StrataFx.Commands;

/// <summary>Where a command's button appears.</summary>
[Flags]
public enum CommandTarget
{
    List = 1,
    Detail = 2,
    Both = List | Detail,
}

/// <summary>How many selected records a command needs in a list view.</summary>
public enum SelectionRequirement
{
    None,
    Single,
    AtLeastOne,
}

public sealed class CommandContext(EntityModel entity, IReadOnlyList<object> selection, IDataStore data, IServiceProvider services)
{
    public EntityModel Entity { get; } = entity;

    /// <summary>Selected records in a list view, or the current record in a detail view.</summary>
    public IReadOnlyList<object> Selection { get; } = selection;

    public IDataStore Data { get; } = data;
    public IServiceProvider Services { get; } = services;
}

public sealed record CommandResult(bool Success, string? Message, bool RefreshView = true)
{
    public static CommandResult Ok(string? message = null) => new(true, message);
    public static CommandResult Fail(string message) => new(false, message, RefreshView: false);
}

/// <summary>
/// A business operation shown as a button in generated views, separate from any UI technology.
/// Register with <c>fx.AddCommand&lt;T&gt;()</c>; commands are resolved from DI so they can take dependencies.
/// </summary>
public abstract class StrataCommand
{
    public abstract string Id { get; }
    public abstract string Caption { get; }
    public virtual string? Icon => null;
    public virtual CommandTarget Target => CommandTarget.Both;
    public virtual SelectionRequirement Selection => SelectionRequirement.Single;

    /// <summary>
    /// What the user must be allowed to do on the entity to see and run the command. Update by default, since most
    /// commands change records; use Read for commands that only look (export, print), or Delete, or a combination.
    /// </summary>
    public virtual EntityOperation RequiredOperation => EntityOperation.Update;

    /// <summary>When set, the user is asked to confirm before the command runs.</summary>
    public virtual string? ConfirmationMessage => null;

    public virtual bool AppliesTo(EntityModel entity) => true;

    /// <summary>Extra enable/disable logic, e.g. based on the selected records' state.</summary>
    public virtual bool CanExecute(CommandContext context) => context.Selection.Count switch
    {
        0 => Selection == SelectionRequirement.None,
        1 => true,
        _ => Selection != SelectionRequirement.Single,
    };

    public abstract Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default);
}

public static class StrataCommandExtensions
{
    /// <summary>The commands to show for an entity in a list or detail view: matching target and entity, and permitted.</summary>
    public static IEnumerable<StrataCommand> Available(this IEnumerable<StrataCommand> commands,
        EntityModel entity, CommandTarget target, IPermissionService permissions) =>
        commands.Where(c => c.Target.HasFlag(target) && c.AppliesTo(entity) && permissions.IsAllowed(c, entity));

    /// <summary>Whether the current user has the operations the command requires on the entity.</summary>
    public static bool IsAllowed(this IPermissionService permissions, StrataCommand command, EntityModel entity) =>
        permissions.IsAllowed(entity.ClrType, command.RequiredOperation);
}

/// <summary>A command for one entity type, with typed access to the selection.</summary>
public abstract class StrataCommand<TEntity> : StrataCommand where TEntity : class
{
    public sealed override bool AppliesTo(EntityModel entity) => entity.ClrType == typeof(TEntity);

    public sealed override bool CanExecute(CommandContext context) =>
        base.CanExecute(context) && CanExecute(context.Selection.Cast<TEntity>().ToList(), context);

    public sealed override Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default) =>
        ExecuteAsync(context.Selection.Cast<TEntity>().ToList(), context, cancellationToken);

    protected virtual bool CanExecute(IReadOnlyList<TEntity> selection, CommandContext context) => true;

    protected abstract Task<CommandResult> ExecuteAsync(IReadOnlyList<TEntity> selection, CommandContext context, CancellationToken cancellationToken);
}
