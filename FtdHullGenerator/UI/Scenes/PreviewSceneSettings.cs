namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Identifies the presentation surrounding the ship. V01 deliberately exposes only the
/// existing analytical Grid scene; later scene work may append values without changing the
/// physical hull or duplicating its renderer.
/// </summary>
public enum PreviewSceneKind
{
    /// <summary>The existing lattice-inspection presentation.</summary>
    Grid = 0,

    /// <summary>A restrained procedural water and horizon presentation.</summary>
    Ocean = 1,

    /// <summary>Procedural water with non-interactive quay furniture.</summary>
    Dock = 2,
}

/// <summary>
/// How scene furniture is treated while the ship is being inspected from a view it could
/// otherwise obscure. This state describes presentation only; it never changes the ship.
/// </summary>
public enum PreviewSceneOcclusion
{
    /// <summary>The selected environment is shown normally.</summary>
    Normal = 0,

    /// <summary>The environment is made faint for an underside inspection.</summary>
    Faded = 1,

    /// <summary>Occluding water and dock furniture are hidden for a cutaway.</summary>
    Hidden = 2,
}

/// <summary>
/// Immutable presentation-only input for the preview scene. It is intentionally independent
/// of <c>HullParameters</c>, <c>GeneratedHull</c>, and project generation revisions: changing
/// it may replace environment visuals, but can never request or describe structural geometry.
/// </summary>
public sealed record PreviewSceneSettings
{
    /// <summary>
    /// The scene kinds the normal 2.0 product exposes. <see cref="PreviewSceneKind.Dock" />
    /// stays a valid dormant value for preserved implementation and tests, but it is not a
    /// normal product surface.
    /// </summary>
    public static IReadOnlyList<PreviewSceneKind> ProductKinds { get; } =
        [PreviewSceneKind.Grid, PreviewSceneKind.Ocean];

    /// <summary>The exact pre-scene-seam presentation.</summary>
    public static PreviewSceneSettings Grid { get; } = new();

    /// <summary>
    /// Maps a persisted or supplied scene to the normal product surface. A dormant or
    /// unknown kind fails closed to Grid, so an old saved preference cannot start the
    /// normal editor in a scene the product does not offer.
    /// </summary>
    public static PreviewSceneSettings ForProduct(PreviewSceneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ProductKinds.Contains(settings.Kind) ? settings : settings with { Kind = PreviewSceneKind.Grid };
    }

    /// <summary>The selected presentation scene.</summary>
    public PreviewSceneKind Kind { get; init; } = PreviewSceneKind.Grid;

    /// <summary>
    /// Manual world-Y datum used to draw the water surface. This is deliberately not a
    /// draft, buoyancy, displacement, or stability calculation.
    /// </summary>
    public double Waterline { get; init; }

    /// <summary>Draws a presentation grid independently of the selected environment.</summary>
    public bool ShowGridOverlay { get; init; }

    /// <summary>Uses flat water, fewer grid lines, and omits small dock furniture.</summary>
    public bool LowDetail { get; init; }

    /// <summary>
    /// Disables motion cues owned by the scene surface. The first-pass water is already
    /// static; reduced motion additionally uses a flat single-quad surface. Camera auto-turn
    /// remains the independent existing preview preference.
    /// </summary>
    public bool ReducedMotion { get; init; }

    /// <summary>
    /// Uses only the explicit seed and scene time when procedural vertices are created.
    /// It defaults on so evidence captures do not depend on a clock or process state.
    /// </summary>
    public bool Deterministic { get; init; } = true;

    /// <summary>The stable procedural seed used for the water phase.</summary>
    public int SceneSeed { get; init; } = 2048;

    /// <summary>
    /// Explicit presentation time for deterministic water geometry. V02 never advances it
    /// on an idle clock; a caller must deliberately replace the immutable settings value.
    /// </summary>
    public double SceneTimeSeconds { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Kind))
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown preview scene kind.");
        if (!double.IsFinite(Waterline) || Math.Abs(Waterline) > 10_000)
            throw new ArgumentOutOfRangeException(nameof(Waterline), Waterline, "Waterline must be a finite presentation datum within +/-10,000 m.");
        if (!double.IsFinite(SceneTimeSeconds) || Math.Abs(SceneTimeSeconds) > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(SceneTimeSeconds), SceneTimeSeconds, "Scene time must be finite and bounded.");
    }
}
