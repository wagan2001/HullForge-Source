using System.Windows.Media.Media3D;

namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Owns the two deliberately separate branches of the preview visual tree. Environment
/// content is presentation furniture and is never considered a ship hit; the ship branch
/// contains only models derived from the current <c>GeneratedHull</c> placement list.
/// </summary>
internal sealed class PreviewSceneVisualRoot
{
    private readonly ModelVisual3D _root = new();
    private readonly ModelVisual3D _environment = new();
    private readonly ModelVisual3D _ship = new();
    private readonly Func<PreviewSceneSettings, Model3D?> _createEnvironment;

    public PreviewSceneVisualRoot(
        IEnumerable<Visual3D> environmentLights,
        ModelVisual3D shipContent,
        ModelVisual3D shipHighlight,
        Func<PreviewSceneSettings, Model3D?> createEnvironment)
    {
        ArgumentNullException.ThrowIfNull(environmentLights);
        ArgumentNullException.ThrowIfNull(shipContent);
        ArgumentNullException.ThrowIfNull(shipHighlight);
        ArgumentNullException.ThrowIfNull(createEnvironment);
        _createEnvironment = createEnvironment;

        foreach (var light in environmentLights)
            _environment.Children.Add(light);

        _ship.Children.Add(shipContent);
        _ship.Children.Add(shipHighlight);
        _root.Children.Add(_environment);
        _root.Children.Add(_ship);
    }

    /// <summary>The single visual attached to the viewport.</summary>
    public ModelVisual3D Visual => _root;

    /// <summary>The environment model, separate from lights and ship geometry.</summary>
    internal Model3D? EnvironmentContent => _environment.Content;

    /// <summary>
    /// Replaces only scene furniture. The Grid implementation is intentionally empty because
    /// its existing two-dimensional lattice remains in the host XAML behind the viewport.
    /// </summary>
    public void Apply(PreviewSceneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var content = _createEnvironment(settings);
        _environment.Content = content;
    }

    /// <summary>
    /// Returns true only for a model reachable through the ship branch. A future ocean or
    /// dock may contain arbitrary geometry without becoming selectable as part of the ship.
    /// </summary>
    public bool IsShipModel(Model3D? model) => model is not null &&
        (_ship.Children.OfType<ModelVisual3D>().Any(child => Contains(child.Content, model)));

    private static bool Contains(Model3D? candidate, Model3D target)
    {
        if (ReferenceEquals(candidate, target))
            return true;
        return candidate is Model3DGroup group && group.Children.Any(child => Contains(child, target));
    }
}
