using System.ComponentModel;
using System.Windows;

namespace Chirpdesk.Ui;

/// <summary>
/// Swaps the token dictionary; every page uses DynamicResource so the change is live.
/// High contrast wins over light/dark: when Windows asks for it the tokens come from the
/// system colour set, so text and selection stay legible whatever the active theme.
/// </summary>
internal static class UiTheme
{
    private static readonly Uri Light = new("pack://application:,,,/Chirpdesk;component/Ui/TokensLight.xaml");
    private static readonly Uri Dark = new("pack://application:,,,/Chirpdesk;component/Ui/TokensDark.xaml");

    private static readonly Dictionary<string, Uri> Catppuccin = new(StringComparer.OrdinalIgnoreCase)
    {
        ["latte"] = new("pack://application:,,,/Chirpdesk;component/Ui/CatppuccinLatte.xaml"),
        ["frappe"] = new("pack://application:,,,/Chirpdesk;component/Ui/CatppuccinFrappe.xaml"),
        ["macchiato"] = new("pack://application:,,,/Chirpdesk;component/Ui/CatppuccinMacchiato.xaml"),
        ["mocha"] = new("pack://application:,,,/Chirpdesk;component/Ui/CatppuccinMocha.xaml"),
    };

    private static bool _dark;
    private static string? _lightFlavor;
    private static string? _darkFlavor;
    private static bool _useCatppuccin;

    static UiTheme()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    /// <summary>The last requested end; called again when high contrast flips.</summary>
    public static void Apply(
        bool dark,
        string? lightFlavor = null,
        string? darkFlavor = null,
        bool useCatppuccin = false)
    {
        _dark = dark;
        _lightFlavor = lightFlavor;
        _darkFlavor = darkFlavor;
        _useCatppuccin = useCatppuccin;

        if (Application.Current is null)
        {
            return;
        }

        ResourceDictionary tokens;

        if (SystemParameters.HighContrast)
        {
            tokens = HighContrastTokens();
        }
        else
        {
            var flavor = dark ? darkFlavor : lightFlavor;
            if (useCatppuccin && flavor is not null && Catppuccin.TryGetValue(flavor, out var source))
            {
                tokens = new ResourceDictionary { Source = source };
            }
            else
            {
                tokens = new ResourceDictionary { Source = dark ? Dark : Light };
            }
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;

        if (dictionaries.Count == 0)
        {
            dictionaries.Add(tokens);
        }
        else
        {
            dictionaries[0] = tokens;
        }
    }

    private static void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            return;
        }

        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(() => Apply(_dark, _lightFlavor, _darkFlavor, _useCatppuccin));
    }

    /// <summary>
    /// A full token set from SystemColors. System brushes follow the user's contrast theme, and
    /// rebuilding on the change means a frozen brush never outlives the theme it came from.
    /// </summary>
    private static ResourceDictionary HighContrastTokens() => new()
    {
        ["CdCanvas"] = SystemColors.WindowBrush,
        ["CdSurface"] = SystemColors.WindowBrush,
        ["CdSurfaceAlt"] = SystemColors.WindowBrush,
        ["CdText"] = SystemColors.WindowTextBrush,
        ["CdTextMuted"] = SystemColors.GrayTextBrush,
        ["CdRule"] = SystemColors.WindowTextBrush,
        ["CdAccent"] = SystemColors.HighlightBrush,
        ["CdAccentSoft"] = SystemColors.WindowBrush,
        ["CdDanger"] = SystemColors.WindowTextBrush,
        ["CdOnAccent"] = SystemColors.HighlightTextBrush,

        ["CdPalette"] = SystemColors.WindowBrush,
        ["CdPaletteSurface"] = SystemColors.WindowBrush,
        ["CdPaletteRaised"] = SystemColors.WindowBrush,
        ["CdPaletteText"] = SystemColors.WindowTextBrush,
        ["CdPaletteMuted"] = SystemColors.GrayTextBrush,
        ["CdPaletteRule"] = SystemColors.WindowTextBrush,
        ["CdPaletteSelection"] = SystemColors.HighlightBrush,
        ["CdPaletteAccent"] = SystemColors.HighlightBrush,
        ["CdScrollThumb"] = SystemColors.WindowTextBrush,
        ["CdControlHover"] = SystemColors.HighlightBrush,
        ["CdScrim"] = SystemColors.WindowBrush,
    };
}
