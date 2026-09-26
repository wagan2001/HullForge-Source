using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Projects;

namespace FtdHullGenerator.UI.Components;

/// <summary>
/// C09 exposure adapter for experimental internal structures.
/// </summary>
public static class InternalStructureFeatureAccess
{
    public static bool IsEditorAvailable(FeatureExposurePolicy exposure) =>
        exposure.IsAvailable(ProductFeature.InternalStructures);

    public static bool CanPreviewOrExport(
        ShipDocument document,
        FeatureExposurePolicy exposure,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Internals.Families.Any(family => family.Enabled) ||
            IsEditorAvailable(exposure))
        {
            reason = null;
            return true;
        }

        reason = exposure.UnavailableReason(ProductFeature.InternalStructures) +
                 " The loaded project intent is preserved; turn Experimental Features on or disable every internal family explicitly.";
        return false;
    }
}
