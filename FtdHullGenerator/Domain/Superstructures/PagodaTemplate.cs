using System.Collections.Immutable;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Superstructures;

public sealed record PagodaTierDefinition(
    int Level,
    int LengthMetres,
    int WidthMetres,
    int OffsetAlongMetres,
    int ClearHeightMetres);

/// <summary>Origin metadata stays separate from the ordinary editable module graph.</summary>
public sealed record SuperstructureTemplateOrigin(string TemplateId, int Version, string DisplayName)
{
    public static SuperstructureTemplateOrigin Custom { get; } = new("custom", 0, "Custom");
}

public sealed record PagodaTemplateApplication(
    SuperstructureLayout Layout,
    SuperstructureTemplateOrigin Origin,
    string RootId,
    string SpanNodeId)
{
    /// <summary>
    /// Returns this template origin only while the graph still equals the applied values. A normal
    /// module edit therefore becomes Custom without changing how the graph itself is generated.
    /// </summary>
    public SuperstructureTemplateOrigin OriginFor(SuperstructureLayout current) =>
        GraphEquals(current, Layout) ? Origin : SuperstructureTemplateOrigin.Custom;

    private static bool GraphEquals(SuperstructureLayout left, SuperstructureLayout right)
    {
        if (left.Enabled != right.Enabled || left.Layers.Length != right.Layers.Length ||
            left.TowerRoots.Length != right.TowerRoots.Length || left.Legacy != right.Legacy)
            return false;

        for (var layerIndex = 0; layerIndex < left.Layers.Length; layerIndex++)
        {
            var leftLayer = left.Layers[layerIndex];
            var rightLayer = right.Layers[layerIndex];
            if (!string.Equals(leftLayer.Id, rightLayer.Id, StringComparison.Ordinal) ||
                leftLayer.Level != rightLayer.Level ||
                !leftLayer.Modules.SequenceEqual(rightLayer.Modules))
                return false;
        }

        return left.TowerRoots.SequenceEqual(right.TowerRoots);
    }
}

/// <summary>
/// Data-only editable pagoda starter. This type exposes no legacy Center Island or French Hotel
/// selector, so creating a V2 template cannot unlock those hidden legacy styles.
/// </summary>
public static class PagodaTemplate
{
    public const string TemplateId = "pagoda";
    public const int Version = 1;

    public static ImmutableArray<PagodaTierDefinition> Tiers { get; } =
    [
        new(1, 17, 11, 0, 2),
        new(2, 15, 9, 0, 2),
        new(3, 13, 9, 1, 2),
        new(4, 11, 7, 1, 2),
        new(5, 9, 7, 2, 2),
        new(6, 7, 5, 2, 2),
        new(7, 5, 5, 3, 2),
        new(8, 3, 3, 3, 2),
    ];

    public static PagodaTemplateApplication Create(
        int levels,
        MaterialKind material,
        string spanNodeId,
        string rootId = "pagoda-root")
    {
        if (levels is < SuperstructureSettings.MinimumLevels or > SuperstructureSettings.MaximumLevels)
            throw new ArgumentOutOfRangeException(nameof(levels), levels,
                $"Pagoda levels must be between {SuperstructureSettings.MinimumLevels} and " +
                $"{SuperstructureSettings.MaximumLevels}.");
        if (!Enum.IsDefined(material))
            throw new ArgumentOutOfRangeException(nameof(material), material, "The material is unsupported.");
        if (string.IsNullOrWhiteSpace(spanNodeId))
            throw new ArgumentException("A pagoda template needs an arrangement span id.", nameof(spanNodeId));
        if (string.IsNullOrWhiteSpace(rootId))
            throw new ArgumentException("A pagoda template needs a stable root id.", nameof(rootId));

        var layers = Tiers.Take(levels)
            .Select(tier => new SuperstructureLayer($"pagoda-layer-{tier.Level:00}", tier.Level,
            [
                new SuperstructureBoxModule(
                    $"pagoda-box-{tier.Level:00}",
                    DesignMeasure.FromMetres(tier.OffsetAlongMetres),
                    DesignMeasure.Zero,
                    DesignMeasure.FromMetres(tier.LengthMetres),
                    DesignMeasure.FromMetres(tier.WidthMetres),
                    tier.ClearHeightMetres,
                    WallThicknessMetres: 1,
                    RoofThicknessMetres: 1,
                    material),
            ]))
            .ToImmutableArray();

        var layout = new SuperstructureLayout(true, layers,
        [
            new SuperstructureTowerRoot(rootId, spanNodeId, DesignMeasure.Zero),
        ], LegacySuperstructureSettings.None);
        return new PagodaTemplateApplication(layout,
            new SuperstructureTemplateOrigin(TemplateId, Version, "Pagoda"), rootId, spanNodeId);
    }
}
