using System.Globalization;
using MudBlazor;

namespace StrataFx.Blazor.Theming;

/// <summary>The colors a StrataFx theme is built from, for one color mode.</summary>
public sealed record StrataThemeColors(
    string Background,
    string Surface,
    string Lines,
    string InputLines,
    string Text,
    string TextMuted,
    string Primary,
    string OnPrimary);

/// <summary>A selectable theme: a MudBlazor theme plus the information the theme switcher shows.</summary>
public sealed record StrataTheme(string Id, string Name, string Description, StrataThemeColors Light, StrataThemeColors Dark, MudTheme Theme)
{
    /// <summary>Wraps an existing MudTheme so it can be offered in the theme switcher.</summary>
    public static StrataTheme FromMudTheme(string id, string name, MudTheme theme, string description = "") =>
        new(id, name, description, ColorsOf(theme.PaletteLight), ColorsOf(theme.PaletteDark), theme);

    private static StrataThemeColors ColorsOf(Palette p) => new(
        p.Background.ToString(), p.Surface.ToString(), p.LinesDefault.ToString(), p.LinesInputs.ToString(),
        p.TextPrimary.ToString(), p.TextSecondary.ToString(), p.Primary.ToString(), p.PrimaryContrastText.ToString());
}

/// <summary>
/// The built-in themes. All of them are "slim": hairline borders instead of heavy shadows, small corners,
/// a 48px app bar and compact type. <see cref="FluentSlim"/> is the default.
/// </summary>
public static class StrataThemes
{
    // One entry per font: MudBlazor quotes each entry, so a comma-separated list in one string would not work.
    private static readonly string[] SystemFonts = ["system-ui", "-apple-system", "Helvetica Neue", "Arial", "sans-serif"];

    public static StrataTheme FluentSlim { get; } = Create(
        "fluent-slim", "Fluent slim", "Enterprise blue with 4px corners and Segoe-style type.",
        fonts: ["Segoe UI Variable Text", "Segoe UI", .. SystemFonts], radius: 4,
        light: new("#FAFAFA", "#FFFFFF", "#E0E0E0", "#C7C7C7", "#242424", "#616161", "#0F6CBD", "#FFFFFF"),
        dark: new("#1F1F1F", "#292929", "#3D3D3D", "#5C5C5C", "#FFFFFF", "#ADADAD", "#479EF5", "#0A0A0A"));

    public static StrataTheme Graphite { get; } = Create(
        "graphite", "Graphite", "Monochrome with a near-black accent and Geist type.",
        fonts: ["Geist", .. SystemFonts], radius: 6,
        light: new("#FFFFFF", "#FFFFFF", "#EAEAEA", "#D4D4D4", "#0A0A0A", "#666666", "#0A0A0A", "#FFFFFF"),
        dark: new("#0A0A0A", "#111111", "#262626", "#404040", "#EDEDED", "#A1A1A1", "#EDEDED", "#0A0A0A"));

    public static StrataTheme IndigoSlate { get; } = Create(
        "indigo-slate", "Indigo slate", "Cool slate neutrals with a muted indigo accent and Inter type.",
        fonts: ["Inter", .. SystemFonts], radius: 6,
        light: new("#FBFBFC", "#FFFFFF", "#E6E6EA", "#D0D1D8", "#1B1B1F", "#6B6F76", "#5E6AD2", "#FFFFFF"),
        dark: new("#0F1011", "#17181A", "#26282C", "#3E4147", "#E6E6E8", "#8A8F98", "#7C85E0", "#0F1011"));

    public static StrataTheme Nordic { get; } = Create(
        "nordic", "Nordic", "The Nord palette's soft frost blues with IBM Plex Sans.",
        fonts: ["IBM Plex Sans", .. SystemFonts], radius: 4,
        light: new("#ECEFF4", "#FFFFFF", "#D8DEE9", "#BCC4D3", "#2E3440", "#4C566A", "#5E81AC", "#FFFFFF"),
        dark: new("#2E3440", "#3B4252", "#434C5E", "#5A657B", "#ECEFF4", "#D8DEE9", "#88C0D0", "#2E3440"));

    public static StrataTheme ZincEmerald { get; } = Create(
        "zinc-emerald", "Zinc emerald", "Zinc neutrals with an emerald accent and DM Sans.",
        fonts: ["DM Sans", .. SystemFonts], radius: 8,
        light: new("#FFFFFF", "#FFFFFF", "#E4E4E7", "#D4D4D8", "#09090B", "#71717A", "#059669", "#FFFFFF"),
        dark: new("#09090B", "#18181B", "#27272A", "#3F3F46", "#FAFAFA", "#A1A1AA", "#10B981", "#022C22"));

    /// <summary>All built-in themes, default first.</summary>
    public static IReadOnlyList<StrataTheme> All { get; } = [FluentSlim, Graphite, IndigoSlate, Nordic, ZincEmerald];

    /// <summary>Builds a slim theme from a set of colors. Use it to add your own brand theme.</summary>
    public static StrataTheme Create(string id, string name, string description, string[] fonts, int radius,
        StrataThemeColors light, StrataThemeColors dark)
    {
        var theme = new MudTheme
        {
            PaletteLight = Apply(new PaletteLight(), light, dark: false),
            PaletteDark = Apply(new PaletteDark(), dark, dark: true),
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = $"{radius}px",
                AppbarHeight = "48px",
                DrawerWidthLeft = "232px",
            },
            Typography = CreateTypography(fonts),
            Shadows = CreateShadows(),
        };
        return new StrataTheme(id, name, description, light, dark, theme);
    }

    private static T Apply<T>(T p, StrataThemeColors c, bool dark) where T : Palette
    {
        p.Primary = c.Primary;
        p.PrimaryContrastText = c.OnPrimary;
        p.Secondary = c.TextMuted;
        p.SecondaryContrastText = c.Surface;
        p.Background = c.Background;
        p.BackgroundGray = c.Background;
        p.Surface = c.Surface;
        p.AppbarBackground = c.Surface;
        p.AppbarText = c.Text;
        p.DrawerBackground = c.Surface;
        p.DrawerText = c.Text;
        p.DrawerIcon = c.TextMuted;
        p.TextPrimary = c.Text;
        p.TextSecondary = c.TextMuted;
        p.TextDisabled = Alpha(c.Text, 0.38);
        p.ActionDefault = c.TextMuted;
        p.ActionDisabled = Alpha(c.Text, 0.3);
        p.ActionDisabledBackground = Alpha(c.Text, 0.08);
        p.LinesDefault = c.Lines;
        p.LinesInputs = c.InputLines;
        p.TableLines = c.Lines;
        p.Divider = c.Lines;
        p.DividerLight = c.Lines;
        p.TableHover = Alpha(c.Primary, dark ? 0.10 : 0.05);
        p.TableStriped = Alpha(c.Text, 0.02);
        p.Skeleton = Alpha(c.Text, 0.08);
        p.OverlayDark = "rgba(0,0,0,0.45)";

        // Status colors: readable on the surface, with contrast text that works on the filled variants.
        p.Success = dark ? "#54B054" : "#107C10";
        p.Warning = dark ? "#F2A93B" : "#B25E09";
        p.Error = dark ? "#F1707B" : "#C42B1C";
        p.Info = dark ? "#58A6FF" : "#0969DA";
        var onStatus = dark ? "#0A0A0A" : "#FFFFFF";
        p.SuccessContrastText = onStatus;
        p.WarningContrastText = onStatus;
        p.ErrorContrastText = onStatus;
        p.InfoContrastText = onStatus;
        return p;
    }

    private static Typography CreateTypography(string[] fonts) => new()
    {
        Default = new DefaultTypography { FontFamily = fonts, FontSize = ".875rem", FontWeight = "400", LineHeight = "1.43", LetterSpacing = "normal" },
        H4 = new H4Typography { FontFamily = fonts, FontSize = "1.75rem", FontWeight = "500", LineHeight = "1.25", LetterSpacing = "normal" },
        H5 = new H5Typography { FontFamily = fonts, FontSize = "1.375rem", FontWeight = "500", LineHeight = "1.3", LetterSpacing = "normal" },
        H6 = new H6Typography { FontFamily = fonts, FontSize = "1.0625rem", FontWeight = "500", LineHeight = "1.4", LetterSpacing = "normal" },
        Subtitle1 = new Subtitle1Typography { FontFamily = fonts, FontSize = ".9375rem", FontWeight = "500", LineHeight = "1.4", LetterSpacing = "normal" },
        Subtitle2 = new Subtitle2Typography { FontFamily = fonts, FontSize = ".8125rem", FontWeight = "500", LineHeight = "1.4", LetterSpacing = "normal" },
        Body1 = new Body1Typography { FontFamily = fonts, FontSize = ".875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
        Body2 = new Body2Typography { FontFamily = fonts, FontSize = ".8125rem", FontWeight = "400", LineHeight = "1.43", LetterSpacing = "normal" },
        Button = new ButtonTypography { FontFamily = fonts, FontSize = ".8125rem", FontWeight = "500", LineHeight = "1.75", LetterSpacing = "normal", TextTransform = "none" },
        Caption = new CaptionTypography { FontFamily = fonts, FontSize = ".75rem", FontWeight = "400", LineHeight = "1.4", LetterSpacing = "normal" },
        Overline = new OverlineTypography { FontFamily = fonts, FontSize = ".6875rem", FontWeight = "500", LineHeight = "1.6", LetterSpacing = ".04em" },
    };

    /// <summary>Low elevations become hairline outlines; menus and dialogs keep a soft shadow.</summary>
    private static Shadow CreateShadows()
    {
        const string hairline = "0 0 0 1px var(--mud-palette-lines-default)";
        var shadows = new Shadow();
        var e = shadows.Elevation;
        e[1] = hairline;
        e[2] = $"{hairline}, 0 1px 2px rgba(0,0,0,0.05)";
        e[3] = $"{hairline}, 0 1px 3px rgba(0,0,0,0.07)";
        e[4] = $"{hairline}, 0 2px 4px rgba(0,0,0,0.07)";
        for (var i = 5; i < e.Length; i++)
            e[i] = $"0 0 0 1px var(--mud-palette-lines-default), 0 {Math.Min(i, 16)}px {Math.Min(i * 2, 40)}px rgba(0,0,0,0.12)";
        return shadows;
    }

    private static string Alpha(string hex, double alpha)
    {
        var h = hex.TrimStart('#');
        int r = Convert.ToInt32(h[..2], 16), g = Convert.ToInt32(h[2..4], 16), b = Convert.ToInt32(h[4..6], 16);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{alpha:0.###})");
    }
}
