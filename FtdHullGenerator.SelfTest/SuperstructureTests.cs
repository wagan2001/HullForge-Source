using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using static SlopeFillTestSupport;

internal static class SuperstructureTests
{
    public static void Run(HullGenerator generator, bool full)
    {
        var baseline = generator.Generate(HullParameters.Default);
        if (full)
        {
            foreach (var style in Enum.GetValues<SuperstructureStyle>())
            foreach (var levels in new[] { 1, 3, 8 })
            {
                var settings = new SuperstructureSettings(true, style, levels, MaterialKind.LightweightAlloy,
                    SuperstructureSmoothingMethod.None);
                var generated = generator.Generate(HullParameters.Default with { Superstructure = settings });
                VerifyStories(generated, settings);
                Require(HullGeometryValidator.Validate(generated).Count == 0,
                    $"{style} level {levels} failed composite validation.");
                Require(generated.Blocks.Where(IsHullBlock).SequenceEqual(baseline.Blocks),
                    $"{style} changed the underlying hull placements.");
                Require(generated.Blocks.Where(block => block.Origin == BlockOrigin.Superstructure)
                        .All(block => block.Material == MaterialKind.LightweightAlloy && block.Shape == BlockShape.Cube),
                    $"{style} did not retain the selected cube construction material.");
            }

            foreach (var style in new[] { SuperstructureStyle.FrenchHotel, SuperstructureStyle.JapanesePagoda })
            {
                var settings = new SuperstructureSettings(true, style, 8, MaterialKind.Metal,
                    SuperstructureSmoothingMethod.HorizontalSlopeFill);
                var filled = generator.Generate(HullParameters.Default with { Superstructure = settings });
                Require(filled.Blocks.Any(block => block.Origin == BlockOrigin.SuperstructureSmoothing),
                    $"{style} horizontal fill added no transition slopes.");
                Require(filled.Blocks.Where(block => block.Origin == BlockOrigin.SuperstructureSmoothing)
                        .All(block => block.Shape == BlockShape.Slope1 && block.Material == MaterialKind.Metal),
                    $"{style} horizontal fill emitted an unexpected shape or material.");
                VerifyStories(filled, settings);
            }

            foreach (var hullSmoothing in new[] { SmoothingMethod.None, SmoothingMethod.VerticalSlopeFill, SmoothingMethod.HorizontalSlopeFill })
            foreach (var structureSmoothing in Enum.GetValues<SuperstructureSmoothingMethod>())
            {
                var plainHull = generator.Generate(HullParameters.Default with { Smoothing = hullSmoothing });
                var combined = generator.Generate(HullParameters.Default with
                {
                    Smoothing = hullSmoothing,
                    Superstructure = new(true, SuperstructureStyle.FrenchHotel, 4, MaterialKind.Metal, structureSmoothing),
                });
                Require(combined.Blocks.Where(IsHullBlock).SequenceEqual(plainHull.Blocks),
                    $"Superstructure {structureSmoothing} changed hull {hullSmoothing} placements.");
            }

            foreach (var material in Enum.GetValues<MaterialKind>())
            {
                var materialHull = generator.Generate(HullParameters.Default with
                {
                    Superstructure = new(true, SuperstructureStyle.CenterIsland, 1, material,
                        SuperstructureSmoothingMethod.None),
                });
                Require(materialHull.Blocks.Where(block => block.Origin == BlockOrigin.Superstructure)
                    .All(block => block.Material == material), $"Superstructure material {material} was not retained.");
            }
        }
        else
        {
            var settings = new SuperstructureSettings(true, SuperstructureStyle.FrenchHotel, 8, MaterialKind.LightweightAlloy,
                SuperstructureSmoothingMethod.HorizontalSlopeFill);
            var generated = generator.Generate(HullParameters.Default with { Superstructure = settings });
            VerifyStories(generated, settings);
            Require(HullGeometryValidator.Validate(generated).Count == 0,
                "The representative superstructure failed composite validation.");
            Require(generated.Blocks.Where(IsHullBlock).SequenceEqual(baseline.Blocks),
                "The representative superstructure changed the underlying hull placements.");
            Require(generated.Blocks.Where(block => block.Origin == BlockOrigin.Superstructure)
                    .All(block => block.Material == MaterialKind.LightweightAlloy && block.Shape == BlockShape.Cube),
                "The representative superstructure did not retain its cube construction material.");
            Require(generated.Blocks.Any(block => block.Origin == BlockOrigin.SuperstructureSmoothing),
                "The representative superstructure added no transition slopes.");
            var plainHull = generator.Generate(HullParameters.Default);
            Require(generated.Blocks.Where(IsHullBlock).SequenceEqual(plainHull.Blocks),
                "Superstructure smoothing changed the underlying hull placements.");
        }

        var even = generator.Generate(HullParameters.Default with
        {
            Width = 24,
            Superstructure = new(true, SuperstructureStyle.CenterIsland, 3, MaterialKind.Metal,
                SuperstructureSmoothingMethod.None),
        });
        Require(HullGeometryValidator.Validate(even).Count == 0, "Even-width superstructure lost symmetry.");

        var raisedShape = HullParameters.Default.EffectiveShape with
        {
            Profile = new HullProfileSettings(5, 4, 3, 2),
        };
        var raised = generator.Generate(HullParameters.Default with
        {
            Shape = raisedShape,
            Superstructure = new(true, SuperstructureStyle.FrenchHotel, 3, MaterialKind.Metal,
                SuperstructureSmoothingMethod.HorizontalSlopeFill),
        });
        Require(HullGeometryValidator.Validate(raised).Count == 0, "Raised-profile superstructure failed validation.");

        var deckless = HullParameters.Default with
        {
            DeckArmor = null,
            Superstructure = new(true, SuperstructureStyle.CenterIsland, 3, MaterialKind.Metal,
                SuperstructureSmoothingMethod.None),
        };
        Require(deckless.Validate().Contains("A superstructure requires a deck."),
            "Deckless superstructure validation was not enforced.");
        Require((HullParameters.Default with
        {
            Superstructure = new(true, SuperstructureStyle.CenterIsland, 9, MaterialKind.Metal,
                SuperstructureSmoothingMethod.None),
        }).Validate().Any(error => error.Contains("between 1 and 8", StringComparison.Ordinal)),
            "Out-of-range superstructure levels were accepted.");
        try
        {
            generator.Generate(HullParameters.Default with
            {
                Width = 7,
                Superstructure = new(true, SuperstructureStyle.CenterIsland, 3, MaterialKind.Metal,
                    SuperstructureSmoothingMethod.None),
            });
            Require(false, "An undersized deck unexpectedly accepted Center Island.");
        }
        catch (HullGenerationException error)
        {
            Require(error.Message.Contains("does not fit", StringComparison.Ordinal),
                "An undersized deck did not return the superstructure fit error.");
        }
        // Superstructures are deferred beyond 2.0: the engine still builds them (proven
        // throughout this test), but the product feature is unavailable in both exposure
        // states and has no normal-surface control.
        Require(!new FeatureExposurePolicy(false).IsAvailable(ProductFeature.Superstructures) &&
                !new FeatureExposurePolicy(true).IsAvailable(ProductFeature.Superstructures),
            "The deferred superstructure feature was exposed as a 2.0 product feature.");
        Require(MainWindow.CreateBlueprintName(HullParameters.Default with
        {
            Superstructure = new(true, SuperstructureStyle.JapanesePagoda, 5, MaterialKind.Metal,
                SuperstructureSmoothingMethod.HorizontalSlopeFill),
        }).EndsWith("_SS_Pagoda_L5_HFill_FA0", StringComparison.Ordinal),
            "Superstructure blueprint suffix is incomplete.");
        // The fore/aft offset is the pagoda's historic station by default, and the slider
        // moves the structure along a hull whose deck leaves the choice of rectangle open.
        Require(SuperstructureSettings.Default.Style == SuperstructureStyle.JapanesePagoda &&
                SuperstructureSettings.Default.ForeAftPercent == 8,
            "The default superstructure must keep the pagoda at its shipped fore/aft station.");
        var centred = SuperstructureZ(generator, 0);
        var forward = SuperstructureZ(generator, 40);
        var aft = SuperstructureZ(generator, -40);
        Require(forward > centred && centred > aft,
            "The fore/aft offset did not move the superstructure toward the bow.");
        Require((HullParameters.Default with
        {
            Superstructure = new(true, SuperstructureStyle.JapanesePagoda, 3, MaterialKind.Metal,
                SuperstructureSmoothingMethod.None, SuperstructureSettings.MaximumForeAftPercent + 1),
        }).Validate().Any(error => error.Contains("fore/aft", StringComparison.Ordinal)),
            "An out-of-range fore/aft offset was accepted.");

        Console.WriteLine(full
            ? "Superstructures: pagoda profile checks, deck/deferred-product gates, and naming passed."
            : "Superstructures: representative pagoda style, smoothing, fore/aft offset, deck/deferred-product gates, and naming passed.");

        static double SuperstructureZ(HullGenerator generator, int foreAftPercent)
        {
            var hull = generator.Generate(HullParameters.Default with
            {
                Superstructure = new(true, SuperstructureStyle.JapanesePagoda, 3, MaterialKind.Metal,
                    SuperstructureSmoothingMethod.None, foreAftPercent),
            });
            var zs = hull.Blocks.Where(block => block.Origin == BlockOrigin.Superstructure)
                .SelectMany(block => block.OccupiedCells).Select(cell => cell.Z).ToArray();
            Require(zs.Length > 0, $"A superstructure at {foreAftPercent} percent generated no blocks.");
            return (zs.Min() + zs.Max()) / 2.0;
        }
    }

    private static void VerifyStories(GeneratedHull hull, SuperstructureSettings settings)
    {
        var baseBlocks = hull.Blocks.Where(block => block.Origin == BlockOrigin.Superstructure).ToArray();
        Require(baseBlocks.Length > 0, $"{settings.Style} generated no structure blocks.");
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var floorY = baseBlocks.Min(block => block.Y) - 1;
        for (var level = 1; level <= settings.Levels; level++)
        {
            var clear = SuperstructureSettings.ClearHeight(settings.Style, level);
            var wallY = floorY + 1;
            var walls = baseBlocks.Where(block => block.Y == wallY).ToArray();
            Require(walls.Length > 0, $"{settings.Style} level {level} has no walls.");
            var minX = walls.Min(block => block.X);
            var maxX = walls.Max(block => block.X);
            var minZ = walls.Min(block => block.Z);
            var maxZ = walls.Max(block => block.Z);
            var minimum = settings.Style switch
            {
                SuperstructureStyle.CenterIsland => (Width: 5, Length: 9),
                SuperstructureStyle.FrenchHotel => (Width: 3, Length: 5),
                _ => (Width: 1, Length: 1),
            };
            Require(maxX - minX - 1 >= minimum.Width && maxZ - minZ - 1 >= minimum.Length,
                $"{settings.Style} level {level} is smaller than its minimum interior.");
            for (var y = wallY; y < wallY + clear; y++)
            for (var x = minX + 1; x < maxX; x++)
            for (var z = minZ + 1; z < maxZ; z++)
                Require(!occupied.Contains((x, y, z)),
                    $"{settings.Style} level {level} does not retain its empty interior.");
            floorY += clear + 1;
        }
    }

    private static bool IsHullBlock(BlockPlacement block) =>
        block.Origin is BlockOrigin.Shell or BlockOrigin.Smoothing;
}
