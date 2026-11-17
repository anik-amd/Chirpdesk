using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.CommandBar;

namespace Chirpdesk.CommandBar;

/// <summary>One row: the stable instance id and the name shown to the user.</summary>
public sealed record BarRow(
    string Id,
    string Name,
    System.Windows.FrameworkElement Icon,
    string UnreadText);

/// <summary>
/// The bar's visual. It never takes focus: the host window owns the keys, so the page and the
/// bar cannot fight over them.
/// </summary>
public partial class CommandBarView : UserControl
{
    public CommandBarView() => InitializeComponent();

    public void Render(IReadOnlyList<BarRow> rows, int highlight, BarMode mode, string query, bool dark)
    {
        ApplyPalette(dark);

        Header.Text = mode == BarMode.Search
            ? (string.IsNullOrEmpty(query) ? "Command palette" : query)
            : "Switch service";
        Rows.ItemsSource = rows;
        Rows.SelectedIndex = highlight >= 0 && highlight < rows.Count ? highlight : -1;
        EmptyText.Text = mode == BarMode.Search ? "No matches." : "No warm services.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPalette(bool dark)
    {
        Root.Background = Token("CdPalette");
        Root.BorderBrush = Token("CdPaletteRule");
        Header.Foreground = Token("CdPaletteMuted");
        EmptyText.Foreground = Token("CdPaletteMuted");
        Rows.Foreground = Token("CdPaletteText");
    }

    private System.Windows.Media.Brush Token(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
}
