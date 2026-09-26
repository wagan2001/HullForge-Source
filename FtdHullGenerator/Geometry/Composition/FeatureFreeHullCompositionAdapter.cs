using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Composition;

/// <summary>
/// Opt-in compatibility seam from the pre-materialization hull context to the existing
/// feature-free physical hull. It deliberately adds no component intent, ownership override,
/// opening, or automatic smoothing choice.
/// </summary>
/// <remarks>
/// The ordinary <see cref="HullGenerator.Generate"/> route remains the product default. V2
/// composition callers use this adapter only after creating one <see cref="HullBuildContext"/>
/// for the revision. Materialization consumes the exact solid, shell, armor and local-surface
/// evaluation captured by that context; it does not reconstruct them from sampled queries.
/// </remarks>
public static class FeatureFreeHullCompositionAdapter
{
    /// <summary>
    /// Produces the current generator's physical output without applying any V2 component.
    /// Existing beam/pole packing, native smoothing, and legacy superstructure stages retain
    /// their established behavior.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the context contains an opening mask. Openings require the future owned-cut
    /// composition path and cannot be silently ignored by feature-free compatibility.
    /// </exception>
    public static GeneratedHull Compose(
        HullBuildContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.DeckOpenings.Count != 0)
        {
            throw new InvalidOperationException(
                "Feature-free composition cannot consume authorized deck openings; use the owned component composition path.");
        }

        return context.ComposeFeatureFree(cancellationToken);
    }
}
