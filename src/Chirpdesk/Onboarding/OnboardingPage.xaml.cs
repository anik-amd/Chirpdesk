using System.Windows;
using System.Windows.Controls;

namespace Chirpdesk.Onboarding;

/// <summary>
/// Shown once, on the first launch after install. It teaches the three surfaces that are not
/// obvious in a hidden-warm app: the plugin page, the palette and the tray. It is a page, not a
/// popup, and it is the only time the window appears without an explicit user action (the launch
/// itself is the action; autostart and diagnostics never reach it).
/// </summary>
public partial class OnboardingPage : UserControl
{
    private readonly Action _openPlugins;
    private readonly Action _dismiss;

    public OnboardingPage(Action openPlugins, Action dismiss)
    {
        _openPlugins = openPlugins;
        _dismiss = dismiss;
        InitializeComponent();
    }

    private void OnOpenPlugins(object sender, RoutedEventArgs e) => _openPlugins();

    private void OnDismiss(object sender, RoutedEventArgs e) => _dismiss();
}
