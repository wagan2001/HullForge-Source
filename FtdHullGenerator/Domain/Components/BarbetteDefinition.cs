using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;

namespace FtdHullGenerator.Domain.Components;

/// <summary>
/// The frozen 2.0 centerline barbette intent. A barbette is a protected clear internal volume
/// surrounded by independently layered side, roof, bottom and neck armor, plus a concentric
/// square neck opening. It is deliberately not a turret mechanism — no bearing ring, gun,
/// ammunition handling, AI or rotating subconstruct is generated or implied.
/// </summary>
/// <remarks>
/// <para>
/// The clear diameter and clear depth describe usable empty space only; armor thickness is never
/// counted inside them. The user never enters an outside diameter: exterior extents are derived
/// from the clear volume plus the requested armor stacks.
/// </para>
/// <para>
/// Armor stacks follow the same <see cref="ArmorLayout"/> vocabulary as hull armor. Layer zero is
/// the layer nearest the protected clear volume and each later layer grows away from it, so the
/// last layer is the exposed/exterior one. Truncating requested armor therefore removes the
/// highest layer index first, exactly the frozen "outermost layer inward" rule.
/// </para>
/// </remarks>
public sealed record BarbetteDefinition(
    string Id,
    string NodeId,
    DesignMeasure ClearDiameter,
    int ClearDepthMetres,
    int TopOffsetMetres,
    ArmorLayout SideArmor,
    ArmorLayout RoofArmor,
    ArmorLayout BottomArmor,
    ArmorLayout NeckArmor,
    int NeckClearSizeMetres)
{
    /// <summary>The only neck clear openings frozen 2.0 supports: 1 m, 3 m or 5 m square.</summary>
    public static IReadOnlyList<int> SupportedNeckClearSizesMetres { get; } = Array.AsReadOnly([1, 3, 5]);

    /// <summary>The smallest supported clear diameter: one whole odd metre.</summary>
    public const int MinimumClearDiameterMetres = 1;

    /// <summary>
    /// True when the requested clear internal diameter is a legal product value: positive, a whole
    /// number of metres, an odd number of metres, and inside the design coordinate bound. This is
    /// the same predicate <see cref="Validate"/> enforces, exposed so the normal editor can offer
    /// only legal values without duplicating the rule.
    /// </summary>
    public static bool IsSupportedClearDiameter(DesignMeasure diameter) =>
        diameter > DesignMeasure.Zero &&
        diameter.IsWholeMetre &&
        IsOddWholeMetre(diameter) &&
        diameter.IsWithinDesignBounds;

    /// <summary>
    /// The largest legal clear diameter inside the design coordinate bound: the largest odd whole
    /// metre not above <see cref="DesignLimits.MaxDesignTwiceMetres"/>.
    /// </summary>
    public static DesignMeasure MaximumSupportedClearDiameter { get; } = ComputeMaximumSupportedClearDiameter();

    /// <summary>
    /// The next legal clear diameter strictly above <paramref name="current"/>, on the odd
    /// whole-metre progression 1, 3, 5, 7, 9, 11, … . Values below the minimum advance to it; a
    /// value already at <see cref="MaximumSupportedClearDiameter"/> returns that maximum rather than
    /// an illegal out-of-range or overflowing value. The arithmetic is computed in 64 bits so no
    /// legal <see cref="DesignMeasure"/> input can throw.
    /// </summary>
    public static DesignMeasure NextSupportedClearDiameter(DesignMeasure current)
    {
        var metres = current.FloorMetres;
        if (metres < MinimumClearDiameterMetres)
            return DesignMeasure.FromMetres(MinimumClearDiameterMetres);
        // The largest odd whole metre not above the current value is metres or metres-1; the next
        // odd whole metre strictly above it is +2 from an odd floor and +1 from an even floor.
        var next = (metres & 1) == 1 ? (long)metres + 2 : (long)metres + 1;
        var maximumMetres = MaximumSupportedClearDiameter.FloorMetres;
        return next > maximumMetres
            ? MaximumSupportedClearDiameter
            : DesignMeasure.FromMetres((int)next);
    }

    /// <summary>
    /// The largest legal clear diameter strictly below <paramref name="current"/>, or <c>null</c>
    /// when none exists. Never overflows and never returns an out-of-range value.
    /// </summary>
    public static DesignMeasure? PreviousSupportedClearDiameter(DesignMeasure current)
    {
        var metres = current.FloorMetres;
        var previous = (metres & 1) == 1 ? (long)metres - 2 : (long)metres - 1;
        var maximumMetres = MaximumSupportedClearDiameter.FloorMetres;
        if (previous > maximumMetres)
            previous = maximumMetres;
        return previous < MinimumClearDiameterMetres ? null : DesignMeasure.FromMetres((int)previous);
    }

    private static DesignMeasure ComputeMaximumSupportedClearDiameter()
    {
        var wholeMetres = DesignLimits.MaxDesignTwiceMetres / 2;
        var oddMetres = (wholeMetres & 1) == 1 ? wholeMetres : wholeMetres - 1;
        return DesignMeasure.FromMetres(oddMetres);
    }

    private static bool IsOddWholeMetre(DesignMeasure value) =>
        value.IsWholeMetre && ((value.TwiceMetres / 2) & 1) == 1;

    /// <summary>
    /// The frozen minimum structural separator between two distinct protected clear volumes.
    /// Armor may overlap and merge; the clear cavities may not come closer than this.
    /// </summary>
    public const int MinimumClearSeparationMetres = 1;

    /// <summary>A barbette with the default one-metre solid Metal armor on every stack and a 3 m neck.</summary>
    public static BarbetteDefinition Create(
        string id,
        string nodeId,
        DesignMeasure clearDiameter,
        int clearDepthMetres,
        int topOffsetMetres = 0,
        int neckClearSizeMetres = 3,
        ArmorLayout? sideArmor = null,
        ArmorLayout? roofArmor = null,
        ArmorLayout? bottomArmor = null,
        ArmorLayout? neckArmor = null) => new(
        id,
        nodeId,
        clearDiameter,
        clearDepthMetres,
        topOffsetMetres,
        sideArmor ?? ArmorLayout.Single(MaterialKind.Metal),
        roofArmor ?? ArmorLayout.Single(MaterialKind.Metal),
        bottomArmor ?? ArmorLayout.Single(MaterialKind.Metal),
        neckArmor ?? ArmorLayout.Single(MaterialKind.Metal),
        neckClearSizeMetres);

    /// <summary>The square neck clear opening as a design measure.</summary>
    public DesignMeasure NeckClearSize => DesignMeasure.FromMetres(NeckClearSizeMetres);

    /// <summary>The exact exterior square side of the neck armor, in whole metres.</summary>
    public int NeckExteriorSizeMetres => NeckArmor is null
        ? NeckClearSizeMetres
        : NeckClearSizeMetres + 2 * NeckArmor.Thickness;

    public int SideArmorThicknessMetres => SideArmor?.Thickness ?? 0;
    public int RoofArmorThicknessMetres => RoofArmor?.Thickness ?? 0;
    public int BottomArmorThicknessMetres => BottomArmor?.Thickness ?? 0;
    public int NeckArmorThicknessMetres => NeckArmor?.Thickness ?? 0;

    public IEnumerable<DesignDiagnostic> Validate()
    {
        foreach (var diagnostic in Arrangement.RequireIdentifier(Id, "barbette", Id))
            yield return diagnostic;
        foreach (var diagnostic in Arrangement.RequireIdentifier(NodeId, "barbette node reference", Id))
            yield return diagnostic;

        if (ClearDiameter <= DesignMeasure.Zero)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteClearDiameterInvalid, DesignSeverity.Error,
                $"Barbette '{Id}' needs a positive clear internal diameter.", Id, nameof(ClearDiameter),
                ClearDiameter, null,
                "Enter the protected clear bore the turret mechanism must fit inside.");
        else if (!ClearDiameter.IsWholeMetre || !IsOddWholeMetre(ClearDiameter))
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteClearDiameterInvalid, DesignSeverity.Error,
                $"Barbette '{Id}' clear diameter must be a whole odd number of metres " +
                "(1, 3, 5, 7, 9, 11, … m).",
                Id, nameof(ClearDiameter), ClearDiameter, null,
                "Choose a whole odd clear diameter; an even or fractional bore is never rounded or clamped.");
        else if (!ClearDiameter.IsWithinDesignBounds)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteClearDiameterInvalid, DesignSeverity.Error,
                $"Barbette '{Id}' clear diameter is outside the supported design range.", Id, nameof(ClearDiameter),
                ClearDiameter, null,
                "Reduce the clear diameter before rasterization.");

        if (ClearDepthMetres < 1)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteClearDepthInvalid, DesignSeverity.Error,
                $"Barbette '{Id}' needs at least one metre of protected clear depth.", Id, nameof(ClearDepthMetres),
                DesignMeasure.FromMetres(ClearDepthMetres), DesignMeasure.FromMetres(1),
                "Request at least 1 m of clear internal depth.");

        if (TopOffsetMetres < 0)
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteTopOffsetInvalid, DesignSeverity.Error,
                $"Barbette '{Id}' cannot have a negative top offset below the reference deck plane.",
                Id, nameof(TopOffsetMetres), DesignMeasure.FromMetres(TopOffsetMetres), DesignMeasure.Zero,
                "Zero is the default; increase the offset to lower the barbette.");

        if (!SupportedNeckClearSizesMetres.Contains(NeckClearSizeMetres))
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteNeckSizeUnsupported, DesignSeverity.Error,
                $"Barbette '{Id}' requests a {NeckClearSizeMetres} m neck opening; 2.0 supports exactly 1 m, 3 m or 5 m.",
                Id, nameof(NeckClearSizeMetres), DesignMeasure.FromMetres(NeckClearSizeMetres), null,
                "Choose a 1 m, 3 m or 5 m square neck.");

        foreach (var diagnostic in ValidateStack(SideArmor, nameof(SideArmor), "side"))
            yield return diagnostic;
        foreach (var diagnostic in ValidateStack(RoofArmor, nameof(RoofArmor), "roof"))
            yield return diagnostic;
        foreach (var diagnostic in ValidateStack(BottomArmor, nameof(BottomArmor), "bottom"))
            yield return diagnostic;
        foreach (var diagnostic in ValidateStack(NeckArmor, nameof(NeckArmor), "neck"))
            yield return diagnostic;
    }

    private IEnumerable<DesignDiagnostic> ValidateStack(ArmorLayout? stack, string field, string label)
    {
        if (stack is null || stack.Layers.Count == 0)
        {
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteArmorStackInvalid, DesignSeverity.Error,
                $"Barbette '{Id}' needs at least one {label} armor layer.", Id, field, null, null,
                $"Add an initial structural {label} armor layer.");
            yield break;
        }

        if (stack.Layers[0].IsAir)
        {
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteClearBoundaryNotArmor, DesignSeverity.Error,
                $"Barbette '{Id}' has an air gap as the innermost {label} layer, so the clear volume would include it.",
                Id, field, null, null,
                $"Make the layer against the protected clear volume a structural {label} armor layer.");
        }

        // The frozen 2.0 barbette contract layers solid armor only. Poles and beam slopes are an
        // ordinary hull-armor construction; barbette generation never realizes them, so an imported,
        // persisted or direct non-Solid layer is rejected here instead of being silently coerced.
        for (var index = 0; index < stack.Layers.Count; index++)
        {
            var layer = stack.Layers[index];
            if (layer.IsAir || layer.Construction == ArmorConstruction.Solid)
                continue;
            yield return new DesignDiagnostic(DesignDiagnosticCodes.BarbetteConstructionUnsupported,
                DesignSeverity.Error,
                $"Barbette '{Id}' {label} armor layer {index + 1} requests {layer.Construction}; " +
                "2.0 barbette armor is Solid only.",
                Id, field, null, null,
                $"Use Solid {label} armor; poles and beam slopes are not part of the frozen barbette contract.");
        }
    }
}
