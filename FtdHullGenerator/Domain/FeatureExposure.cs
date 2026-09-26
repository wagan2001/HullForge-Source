namespace FtdHullGenerator.Domain;

/// <summary>The product capabilities whose maturity affects runtime exposure.</summary>
public enum ProductFeature
{
    ExpandedHullOptions = 0,
    Superstructures = 1,
    InternalStructures = 2,
    ExplicitSlopeRefinement = 3,
    InvertedTriangleFill = 4,
}

/// <summary>The single V2 development lifecycle for a product capability.</summary>
public enum FeatureMaturity
{
    Stable = 0,
    Experimental = 1,
    Unavailable = 2,
}

/// <summary>
/// Central runtime exposure policy for the one Hull Forge development executable.
/// Feature implementations depend on maturity only; commercial entitlement is deliberately absent.
/// </summary>
public sealed class FeatureExposurePolicy
{
    public FeatureExposurePolicy(bool experimentalFeaturesEnabled)
    {
        ExperimentalFeaturesEnabled = experimentalFeaturesEnabled;
    }

    public bool ExperimentalFeaturesEnabled { get; }

    /// <summary>
    /// The frozen 2.0 surface. Internal Structures is the only experimental feature; the
    /// deferred post-2.0 systems are unavailable even though their implementation is preserved.
    /// </summary>
    public static FeatureMaturity MaturityOf(ProductFeature feature) => feature switch
    {
        ProductFeature.ExpandedHullOptions => FeatureMaturity.Stable,
        ProductFeature.Superstructures => FeatureMaturity.Unavailable,
        ProductFeature.InternalStructures => FeatureMaturity.Experimental,
        ProductFeature.ExplicitSlopeRefinement => FeatureMaturity.Unavailable,
        ProductFeature.InvertedTriangleFill => FeatureMaturity.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown product feature."),
    };

    public bool IsAvailable(ProductFeature feature) => MaturityOf(feature) switch
    {
        FeatureMaturity.Stable => true,
        FeatureMaturity.Experimental => ExperimentalFeaturesEnabled,
        FeatureMaturity.Unavailable => false,
        _ => false,
    };

    public string UnavailableReason(ProductFeature feature) => MaturityOf(feature) switch
    {
        FeatureMaturity.Experimental =>
            feature == ProductFeature.InternalStructures
                ? "Internal structures require Experimental Features to be turned on."
                : $"{LabelFor(feature)} requires Experimental Features to be turned on.",
        FeatureMaturity.Unavailable => feature switch
        {
            ProductFeature.Superstructures =>
                "Superstructures are not part of the Hull Forge 2.0 product surface.",
            ProductFeature.ExplicitSlopeRefinement =>
                "Manual decorative slope extensions are not part of the Hull Forge 2.0 product surface.",
            _ => $"{LabelFor(feature)} is unavailable because implementation or required evidence is incomplete.",
        },
        _ => string.Empty,
    };

    private static string LabelFor(ProductFeature feature) => feature switch
    {
        ProductFeature.ExpandedHullOptions => "Expanded hull options",
        ProductFeature.Superstructures => "Superstructures",
        ProductFeature.InternalStructures => "Internal structures",
        ProductFeature.ExplicitSlopeRefinement => "Decorative slope extensions",
        ProductFeature.InvertedTriangleFill => "Inverted triangle fill",
        _ => feature.ToString(),
    };
}
