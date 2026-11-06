namespace StrataFx.Blazor.Theming;

public enum StrataColorMode
{
    Light,
    Dark,
    /// <summary>Follows the operating system's light or dark setting.</summary>
    System,
}

/// <summary>The themes an app offers. Configure it with <c>AddStrataFxBlazor(themes: t => ...)</c>.</summary>
public sealed class StrataThemeOptions
{
    /// <summary>The themes users can pick from. Starts with the built-in ones.</summary>
    public List<StrataTheme> Themes { get; } = [.. StrataThemes.All];

    /// <summary>The theme used until a user picks another. Fluent slim by default.</summary>
    public string DefaultThemeId { get; set; } = StrataThemes.FluentSlim.Id;

    public StrataColorMode DefaultColorMode { get; set; } = StrataColorMode.Light;

    /// <summary>When false, the theme switcher is hidden and everyone gets the default theme.</summary>
    public bool AllowUserSelection { get; set; } = true;

    public StrataTheme DefaultTheme => Find(DefaultThemeId) ?? Themes.FirstOrDefault() ?? StrataThemes.FluentSlim;

    public StrataTheme? Find(string? id) =>
        id is null ? null : Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The current user's theme and color mode for this circuit. The theme switcher changes it.</summary>
public sealed class StrataThemeState(StrataThemeOptions options)
{
    public StrataThemeOptions Options { get; } = options;
    public StrataTheme Current { get; private set; } = options.DefaultTheme;
    public StrataColorMode ColorMode { get; private set; } = options.DefaultColorMode;

    /// <summary>Raised after the theme or color mode changes.</summary>
    public event Action? Changed;

    public void SetTheme(string id)
    {
        var theme = Options.Find(id);
        if (theme is null || theme == Current) return;
        Current = theme;
        Changed?.Invoke();
    }

    public void SetColorMode(StrataColorMode mode)
    {
        if (mode == ColorMode) return;
        ColorMode = mode;
        Changed?.Invoke();
    }

    /// <summary>Applies a saved choice. Unknown themes (e.g. one removed since) fall back to the default.</summary>
    public void Restore(string? themeId, string? colorMode)
    {
        var theme = Options.AllowUserSelection ? Options.Find(themeId) ?? Current : Current;
        var mode = Enum.TryParse<StrataColorMode>(colorMode, ignoreCase: true, out var m) ? m : ColorMode;
        if (theme == Current && mode == ColorMode) return;
        Current = theme;
        ColorMode = mode;
        Changed?.Invoke();
    }
}
