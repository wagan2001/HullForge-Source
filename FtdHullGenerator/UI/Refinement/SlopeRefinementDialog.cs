using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Projects;
namespace FtdHullGenerator.UI.Refinement;

internal sealed class SlopeRefinementDialog : Window
{
    private sealed class Row
    {
        public required BlockPlacement Anchor { get; init; }
        public bool Available { get; init; }
        public string Host => $"{Anchor.X}, {Anchor.Y}, {Anchor.Z} · {Anchor.Material} · rotation {Anchor.Rotation}" + (Available ? "" : " · STALE: set 4 to remove");
        public string Length { get; set; } = "4";
    }
    public ExplicitSlopeRefinement? Selection { get; private set; }
    public SlopeRefinementDialog(
        IEnumerable<BlockPlacement> anchors,
        ExplicitSlopeRefinement? current,
        FeatureExposurePolicy exposure)
    {
        Title = "Decorative extensions — experimental"; Width = 720; Height = 520; MinWidth = 680; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var available = anchors.ToHashSet();
        var previous = current is { Requests.IsDefault: false } ? current.Requests : [];
        var rows = available.Concat(previous.Where(r => r is not null).Select(r => r.Anchor)).Distinct()
            .Select(anchor => new Row { Anchor = anchor, Available = available.Contains(anchor),
                Length = (previous.FirstOrDefault(r => r is not null && r.Anchor == anchor)?.VisualLengthMetres ?? 4).ToString(CultureInfo.InvariantCulture) }).ToArray();
        var supportedRecipe = current is null || current.RecipeId == NativeSlopeExtensionRule.RecipeId && current.RecipeVersion == NativeSlopeExtensionRule.RecipeVersion;
        var panel = new DockPanel { Margin = new Thickness(16), Background = SystemColors.WindowBrush };
        Content = panel;
        var note = new TextBlock { Text = "Set an explicit visual length for each native 4 m host. 4 leaves it unchanged; 5–40 adds a decoration. Native structure is unchanged. Game review is pending. Cutaway views show native structure only.", Foreground = SystemColors.WindowTextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        if (!supportedRecipe) note.Text = "This saved recipe/version is unsupported. Cancel preserves it; Disable extensions explicitly removes it.";
        else if (!exposure.IsAvailable(ProductFeature.ExplicitSlopeRefinement)) note.Text = exposure.UnavailableReason(ProductFeature.ExplicitSlopeRefinement) + " Cancel preserves the project; Disable extensions restores native output.";
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Native host (x, y, z / material / orientation)", Binding = new Binding(nameof(Row.Host)), IsReadOnly = true, MinWidth = 420, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Visual length (m)", Binding = new Binding(nameof(Row.Length)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 140 });
        panel.Children.Add(grid);
        var disable = new Button { Content = "Disable extensions", Margin = new Thickness(4) };
        disable.Click += (_, _) => { Selection = null; DialogResult = true; }; buttons.Children.Add(disable);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(4) }; buttons.Children.Add(cancel);
        var apply = new Button { Content = "Apply", Margin = new Thickness(4), IsEnabled = exposure.IsAvailable(ProductFeature.ExplicitSlopeRefinement) && supportedRecipe };
        apply.Click += (_, _) =>
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
            if (!TryBuildSelection(rows.Select(r => (r.Anchor, r.Available, r.Length)), out var selection, out var error))
            { MessageBox.Show(this, error); return; }
            Selection = selection;
            DialogResult = true;
        }; buttons.Children.Add(apply);
    }
    internal static bool TryBuildSelection(IEnumerable<(BlockPlacement Anchor, bool Available, string Length)> rows,
        out ExplicitSlopeRefinement? selection, out string? error)
    {
        selection = null; error = null;
        var requests = ImmutableArray.CreateBuilder<SlopeExtensionRequest>();
        foreach (var row in rows)
        {
            if (!int.TryParse(row.Length, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 4 or > 40)
            { error = "Enter whole lengths from 4 through 40 metres."; return false; }
            if (length == 4) continue;
            if (!row.Available)
            { error = "A selected native host no longer exists. Set its length to 4 to remove it, or Cancel to preserve the selection."; return false; }
            requests.Add(new(row.Anchor, length));
        }
        selection = requests.Count == 0 ? null : new(NativeSlopeExtensionRule.RecipeId, NativeSlopeExtensionRule.RecipeVersion, requests.ToImmutable());
        return true;
    }

}
