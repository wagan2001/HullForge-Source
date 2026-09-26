namespace FtdHullGenerator.Domain.Sketchbook;

/// <summary>
/// One immutable Alternate Naval Sketchbook design candidate. The entry carries its complete
/// Shape V2 bundle, its suggested and minimum dimensions, an explicit bulb state and a
/// comparison-only smoothing recommendation.
/// </summary>
/// <remarks>
/// The entry is a launcher, not a mode. <see cref="Shape" /> is complete: its body style is
/// always <see cref="BodyStyle.Custom" /> and every regional control is set explicitly, so
/// applying the entry never inherits an unrelated value from the caller. <see cref="Bulb" /> is
/// likewise always explicit, whether the bulb is switched on or deliberately disabled.
/// <see cref="RecommendedSmoothing" /> is presentation metadata only; it is never written to
/// <see cref="HullParameters.Smoothing" />.
/// <para>
/// <see cref="Family" />, <see cref="Traits" /> and <see cref="Provenance" /> are Team C
/// presentation metadata: they describe the generated hull shape for the browser only. They
/// never enter geometry, the size policies, the geometry-distinctness signature or an editor
/// transaction, and they make no speed, stability, seakeeping or armor claim. The Team B fields
/// and every geometry value are unchanged.
/// </para>
/// </remarks>
/// <param name="Family">The browse group: one of the four Team C presentation families.</param>
/// <param name="Traits">Two to four short, unique shape descriptions. Presentation only.</param>
/// <param name="Provenance">The honest design-study origin of the candidate. Presentation only.</param>
public sealed record AlternateNavalSketchbookEntry(
    string Id,
    int Ordinal,
    string Name,
    string Role,
    string Inspiration,
    int SuggestedLength,
    int SuggestedWidth,
    int SuggestedHeight,
    int MinimumLength,
    int MinimumWidth,
    int MinimumHeight,
    BowStyle BowStyle,
    SternStyle SternStyle,
    HullShapeSettings Shape,
    bool HasBulb,
    BulbSettings Bulb,
    SmoothingMethod RecommendedSmoothing,
    string RepresentationNote,
    string Family,
    IReadOnlyList<string> Traits,
    string Provenance)
{
    /// <summary>The suggested envelope, for the browser's compact size line.</summary>
    public string EnvelopeSummary => $"{SuggestedLength} × {SuggestedWidth} × {SuggestedHeight} m";

    /// <summary>The smallest supported envelope, for the browser's compact size line.</summary>
    public string MinimumSummary => $"{MinimumLength} × {MinimumWidth} × {MinimumHeight} m";

    /// <summary>The immutable baseline profile the size policies scale from.</summary>
    public HullProfileSettings BaselineProfile => Shape.Profile;

    /// <summary>The traits as one compact line, for a browser that cannot render a list.</summary>
    public string TraitsLine => string.Join(" · ", Traits);
}
