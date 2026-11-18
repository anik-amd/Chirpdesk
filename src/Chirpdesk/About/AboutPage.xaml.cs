using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Chirpdesk.Ui;

namespace Chirpdesk.About;

public partial class AboutPage : UserControl
{
    private readonly Action _back;

    internal AboutPage(Action back)
    {
        InitializeComponent();
        _back = back;
        PageIcon.Content = UiIcons.Create("info", Token("CdAccent"), 24);
        VersionText.Text = $"Version {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"}";
        RenderCredits();
        RenderLicenses();
    }

    private void RenderCredits()
    {
        CreditsPanel.Children.Clear();
        AddCredit(
            ".NET 8 + WPF",
            "Desktop runtime, XAML, data binding and the shell.",
            "MIT",
            "https://github.com/dotnet/wpf");
        AddCredit(
            "Microsoft Edge WebView2",
            "The Chromium surface that hosts each chat service.",
            "Microsoft Software License Terms",
            "https://learn.microsoft.com/microsoft-edge/webview2/");
        AddCredit(
            "Hardcodet.NotifyIcon.Wpf",
            "The tray icon and its menu.",
            "MIT",
            "https://github.com/hardcodet/wpf-notifyicon");
        AddCredit(
            "Catppuccin",
            "The Latte, Frappe, Macchiato and Mocha palettes.",
            "MIT",
            "https://github.com/catppuccin/catppuccin");
        AddCredit(
            "Material Design Icons",
            "Navigation glyph paths in the shell.",
            "Apache-2.0",
            "https://github.com/Templarian/MaterialDesign");
        AddCredit(
            "Simple Icons",
            "Brand marks for the service rail and plugin cards.",
            "CC0 1.0",
            "https://github.com/simple-icons/simple-icons");
        AddCredit(
            "Inno Setup",
            "The per-user Windows installer.",
            "Inno Setup License",
            "https://github.com/jrsoftware/issrc");
    }

    private void RenderLicenses()
    {
        LicensesPanel.Children.Clear();
        AddLicense("MIT", "https://opensource.org/license/mit");
        AddLicense("Apache-2.0", "https://www.apache.org/licenses/LICENSE-2.0");
        AddLicense("CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/");
        AddLicense("Microsoft Software License Terms", "https://learn.microsoft.com/microsoft-edge/webview2/");
        AddLicense("Inno Setup License", "https://jrsoftware.org/files/is/license.txt");
    }

    private void AddCredit(string name, string description, string license, string url)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = Token("CdAccentSoft"),
            BorderBrush = Token("CdRule"),
            BorderThickness = new Thickness(1),
        };
        badge.Child = new TextBlock
        {
            Text = Initials(name),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("CdAccent"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(badge);

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("CdText"),
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var link = new Button
        {
            Content = license,
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(10, 5, 10, 5),
            Tag = url,
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.Click += OnOpenLink;
        Grid.SetColumn(link, 2);
        row.Children.Add(link);

        CreditsPanel.Children.Add(row);
        CreditsPanel.Children.Add(new Border
        {
            Height = 1,
            Background = Token("CdRule"),
            Margin = new Thickness(0, 2, 0, 2),
        });
    }

    private void AddLicense(string name, string url)
    {
        var link = new Hyperlink(new Run(name))
        {
            NavigateUri = new Uri(url),
            Foreground = Token("CdAccent"),
        };
        link.RequestNavigate += (_, e) =>
        {
            Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        };

        var text = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
        text.Inlines.Add(link);
        LicensesPanel.Children.Add(text);
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            Open(url);
        }
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A missing browser must never break the About page.
        }
    }

    private static string Initials(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant()
            : value[..Math.Min(2, value.Length)].ToUpperInvariant();
    }

    private void OnBack(object sender, RoutedEventArgs e) => _back();

    private Brush Token(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Black;
}
