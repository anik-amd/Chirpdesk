using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.CommandBar;
using Chirpdesk.Ui;

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
    private bool _syncing;

    public CommandBarView()
    {
        InitializeComponent();
        SearchIcon.Content = UiIcons.Create("search", Token("CdPaletteMuted"), 18);
    }

    public event EventHandler<string>? QueryChanged;

    public void FocusQuery()
    {
        if (QueryBox.Visibility == Visibility.Visible)
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text.Length;
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing)
        {
            QueryChanged?.Invoke(this, QueryBox.Text);
        }
    }

    public void Render(IReadOnlyList<BarRow> rows, int highlight, BarMode mode, string query, bool dark)
    {
        ApplyPalette(dark);

        if (mode == BarMode.Search)
        {
            Header.Text = "Search tabs, plugins, commands…";
            Header.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
            QueryBox.Visibility = Visibility.Visible;

            if (!string.Equals(QueryBox.Text, query, StringComparison.Ordinal))
            {
                _syncing = true;
                QueryBox.Text = query;
                _syncing = false;
                QueryBox.CaretIndex = QueryBox.Text.Length;
            }
        }
        else
        {
            Header.Text = "Switch service";
            Header.Visibility = Visibility.Visible;
            QueryBox.Visibility = Visibility.Collapsed;
        }

        Hint.Text = mode == BarMode.Search
            ? "Enter to run · Esc back"
            : "Tab to move · release Ctrl to switch";
        Hint.Foreground = Token("CdPaletteMuted");

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
        QueryBox.Foreground = Token("CdPaletteText");
        QueryBox.CaretBrush = Token("CdPaletteAccent");
        QueryBox.SelectionBrush = Token("CdPaletteSelection");
        EmptyText.Foreground = Token("CdPaletteMuted");
        Rows.Foreground = Token("CdPaletteText");
    }

    private System.Windows.Media.Brush Token(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
}
