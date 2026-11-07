using StrataFx.Data;
using StrataFx.Model;
using StrataFx.Security;
using StrataFx.Validation;

namespace StrataFx.Blazor.Components;

/// <summary>Turns exceptions into messages that are safe to show users.</summary>
public static class ErrorText
{
    public static string For(Exception ex) => ex switch
    {
        StrataAccessDeniedException denied => denied.Message,
        StrataValidationException invalid => string.Join(" ", invalid.Errors.Select(e => e.Message)),
        _ => "Something went wrong. Please try again.",
    };

    /// <summary>
    /// What else a delete removes ("This will also delete 3 orders and their lines."), for the confirmation.
    /// Empty when nothing else is deleted or it cannot be found out: the confirmation still works without it.
    /// </summary>
    public static async Task<string> DeleteImpactAsync(IDataStore data, EntityModel entity, IReadOnlyList<object> instances)
    {
        try
        {
            return await data.GetDeleteImpactAsync(entity, instances) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
