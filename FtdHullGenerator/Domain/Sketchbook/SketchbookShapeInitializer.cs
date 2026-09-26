using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;

namespace FtdHullGenerator.Domain.Sketchbook;

/// <summary>
/// The outcome of resolving and applying one sketchbook entry: the document that should be
/// committed plus the design diagnostics that document already produces.
/// </summary>
public sealed record SketchbookApplication(
    AlternateNavalSketchbookEntry Entry,
    SketchbookSizePolicy Policy,
    ShipDocument Document,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    /// <summary>True when the resolved document carries at least one error diagnostic.</summary>
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// Resolves a sketchbook entry into a complete document against an immutable baseline. The
/// initializer never accumulates scaling, never silently clamps, and never mutates the baseline
/// or the catalog entry; every scaled value is recomputed from the entry's baseline profile.
/// </summary>
public static class SketchbookShapeInitializer
{
    /// <summary>
    /// Scales one baseline rise from the entry's suggested height to the current height, using a
    /// single <see cref="MidpointRounding.AwayFromZero"/> rounding from the immutable baseline.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="suggestedHeight" /> is below two, so the baseline has no usable span, or
    /// <paramref name="currentHeight" /> is below one.
    /// </exception>
    public static int ScaleRise(int baselineRise, int suggestedHeight, int currentHeight)
    {
        if (suggestedHeight < 2)
            throw new ArgumentOutOfRangeException(nameof(suggestedHeight), suggestedHeight,
                "A sketchbook entry's suggested height must be at least 2 m to scale its profile.");
        if (currentHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(currentHeight), currentHeight,
                "The current hull height must be at least 1 m to scale a profile.");

        return (int)Math.Round(
            baselineRise * (double)(currentHeight - 1) / (suggestedHeight - 1),
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Resolves the complete Shape V2 bundle for one entry. Suggested dimensions use the entry's
    /// exact baseline profile; keeping the current dimensions scales each baseline rise
    /// independently. Every dimensionless control is copied verbatim and is never scaled.
    /// </summary>
    public static HullShapeSettings ResolveShape(
        AlternateNavalSketchbookEntry entry,
        SketchbookSizePolicy policy,
        int currentHeight)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (policy == SketchbookSizePolicy.SuggestedDimensions)
            return entry.Shape;

        if (policy != SketchbookSizePolicy.KeepCurrentDimensions)
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown sketchbook size policy.");

        var baseline = entry.Shape.Profile;
        return entry.Shape with
        {
            Profile = new HullProfileSettings(
                ScaleRise(baseline.BowDeckRise, entry.SuggestedHeight, currentHeight),
                ScaleRise(baseline.SternDeckRise, entry.SuggestedHeight, currentHeight),
                ScaleRise(baseline.BowKeelRise, entry.SuggestedHeight, currentHeight),
                ScaleRise(baseline.SternKeelRise, entry.SuggestedHeight, currentHeight)),
        };
    }

    /// <summary>
    /// Resolves the complete parameter set for one entry. The basis keeps its armor, deck, bottom
    /// armor, beamification, smoothing, hybrid offset and superstructure; only the dimensions, the
    /// bow/stern styles, the three legacy shape aliases, the Shape V2 bundle and the explicit bulb
    /// state change.
    /// </summary>
    public static HullParameters ResolveParameters(
        AlternateNavalSketchbookEntry entry,
        SketchbookSizePolicy policy,
        HullParameters basis)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(basis);

        var shape = ResolveShape(entry, policy, basis.Height);
        var (length, width, height) = policy switch
        {
            SketchbookSizePolicy.SuggestedDimensions =>
                (entry.SuggestedLength, entry.SuggestedWidth, entry.SuggestedHeight),
            SketchbookSizePolicy.KeepCurrentDimensions =>
                (basis.Length, basis.Width, basis.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown sketchbook size policy."),
        };

        return basis with
        {
            Length = length,
            Width = width,
            Height = height,
            BowStyle = entry.BowStyle,
            SternStyle = entry.SternStyle,
            BowFullness = shape.Bow.Fullness,
            SternFullness = shape.Stern.Fullness,
            CrossSectionCurve = shape.Body.Fullness,
            Shape = shape,
            HasBulb = entry.HasBulb,
            Bulb = entry.Bulb,
        };
    }

    /// <summary>
    /// Applies one entry to a document through the same shape of edit the normal editor commits.
    /// The document is returned even when it carries error diagnostics: components are never
    /// moved, deleted or resized and the result is never clamped to make validation pass.
    /// </summary>
    public static SketchbookApplication Apply(
        AlternateNavalSketchbookEntry entry,
        SketchbookSizePolicy policy,
        ShipDocument basis)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(basis);

        var parameters = ResolveParameters(entry, policy, basis.Hull);
        // The generator's odd lattice mirrors around X=0; an even lattice spans -width/2 through
        // width/2-1 and therefore mirrors around X=-0.5. Update the datum only when the resolved
        // centre plane differs, preserving a null datum when nothing needs to change.
        var centerPlaneX = parameters.HasSingleBlockCenterline
            ? DesignMeasure.Zero
            : DesignMeasure.FromTwiceMetres(-1);
        var document = basis with
        {
            Hull = parameters,
            Datum = basis.EffectiveDatum.CenterPlaneX == centerPlaneX
                ? basis.Datum
                : basis.EffectiveDatum with { CenterPlaneX = centerPlaneX },
        };

        return new SketchbookApplication(entry, policy, document, document.Validate());
    }
}
