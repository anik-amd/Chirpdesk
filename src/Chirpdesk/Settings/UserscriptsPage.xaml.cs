using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.Integrations;
using Chirpdesk.Ui;

namespace Chirpdesk.Settings;

/// <summary>
/// The manager UI for the Userscripts integration plugin. Every action is an RPC command to the
/// helper; this page owns no script files and performs no network calls itself.
/// </summary>
public partial class UserscriptsPage : UserControl
{
    private readonly Func<string, string?, Task<IntegrationCommandResult>> _send;
    private readonly Action _back;
    private bool _loading;
    private string? _pendingToken;

    internal UserscriptsPage(
        Func<string, string?, Task<IntegrationCommandResult>> send,
        Action back)
    {
        InitializeComponent();
        _send = send;
        _back = back;
        PageIcon.Content = UiIcons.Create("plugins", Token("CdAccent"), 24);
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var result = await SendAsync("userscripts.state", null);
        if (result is null)
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<StateDto>(result, Options);
            if (state is null)
            {
                return;
            }

            _loading = true;
            CheckUpdatesBox.IsChecked = state.Config.Check;
            AutoBox.IsChecked = state.Config.Auto;
            _loading = false;

            ConfigNote.Text = string.IsNullOrWhiteSpace(state.Config.LastCheck)
                ? "Never checked."
                : $"Last checked: {state.Config.LastCheck}";

            RenderScripts(state.Scripts);
            EmptyText.Visibility = state.Scripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (JsonException)
        {
            StatusText.Text = "The manager returned an unreadable state.";
        }
    }

    private void RenderScripts(IReadOnlyList<ScriptDto> scripts)
    {
        ScriptsPanel.Children.Clear();

        foreach (var script in scripts)
        {
            var card = new Border
            {
                Style = (Style)FindResource("CdPanel"),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(16),
            };

            var stack = new StackPanel();
            var head = new DockPanel();

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            remove.Click += async (_, _) => await SendAndRefreshAsync("userscripts.remove", script.Id);
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(script.Version)
                    ? script.Name
                    : $"{script.Name} {script.Version}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            head.Children.Add(title);
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock
            {
                Text = script.Matches.Count == 0
                    ? "No patterns."
                    : "Runs on: " + string.Join(", ", script.Matches),
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8),
            });

            var toggles = new WrapPanel();
            var enabled = new CheckBox
            {
                Content = "Enabled",
                IsChecked = script.Enabled,
                Foreground = Token("CdText"),
                Margin = new Thickness(0, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.Checked += async (_, _) => await SendAndRefreshAsync("userscripts.enable", JsonSerializer.Serialize(new { id = script.Id, enabled = true }));
            enabled.Unchecked += async (_, _) => await SendAndRefreshAsync("userscripts.enable", JsonSerializer.Serialize(new { id = script.Id, enabled = false }));
            toggles.Children.Add(enabled);

            var auto = new CheckBox
            {
                Content = "Auto-update",
                IsChecked = script.AutoUpdate,
                Foreground = Token("CdText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            auto.Checked += async (_, _) => await SendAndRefreshAsync("userscripts.auto", JsonSerializer.Serialize(new { id = script.Id, enabled = true }));
            auto.Unchecked += async (_, _) => await SendAndRefreshAsync("userscripts.auto", JsonSerializer.Serialize(new { id = script.Id, enabled = false }));
            toggles.Children.Add(auto);
            stack.Children.Add(toggles);

            if (script.UpdateAvailable)
            {
                var update = new Button
                {
                    Content = "Update now",
                    Style = (Style)FindResource("PillButton"),
                    Padding = new Thickness(12, 5, 12, 5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                update.Click += async (_, _) => await SendAndRefreshAsync("userscripts.update", script.Id);
                stack.Children.Add(update);
            }

            if (!string.IsNullOrWhiteSpace(script.SourceUrl))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = script.SourceUrl,
                    FontSize = 10,
                    Foreground = Token("CdTextMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }

            card.Child = stack;
            ScriptsPanel.Children.Add(card);
        }
    }

    private async void OnConfigChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        await SendAndRefreshAsync(
            "userscripts.config",
            JsonSerializer.Serialize(new { check = CheckUpdatesBox.IsChecked == true, auto = AutoBox.IsChecked == true }));
    }

    private async void OnCheck(object sender, RoutedEventArgs e) =>
        await SendAndRefreshAsync("userscripts.check", null);

    private async void OnUpdateAll(object sender, RoutedEventArgs e) =>
        await SendAndRefreshAsync("userscripts.update", "all");

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var url = PromptText("Add userscript", "https://example.com/script.user.js");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var result = await SendAsync("userscripts.add", url);
        if (result is null)
        {
            return;
        }

        try
        {
            var candidate = JsonSerializer.Deserialize<CandidateDto>(result, Options);
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Token))
            {
                return;
            }

            _pendingToken = candidate.Token;
            ConsentTitle.Text = $"Install {candidate.Name} {candidate.Version}?";
            ConsentBody.Text =
                "It runs in the page with your signed-in session and can read anything you can see.\n\n" +
                "Runs on:\n  " + string.Join("\n  ", candidate.Matches.Concat(candidate.Includes)) + "\n\n" +
                "Grants: " + (candidate.Grants.Count == 0 ? "none" : string.Join(", ", candidate.Grants)) + "\n\n" +
                $"SHA-256:\n{candidate.Sha256}\n\nSource:\n{candidate.SourceUrl}";
            ConsentLayer.Visibility = Visibility.Visible;
        }
        catch (JsonException)
        {
            StatusText.Text = "The manager returned an unreadable candidate.";
        }
    }

    private async void OnConsentInstall(object sender, RoutedEventArgs e)
    {
        var token = _pendingToken;
        _pendingToken = null;
        ConsentLayer.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(token))
        {
            await SendAndRefreshAsync("userscripts.install", token);
        }
    }

    private void OnConsentCancel(object sender, RoutedEventArgs e)
    {
        _pendingToken = null;
        ConsentLayer.Visibility = Visibility.Collapsed;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppPaths.DataRoot, "integrations", "userscripts");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnBack(object sender, RoutedEventArgs e) => _back();

    private async Task SendAndRefreshAsync(string command, string? argument)
    {
        await SendAsync(command, argument);
        await RefreshAsync();
    }

    private async Task<string?> SendAsync(string command, string? argument)
    {
        StatusText.Text = string.Empty;

        IntegrationCommandResult result;
        try
        {
            result = await _send(command, argument);
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
            return null;
        }

        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "The userscript manager refused the request.";
            return null;
        }

        return result.Message;
    }

    private string? PromptText(string title, string placeholder)
    {
        var window = new Window
        {
            Title = title,
            Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Token("CdCanvas"),
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Paste the https URL of a .user.js file. The plugin downloads it once for the consent step; updates come from the script's own update URL.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Token("CdTextMuted"),
            Margin = new Thickness(0, 0, 0, 10),
        });

        var box = new TextBox { Text = placeholder, Padding = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(box);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Next", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        ok.Click += (_, _) =>
        {
            window.DialogResult = true;
            window.Close();
        };
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        panel.Children.Add(actions);
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private Brush Token(string key) => (Brush)FindResource(key);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record StateDto(ConfigDto Config, List<ScriptDto> Scripts);

    private sealed record ConfigDto(bool Check, bool Auto, string? LastCheck);

    private sealed record ScriptDto(
        string Id,
        string Name,
        string? Version,
        bool Enabled,
        bool AutoUpdate,
        List<string> Matches,
        string? SourceUrl,
        bool UpdateAvailable,
        string? LastChecked);

    private sealed record CandidateDto(
        string Token,
        string Name,
        string? Version,
        List<string> Matches,
        List<string> Includes,
        List<string> Grants,
        string Sha256,
        string SourceUrl);
}
