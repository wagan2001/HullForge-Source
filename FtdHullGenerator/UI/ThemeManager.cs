using System.Windows;

namespace FtdHullGenerator.UI;

/// <summary>One of the two palettes the shared control templates can be dressed in.</summary>
public enum AppTheme
{
    /// <summary>The light bench, one click away from the standard dark one.</summary>
    Workbench,

    /// <summary>The same bench in darker stock with a warmer signal band. The default.</summary>
    Forge,
}

/// <summary>
/// Swaps the palette dictionary merged at the top of the application resources. The
/// control templates never move, so a swap changes only colour: the same shapes, the same
/// spacing, the same animations. Palette brushes are referenced as DynamicResource, which
/// is what lets a swap reach controls that are already on screen.
/// </summary>
public static class ThemeManager
{
    // App.xaml seeds the merged dictionary with the same palette, so the two must agree
    // until the first Apply swaps one for the other.
    private static AppTheme _current = AppTheme.Forge;

    /// <summary>The palette currently merged into the application resources.</summary>
    public static AppTheme Current => _current;

    /// <summary>Raised after a swap, so views holding un-templated colour can follow it.</summary>
    public static event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>
    /// Merges a palette in place of the current one. The palette is always the first
    /// merged dictionary, so it is replaced by index rather than by searching for a key
    /// that both palettes define. A repeated request for the palette already in place is
    /// ignored, since re-merging would rebuild every brush for no visible change.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } application)
            return;
        var dictionaries = application.Resources.MergedDictionaries;
        if (dictionaries.Count == 0)
            return;
        if (_current == theme && dictionaries[0].Source is not null)
            return;

        dictionaries[0] = new ResourceDictionary { Source = SourceFor(theme) };
        _current = theme;
        ThemeChanged?.Invoke(null, theme);
    }

    private static Uri SourceFor(AppTheme theme) => new(
        theme == AppTheme.Forge
            ? "pack://application:,,,/HullForge;component/Themes/Forge.xaml"
            : "pack://application:,,,/HullForge;component/Themes/Workbench.xaml",
        UriKind.Absolute);
}
