using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Projects;

/// <summary>
/// Persisted reserved values from the original V2 planning contract. Non-None values are retained
/// losslessly on load but remain unavailable: automatic anchor/length selection is unresolved.
/// </summary>
public enum DecorativeRefinementKind
{
    None = 0,
    VerticalRuns = 1,
    HorizontalRuns = 2,
    VerticalAndHorizontalRuns = 3,
}

/// <summary>
/// The smoothing intent of a document. NativeMethod keeps its existing serialized numeric
/// meaning: CombinedSlopeFill = 4 is never silently rewritten to a newer automatic method, and
/// the offset HybridSlopeFill = 5 keeps its top-band semantics. The replacement automatic method
/// is added as a new append-only identity by its own task.
/// AlgorithmVersion identifies the revision of the chosen method that produced a saved resolved
/// plan, so reopening against a changed algorithm is a visible event rather than a silent one.
/// RefinementMaxRunMetres is retained only for schema compatibility; it has no supported product
/// meaning for explicit decorative refinement and is never converted into its versioned recipe.
/// </summary>
public sealed record SmoothingSettings(
    SmoothingMethod NativeMethod,
    int AlgorithmVersion = 1,
    DecorativeRefinementKind Refinement = DecorativeRefinementKind.None,
    int RefinementMaxRunMetres = DesignLimits.LegacyRefinementRunMetres)
{
    public ExplicitSlopeRefinement? ExplicitRefinement { get; init; }

    public const int FirstAlgorithmVersion = 1;

    public static SmoothingSettings FromParameters(HullParameters parameters) =>
        new(parameters.Smoothing, FirstAlgorithmVersion, DecorativeRefinementKind.None,
            DesignLimits.LegacyRefinementRunMetres);

    public bool HasDecorativeRefinement => Refinement != DecorativeRefinementKind.None || ExplicitRefinement is not null;

    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (ExplicitRefinement is { } explicitRefinement)
            foreach (var diagnostic in explicitRefinement.Validate()) yield return diagnostic;
        if (!Enum.IsDefined(NativeMethod))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SmoothingMethodUnsupported,
                "The document selects an unsupported native smoothing method.", field: nameof(NativeMethod));
        if (AlgorithmVersion < FirstAlgorithmVersion)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SmoothingAlgorithmVersionUnsupported,
                $"The smoothing algorithm version must be at least {FirstAlgorithmVersion}.",
                field: nameof(AlgorithmVersion));
        if (!Enum.IsDefined(Refinement))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.SmoothingMethodUnsupported,
                "The document selects an unsupported decorative refinement.", field: nameof(Refinement));
        else if (Refinement != DecorativeRefinementKind.None)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.DecorativeRefinementUnavailable,
                DesignSeverity.Error,
                "Legacy automatic refinement selection is unavailable. Use an explicit versioned anchor selection; legacy run limits are not converted.",
                Field: nameof(Refinement),
                SuggestedCorrection: "Keep native smoothing or remove the refinement request.");
    }
}
