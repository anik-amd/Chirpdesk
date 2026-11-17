using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Tests.Plugins;

public class PanelValidatorTests
{
    private static PanelDefinition Panel() => new()
    {
        Title = "Disk cleaner",
        Sections =
        [
            new PanelSection { Type = "text", Id = "note", Text = "Sessions are never trimmed." },
        ],
        Actions =
        [
            new PanelAction
            {
                Id = "trim",
                Label = "Trim caches",
                Primitive = "trimCache",
                Args = new PanelActionArgs { BudgetMb = 60, Targets = "all" },
            },
            new PanelAction
            {
                Id = "reveal",
                Label = "Open folder",
                Primitive = "revealFolder",
                Args = new PanelActionArgs { Root = "profiles" },
            },
        ],
    };

    [Fact]
    public void A_valid_panel_has_no_errors()
    {
        Assert.Empty(PanelValidator.Validate(Panel()));
    }

    [Fact]
    public void An_unknown_section_is_removed_without_killing_the_panel()
    {
        var panel = Panel();
        panel.Sections.Add(new PanelSection { Type = "iframe" });

        var errors = PanelValidator.Validate(panel);

        Assert.Contains(errors, error => error.Contains("iframe", StringComparison.Ordinal));
        Assert.Single(panel.Sections);
    }

    [Fact]
    public void An_unknown_action_is_removed_but_other_actions_stay()
    {
        var panel = Panel();
        panel.Actions.Add(new PanelAction { Id = "run", Label = "Run", Primitive = "shell" });

        var errors = PanelValidator.Validate(panel);

        Assert.Contains(errors, error => error.Contains("shell", StringComparison.Ordinal));
        Assert.Equal(2, panel.Actions.Count);
    }

    [Fact]
    public void A_reveal_folder_must_name_a_known_root()
    {
        var panel = Panel();
        panel.Actions[1].Args = new PanelActionArgs { Root = "C:\\Windows" };

        var errors = PanelValidator.Validate(panel);

        Assert.Contains(errors, error => error.Contains("known root", StringComparison.Ordinal));
        Assert.Single(panel.Actions);
    }

    [Fact]
    public void A_trim_budget_outside_the_range_disables_the_action()
    {
        var panel = Panel();
        panel.Actions[0].Args = new PanelActionArgs { BudgetMb = 5000, Targets = "all" };

        var errors = PanelValidator.Validate(panel);

        Assert.Contains(errors, error => error.Contains("budget", StringComparison.Ordinal));
        Assert.Single(panel.Actions);
    }

    [Fact]
    public void A_text_section_without_text_is_removed()
    {
        var panel = Panel();
        panel.Sections[0].Text = " ";

        var errors = PanelValidator.Validate(panel);

        Assert.NotEmpty(errors);
        Assert.Empty(panel.Sections);
    }
}
