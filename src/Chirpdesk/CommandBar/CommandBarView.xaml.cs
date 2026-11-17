using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.CommandBar;

namespace Chirpdesk.CommandBar;

/// <summary>One row: the stable instance id and the name shown to the user.</summary>
public sealed record BarRow(string Id, string Name);

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
            ? (string.IsNullOrEmpty(query) ? "Search tabs" : query)
            : "Switch service";
        Rows.ItemsSource = rows;
        Rows.SelectedIndex = highlight >= 0 && highlight < rows.Count ? highlight : -1;
        EmptyText.Text = mode == BarMode.Search ? "No matches." : "No warm services.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPalette(bool dark)
    {
        var window = dark ? Color.FromRgb(0x1A, 0x17, 0x1C) : Color.FromRgb(0xFB, 0xFA, 0xF8);
        var border = dark ? Color.FromRgb(0x2C, 0x27, 0x30) : Color.FromRgb(0xDD, 0xD7, 0xD0);
        var text = dark ? Color.FromRgb(0xC9, 0xC4, 0xCC) : Color.FromRgb(0x33, 0x30, 0x36);
        var muted = dark ? Color.FromRgb(0x8F, 0x88, 0x94) : Color.FromRgb(0x6B, 0x65, 0x60);
        var selection = dark ? Color.FromRgb(0x34, 0x2C, 0x3E) : Color.FromRgb(0xE7, 0xE0, 0xD8);

        Root.Background = new SolidColorBrush(window);
        Root.BorderBrush = new SolidColorBrush(border);
        Header.Foreground = new SolidColorBrush(muted);
        EmptyText.Foreground = new SolidColorBrush(muted);
        Rows.Foreground = new SolidColorBrush(text);
        Resources["BarSelectionBrush"] = new SolidColorBrush(selection);
    }
}
