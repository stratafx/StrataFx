using System.ComponentModel.DataAnnotations;
using StrataFx.Model;

namespace StrataFx.Validation;

public sealed record ValidationError(string? PropertyName, string Message);

public sealed class StrataValidationException(IReadOnlyList<ValidationError> errors)
    : Exception("The record is not valid: " + string.Join(" ", errors.Select(e => e.Message)))
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;
}

/// <summary>Custom validation rules beyond data annotations. Register implementations in DI.</summary>
public interface IEntityRule
{
    bool AppliesTo(EntityModel entity);
    IEnumerable<ValidationError> Validate(EntityModel entity, object instance);
}

/// <summary>
/// Validates an instance with data annotations, the model's Required flags (including non-nullable references)
/// and any registered <see cref="IEntityRule"/>s. Runs in the UI for feedback and in the data store for enforcement.
/// </summary>
public sealed class EntityValidator(IEnumerable<IEntityRule> rules)
{
    public EntityValidator() : this([]) { }

    public IReadOnlyList<ValidationError> Validate(EntityModel entity, object instance) =>
        Validate(entity, instance, parentReference: null);

    /// <param name="parentReference">When validating an owned item: its reference to the parent, which is set on save.</param>
    private List<ValidationError> Validate(EntityModel entity, object instance, PropertyModel? parentReference)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(null!).Select(m => new ValidationError(m, r.ErrorMessage ?? "Invalid value.")))
            .ToList();

        if (parentReference is not null)
            errors.RemoveAll(e => e.PropertyName == parentReference.Name);

        foreach (var prop in entity.Properties.Where(p => p.Required && p.VisibleInDetail && !p.ReadOnly))
        {
            if (prop == parentReference || errors.Any(e => e.PropertyName == prop.Name)) continue;
            var value = prop.GetValue(instance);
            if (value is null || value is string s && string.IsNullOrWhiteSpace(s))
                errors.Add(new ValidationError(prop.Name, $"{prop.Caption} is required."));
        }

        foreach (var rule in rules.Where(r => r.AppliesTo(entity)))
            errors.AddRange(rule.Validate(entity, instance));

        // Owned items are saved with the parent, so they are validated with it. Their errors are reported
        // on the collection, e.g. "Lines, row 2: Quantity must be between 1 and 100."
        foreach (var collection in entity.OwnedCollections)
        {
            var items = collection.GetItems(instance);
            for (var i = 0; i < items.Count; i++)
            {
                foreach (var error in Validate(collection.ElementEntity!, items[i], collection.InverseReference))
                    errors.Add(new ValidationError(collection.Name, $"{collection.Caption}, row {i + 1}: {error.Message}"));
            }
        }

        return errors;
    }

    public void ThrowIfInvalid(EntityModel entity, object instance)
    {
        var errors = Validate(entity, instance);
        if (errors.Count > 0) throw new StrataValidationException(errors);
    }
}
