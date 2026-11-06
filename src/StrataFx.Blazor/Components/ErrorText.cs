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
}
