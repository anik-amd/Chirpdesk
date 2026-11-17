using Chirpdesk.Core.Theming;

namespace Chirpdesk.Core.Tests.Theming;

public class ThemeDecisionTests
{
    [Theory]
    [InlineData(ThemeMode.Dark, true, true)]
    [InlineData(ThemeMode.Dark, false, true)]
    [InlineData(ThemeMode.Light, true, false)]
    [InlineData(ThemeMode.Light, false, false)]
    [InlineData(ThemeMode.System, true, false)]
    [InlineData(ThemeMode.System, false, true)]
    public void Resolves_the_mode_against_the_system_setting(ThemeMode mode, bool systemUsesLight, bool expectedDark)
        => Assert.Equal(expectedDark, ThemeDecision.IsDark(mode, systemUsesLight));

    [Theory]
    [InlineData("dark", ThemeMode.Dark)]
    [InlineData("DARK", ThemeMode.Dark)]
    [InlineData(" light ", ThemeMode.Light)]
    [InlineData("system", ThemeMode.System)]
    [InlineData(null, ThemeMode.System)]
    [InlineData("", ThemeMode.System)]
    [InlineData("solarized", ThemeMode.System)]
    public void Parses_storage_values_and_fails_open(string? value, ThemeMode expected)
        => Assert.Equal(expected, ThemeDecision.Parse(value));

    [Theory]
    [InlineData(ThemeMode.Dark, "dark")]
    [InlineData(ThemeMode.Light, "light")]
    [InlineData(ThemeMode.System, "system")]
    public void Round_trips_the_storage_value(ThemeMode mode, string expected)
    {
        Assert.Equal(expected, ThemeDecision.ToStorageValue(mode));
        Assert.Equal(mode, ThemeDecision.Parse(expected));
    }
}
