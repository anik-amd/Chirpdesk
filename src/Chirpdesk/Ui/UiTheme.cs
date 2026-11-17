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
    private static bool _dark;

    static UiTheme()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    /// <summary>The last requested end; called again when high contrast flips.</summary>
    public static void Apply(bool dark)
    {
        _dark = dark;

        if (Application.Current is null)
        {
            return;
        }

        var tokens = SystemParameters.HighContrast
            ? HighContrastTokens()
            : new ResourceDictionary { Source = dark ? Dark : Light };

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

        app.Dispatcher.BeginInvoke(() => Apply(_dark));
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
