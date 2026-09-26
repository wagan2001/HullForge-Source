using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Smoothing;
using FtdHullGenerator.Infrastructure;
using static SlopeFillTestSupport;

internal static class EditorRevisionTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        Require(HullEditorSettings.Default.Smoothing == SmoothingMethod.VerticalSlopeFill,
            "The editor must start with proven vertical smoothing.");
        var normalExposure = new FeatureExposurePolicy(experimentalFeaturesEnabled: false);
        var experimentalExposure = new FeatureExposurePolicy(experimentalFeaturesEnabled: true);
        Require(normalExposure.IsAvailable(ProductFeature.ExpandedHullOptions) &&
                !normalExposure.IsAvailable(ProductFeature.Superstructures) &&
                !normalExposure.IsAvailable(ProductFeature.InternalStructures) &&
                !normalExposure.IsAvailable(ProductFeature.ExplicitSlopeRefinement) &&
                !normalExposure.IsAvailable(ProductFeature.InvertedTriangleFill) &&
                experimentalExposure.IsAvailable(ProductFeature.InternalStructures) &&
                !experimentalExposure.IsAvailable(ProductFeature.Superstructures) &&
                !experimentalExposure.IsAvailable(ProductFeature.ExplicitSlopeRefinement) &&
                !experimentalExposure.IsAvailable(ProductFeature.InvertedTriangleFill),
            "The frozen 2.0 exposure matrix drifted; Internal Structures must be the only experimental feature.");
        Require(HullEditorSettings.ValidateFeatureAvailability(
                    HullParameters.Default with { BowStyle = BowStyle.Axe, SternStyle = SternStyle.Fantail,
                        Smoothing = SmoothingMethod.CombinedSlopeFill }, normalExposure) is null,
            "Deferred options were not promoted to the stable interface.");
        Require(HullEditorSettings.ValidateFeatureAvailability(
                    HullParameters.Default with { Smoothing = SmoothingMethod.InvertedTriangleFill },
                    experimentalExposure)?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true,
            "Experimental Features incorrectly exposed unavailable inverted construction.");

        var preferenceRoot = Path.Combine(Path.GetTempPath(), "HullForge-ExperimentalPreference-" + Guid.NewGuid().ToString("N"));
        try
        {
            var preference = new ExperimentalFeaturesPreferenceStore(preferenceRoot);
            Require(!preference.Load() && preference.Save(true, out _) && preference.Load() &&
                    preference.Save(false, out _) && !preference.Load(),
                "Experimental Features did not default off or persist explicitly.");
        }
        finally
        {
            if (Directory.Exists(preferenceRoot))
                Directory.Delete(preferenceRoot, recursive: true);
        }

        var sealParameters = HullShapePreset.FindByName("Seal")!.Apply(HullParameters.Default);
        var sealBase = generator.Generate(sealParameters);
        var seal = generator.Generate(sealParameters with { Smoothing = SmoothingMethod.CombinedSlopeFill });
        VerifyAdditive(sealBase, seal);
        var vertical = new VerticalSlopeFillPass().Apply(sealBase);
        var centerline = vertical.Blocks.Where(b => b.X == 0 && b.Origin == BlockOrigin.Smoothing).ToArray();
        Require(centerline.Length > 0 && centerline.All(seal.Blocks.Contains),
            "Combined Seal fill still drops the independently generated centerline wedges.");
        Require(new CombinedSlopeFillPass().Apply(sealBase with { Blocks = sealBase.Blocks.Reverse().ToArray() })
                .Blocks.SequenceEqual(seal.Blocks), "Centerline arbitration depends on block enumeration order.");

        foreach (var name in new[] { "Dolphin", "Seal" })
        {
            var parameters = HullShapePreset.FindByName(name)!.Apply(HullParameters.Default);
            var hull = generator.Generate(parameters);
            var cells = Cells(hull.Blocks);
            var sternBoundary = hull.MinZ + (int)Math.Round((parameters.Length - 1) *
                parameters.EffectiveShape.Stern.RunLengthPercent / 100d, MidpointRounding.AwayFromZero);
            var bowBoundary = hull.MaxZ - (int)Math.Round((parameters.Length - 1) *
                parameters.EffectiveShape.Bow.EntranceLengthPercent / 100d, MidpointRounding.AwayFromZero);
            foreach (var z in new[] { sternBoundary, bowBoundary })
            {
                var open = (from x in Enumerable.Range(-3, 7) from y in Enumerable.Range(4, 5)
                            where !cells.Contains((x, y, z)) select (x, y)).Count();
                Require(open >= 28, $"{name} has a transverse internal wall at station {z}.");
            }
        }

        var raised = HullParameters.Default with
        {
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(10, 8, 6, 4) },
            Smoothing = SmoothingMethod.HybridSlopeFill,
            HybridFillOffset = 18,
        };
        var raisedHull = generator.Generate(raised);
        Require(raisedHull.Parameters.HybridFillOffset == 18 && raisedHull.OccupiedHeight == 22,
            "Hybrid fill still constrains a raised hull to midship height.");
        var lowered = generator.Generate(raised with { Shape = HullShapeSettings.Default, HybridFillOffset = 100 });
        Require(lowered.Parameters.HybridFillOffset == lowered.OccupiedHeight - 1,
            "Hybrid range did not shrink when the raised profile was removed.");

        if (full)
        {
            var bulbSignatures = new HashSet<string>();
            foreach (var rise in new[] { -50, -25, 0, 25, 50 })
            foreach (var offset in new[] { -40, -20, 0, 20, 40 })
            {
                var hull = generator.Generate(HullParameters.Default with
                {
                    HasBulb = true, Bulb = new BulbSettings(15, 35, offset, rise),
                    Smoothing = SmoothingMethod.CombinedSlopeFill,
                });
                Require(HullGeometryValidator.Validate(hull).Count == 0, "An expanded bulb placement is invalid.");
                if (rise < 0)
                    Require(hull.MinY < 0, "Negative bulb rise was clipped at the keel.");
                if (rise == 0 && offset >= 0)
                    bulbSignatures.Add(string.Join(';', hull.Blocks.Select(b => b.Position)));
            }
            Require(bulbSignatures.Count == 3, "Far-forward bulb adjustments were silently clamped to the same geometry.");
        }
        else
        {
            var hull = generator.Generate(HullParameters.Default with
            {
                HasBulb = true, Bulb = new BulbSettings(15, 35, 0, 0),
                Smoothing = SmoothingMethod.CombinedSlopeFill,
            });
            Require(HullGeometryValidator.Validate(hull).Count == 0, "The representative bulb placement is invalid.");
        }

        var bottom = new ArmorLayout([new ArmorLayer(MaterialKind.Lead), ArmorLayer.Air,
            new ArmorLayer(MaterialKind.HeavyArmor, usePoles: true)]);
        var armoredParameters = HullParameters.Default with
        {
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            BottomArmor = bottom,
            DeckArmor = ArmorLayout.Single(MaterialKind.Wood),
            Beamify = false,
        };
        var armored = generator.Generate(armoredParameters);
        var owners = armored.Blocks.SelectMany(b => b.OccupiedCells.Select(c => (c, b.Material))).ToDictionary(p => p.c, p => p.Material);
        Require(owners[(0, 0, 0)] == MaterialKind.Lead && !owners.ContainsKey((0, 1, 0)) &&
                owners[(0, 2, 0)] == MaterialKind.HeavyArmor && owners[(0, 11, 0)] == MaterialKind.Wood,
            "Deck, side, bottom, or bottom air-gap materials do not follow their independent stacks.");
        Require(owners.Values.Contains(MaterialKind.Metal), "The side armor stack was lost.");
        var beamified = generator.Generate(armoredParameters with { Beamify = true });
        Require(beamified.Blocks.Any(b => b.Shape is BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4),
            "The inner bottom pole layer was not beamified.");
        Require(owners.OrderBy(p => p.Key).SequenceEqual(beamified.Blocks
            .SelectMany(b => b.OccupiedCells.Select(c => new KeyValuePair<(int, int, int), MaterialKind>(c, b.Material)))
            .OrderBy(p => p.Key)), "Beam merging changed separate armor materials.");
        if (full)
        {
            foreach (var smoothing in new[] { SmoothingMethod.None, SmoothingMethod.VerticalSlopeFill,
                         SmoothingMethod.HorizontalSlopeFill, SmoothingMethod.CombinedSlopeFill, SmoothingMethod.HybridSlopeFill })
            foreach (var deck in new[] { true, false })
            {
                var hull = generator.Generate(armoredParameters with
                    { Smoothing = smoothing, DeckArmor = deck ? armoredParameters.DeckArmor : null });
                Require(HullGeometryValidator.Validate(hull).Count == 0, "Separate armor failed a smoothing/deck variant.");
            }

            var random = new Random(731);
            var randomShapes = new HashSet<HullShapeSettings>();
            for (var index = 0; index < 24; index++)
            {
                var choice = HullEditorSettings.RandomizeShape(HullEditorSettings.Default, random);
                var hull = generator.Generate(choice);
                Require(choice.Length == 100 && choice.Width == 21 &&
                        choice.HullArmor.Equals(HullEditorSettings.Default.HullArmor), "Randomization changed dimensions or armor.");
                randomShapes.Add(choice.EffectiveShape);
            }
            Require(randomShapes.Count == 24, "Randomization did not produce distinct editor shapes.");
            var expandedRandom = new Random(1901);
            Require(Enumerable.Range(0, 80)
                    .Select(_ => HullEditorSettings.RandomizeShape(HullEditorSettings.Default, expandedRandom))
                    .Any(choice => choice.BowStyle is BowStyle.Axe or BowStyle.Clipper ||
                                   choice.SternStyle is SternStyle.Cruiser or SternStyle.Fantail),
                "The single-build randomizer never selected the complete stable preset collection.");
        }
        else
        {
            var armoredHull = generator.Generate(armoredParameters with { Smoothing = SmoothingMethod.CombinedSlopeFill });
            Require(HullGeometryValidator.Validate(armoredHull).Count == 0, "The representative separate-armor variant failed.");
            var random = new Random(731);
            var choice = HullEditorSettings.RandomizeShape(HullEditorSettings.Default, random);
            var hull = generator.Generate(choice);
            Require(choice.Length == 100 && choice.Width == 21 &&
                    choice.HullArmor.Equals(HullEditorSettings.Default.HullArmor), "Randomization changed dimensions or armor.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "HullForge-EditorRevision-" + Guid.NewGuid().ToString("N"));
        try
        {
            VerifyExport(seal, catalog, directory, seal.Blocks.Count(b => b.Origin == BlockOrigin.Smoothing));
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            var allowedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "HullForge-EditorRevision-");
            if (resolved.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
        Console.WriteLine(full
            ? $"Editor revision: Seal centerline ({centerline.Length} wedges), open end transitions, raised hybrid, 25 bulb cases, armor stacks, caps, randomizer, and export passed."
            : $"Editor revision: Seal centerline ({centerline.Length} wedges), representative bulb and armor variants, caps, randomizer, and export passed.");
    }
}
