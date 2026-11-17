namespace Chirpdesk.Core.Theming;

/// <summary>What the user asked for. <see cref="System"/> follows Windows.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Dark/light resolution is a pure decision so it is testable without a window, a registry
/// or a webview. Windows itself is asked separately, at the edge.
/// </summary>
public static class ThemeDecision
{
    public static bool IsDark(ThemeMode mode, bool systemUsesLight) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => !systemUsesLight,
    };

    /// <summary>Anything unrecognised falls back to following the system, never to a crash.</summary>
    public static ThemeMode Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            _ => ThemeMode.System,
        };

    public static string ToStorageValue(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => "dark",
        ThemeMode.Light => "light",
        _ => "system",
    };
}
