using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Projects;

namespace FtdHullGenerator.UI.Editor;

/// <summary>
/// Lossless decisions needed when an immutable hull document is projected into the legacy WPF
/// controls. Keeping them explicit prevents the form from silently changing saved semantics.
/// </summary>
public readonly record struct EditorControlProjection(
    bool KeepSingleBlocks,
    bool BottomArmorInheritsSide,
    ArmorLayout DisplayedBottomArmor,
    SuperstructureStyle LegacySuperstructureStyle)
{
    public static EditorControlProjection FromHull(HullParameters parameters) => new(
        KeepSingleBlocks: !parameters.Beamify,
        BottomArmorInheritsSide: parameters.BottomArmor is null,
        DisplayedBottomArmor: parameters.EffectiveBottomArmor,
        LegacySuperstructureStyle: parameters.EffectiveSuperstructure.Style);

    public static ArmorLayout? ResolveBottomArmor(bool inheritsSide, ArmorLayout displayedBottomArmor) =>
        inheritsSide ? null : displayedBottomArmor;

    public static bool CanGenerate(HullSource source) => source.Kind == HullSourceKind.RegionalShapeV2;

    public static bool CanGenerate(
        ShipDocument document,
        FeatureExposurePolicy exposure,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanGenerate(document.Source))
        {
            reason = "This project references a historical hull envelope that this build cannot resolve. " +
                     "Its intent is preserved, but no regional substitute will be previewed or exported.";
            return false;
        }

        if (HullEditorSettings.ValidateFeatureAvailability(document.Hull, exposure) is { } accessError)
        {
            reason = accessError + " The saved project intent is preserved; explicitly edit the unavailable setting to generate.";
            return false;
        }

        reason = null;
        return true;
    }
}
