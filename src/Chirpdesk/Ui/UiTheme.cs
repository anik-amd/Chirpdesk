using System.Windows;

namespace Chirpdesk.Ui;

/// <summary>Swaps the token dictionary; every page uses DynamicResource so the change is live.</summary>
internal static class UiTheme
{
    private static readonly Uri Light = new("pack://application:,,,/Chirpdesk;component/Ui/TokensLight.xaml");
    private static readonly Uri Dark = new("pack://application:,,,/Chirpdesk;component/Ui/TokensDark.xaml");

    public static void Apply(bool dark)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = dark ? Dark : Light,
        };
    }
}
