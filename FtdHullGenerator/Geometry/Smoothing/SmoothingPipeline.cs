using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Dispatches a <see cref="SmoothingMethod" /> to its <see cref="ISmoothingPass" />.
/// Adding a method is one entry in <see cref="Passes" />; the generator stays
/// unchanged, while the UI choice and blueprint-name suffix are wired separately.
/// </summary>
public static class SmoothingPipeline
{
    private static readonly IReadOnlyList<ISmoothingPass> Passes =
    [
        new VerticalSlopeFillPass(),
        new HorizontalSlopeFillPass(),
        new CrossSectionSlopeFillPass(),
        new CombinedSlopeFillPass(),
        new HybridSlopeFillPass(),
    ];

    /// <summary>
    /// Returns the hull with the requested smoothing applied. <see cref="SmoothingMethod.None" />
    /// returns the input unchanged.
    /// </summary>
    /// <exception cref="HullGenerationException">Thrown when no pass implements the requested method.</exception>
    public static GeneratedHull Apply(GeneratedHull hull, SmoothingMethod method)
    {
        ArgumentNullException.ThrowIfNull(hull);
        // Deco Vertical/Horizontal are the proven native pass plus a derived visual extension;
        // the physical stage must never see the decorative identity itself.
        method = SmoothingMethodMapping.NativeBase(method);
        if (method == SmoothingMethod.None)
            return hull;
        if (method == SmoothingMethod.InvertedTriangleFill)
            throw new HullGenerationException(["Inverted construction requires the sampled hull surface. Generate the hull with this smoothing method selected."]);

        foreach (var pass in Passes)
        {
            if (pass.Method == method)
                return pass.Apply(hull);
        }

        throw new HullGenerationException([$"No smoothing pass implements {method}."]);
    }
}
