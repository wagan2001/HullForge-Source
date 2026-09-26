namespace FtdHullGenerator.Domain;

/// <summary>Editor limits are separate from the geometry engine's supported envelope.</summary>
public static class HullEditorSettings
{
    public static HullParameters Default => HullParameters.Default with
    {
        Smoothing = SmoothingMethod.VerticalSlopeFill,
        BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
    };

    public static string? ValidateFeatureAvailability(
        HullParameters parameters,
        FeatureExposurePolicy exposure)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(exposure);
        if (parameters.Smoothing == SmoothingMethod.InvertedTriangleFill)
            return exposure.UnavailableReason(ProductFeature.InvertedTriangleFill);
        return null;
    }

    public static HullParameters RandomizeShape(HullParameters basis, Random random)
    {
        var candidate = HullShapePreset.All[random.Next(HullShapePreset.All.Count)].Apply(basis);
        var shape = candidate.EffectiveShape;
        var maximumRise = Math.Max(0, Math.Min(6, basis.Height - 2));
        double Vary(double value) => Math.Round(Math.Clamp(
            value + random.NextDouble() * 0.4 - 0.2,
            HullShapeSettings.MinimumControl,
            HullShapeSettings.MaximumControl), 2);
        return candidate with
        {
            Shape = shape with
            {
                Bow = shape.Bow with { Fullness = Vary(shape.Bow.Fullness), Flare = Vary(shape.Bow.Flare) },
                Stern = shape.Stern with { Fullness = Vary(shape.Stern.Fullness), SideShape = Vary(shape.Stern.SideShape) },
                Profile = new HullProfileSettings(random.Next(maximumRise + 1), random.Next(maximumRise + 1),
                    random.Next(maximumRise + 1), random.Next(maximumRise + 1)),
            },
        };
    }
}
