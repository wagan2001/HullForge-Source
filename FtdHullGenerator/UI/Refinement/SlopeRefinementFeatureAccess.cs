using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Projects;
namespace FtdHullGenerator.UI.Refinement;
public static class SlopeRefinementFeatureAccess
{
    public static bool CanPreviewOrExport(
        ShipDocument document,
        FeatureExposurePolicy exposure,
        out string? reason)
    {
        reason = document.Smoothing.ExplicitRefinement is not null &&
                 !exposure.IsAvailable(ProductFeature.ExplicitSlopeRefinement)
            ? exposure.UnavailableReason(ProductFeature.ExplicitSlopeRefinement) +
              " The loaded intent is preserved; explicitly clear the extension to generate."
            : null;
        return reason is null;
    }
}
