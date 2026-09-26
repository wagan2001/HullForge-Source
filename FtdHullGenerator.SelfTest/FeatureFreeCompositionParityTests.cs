using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using System.Collections.Concurrent;

/// <summary>
/// Independent BASE-03 equivalence matrix for the opt-in feature-free composition seam.
/// Beam and pole runs are expanded through their real anchor/rotation footprints and retain
/// material plus structural-family identity. Native smoothing is compared separately without
/// normalizing away its shape, length, anchor, handedness, or rotation.
/// </summary>
public static class FeatureFreeCompositionParityTests
{
    public static void Run(HullGenerator generator, bool full, int workerCount = 0)
    {
        ArgumentNullException.ThrowIfNull(generator);

        var cases = full ? FullMatrix() : FastMatrix();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = SelfTestProfiles.ResolveWorkerCount(workerCount)
        };
        var compared = 0;
        // Run every case before reporting so one failure does not hide the rest.
        var exceptions = new ConcurrentQueue<Exception>();

        Parallel.ForEach(
            cases,
            options,
            () => new HullGenerator(),
            (testCase, state, localGenerator) =>
            {
                try
                {
                    var legacy = localGenerator.Generate(testCase.Parameters);
                    var context = HullGenerator.CreateContext(testCase.Parameters);
                    var composed = FeatureFreeHullCompositionAdapter.Compose(context);

                    VerifyEquivalent(testCase.Name, legacy, composed);
                    Require(HullGeometryValidator.Validate(composed).Count == 0,
                        $"{testCase.Name}: the composed hull failed geometry validation.");

                    // One context is a revision snapshot and can be materialized repeatedly without
                    // consuming or mutating its captured shell/armor evaluation.
                    var repeated = FeatureFreeHullCompositionAdapter.Compose(context);
                    Require(composed.Blocks.SequenceEqual(repeated.Blocks) &&
                            composed.ConstructionNotes.SequenceEqual(repeated.ConstructionNotes),
                        $"{testCase.Name}: repeated composition from one context was not deterministic.");
                    Interlocked.Increment(ref compared);
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }
                return localGenerator;
            },
            _ => { });

        if (!exceptions.IsEmpty)
            throw new AggregateException(exceptions);

        VerifyFeatureFreeGuards();
        var expected = full ? 96 : 8;
        Require(compared == expected,
            $"The feature-free parity matrix compared {compared} cases instead of {expected}.");
        Console.WriteLine(
            $"Feature-free composition: {compared} legacy/context parity cases passed; " +
            "normalized material/family occupancy, exact native smoothing, flat-bottom, " +
            "V/H/Hybrid, opening, cancellation, and deferred-construction guards passed.");
    }

    private static IReadOnlyList<ParityCase> FastMatrix()
    {
        var standard = Basis("standard", 40, 15, 10);
        var flat = FlatBottomBasis("flat-bottom", 44, 21, 10);
        return
        [
            new("standard-none-beams", standard with { Beamify = true, Smoothing = SmoothingMethod.None }),
            new("standard-v-cubes", standard with { Beamify = false, Smoothing = SmoothingMethod.VerticalSlopeFill }),
            new("standard-h-beams", standard with { Beamify = true, Smoothing = SmoothingMethod.HorizontalSlopeFill }),
            new("standard-hybrid-beams", standard with { Beamify = true, Smoothing = SmoothingMethod.HybridSlopeFill, HybridFillOffset = 3 }),
            new("flat-none-cubes", flat with { Beamify = false, Smoothing = SmoothingMethod.None }),
            new("flat-v-beams", flat with { Beamify = true, Smoothing = SmoothingMethod.VerticalSlopeFill }),
            new("flat-h-cubes", flat with { Beamify = false, Smoothing = SmoothingMethod.HorizontalSlopeFill }),
            new("flat-hybrid-beams", flat with { Beamify = true, Smoothing = SmoothingMethod.HybridSlopeFill, HybridFillOffset = 4 }),
        ];
    }

    private static IReadOnlyList<ParityCase> FullMatrix()
    {
        var profiles = new (string Name, HullParameters Parameters)[]
        {
            ("standard-odd-decked", Basis("standard", 40, 15, 10)),
            ("standard-even-deckless", Basis("even", 42, 16, 10) with
            {
                DeckArmor = null,
                HullArmor = ArmorLayout.Single(MaterialKind.Wood),
            }),
            ("manta-flat", ApplyPreset("Manta", Basis("manta", 44, 25, 10))),
            ("ray-flat-even-deckless", ApplyPreset("Ray", Basis("ray", 46, 26, 10)) with
            {
                DeckArmor = null,
            }),
            ("marlin-raised", ApplyPreset("Marlin", Basis("marlin", 48, 17, 14))),
            ("layered-materials", Basis("layered", 44, 19, 13) with
            {
                HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor]),
                BottomArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.LightweightAlloy]),
                DeckArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.Lead]),
            }),
            ("air-and-poles", Basis("poles", 46, 19, 14) with
            {
                HullArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal),
                    ArmorLayer.Air,
                    new ArmorLayer(MaterialKind.HeavyArmor, ArmorConstruction.Pole),
                ]),
                DeckArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Wood),
                    ArmorLayer.Air,
                    new ArmorLayer(MaterialKind.LightweightAlloy, ArmorConstruction.Pole),
                ]),
            }),
            ("patterned-members-and-superstructure", Basis("patterned", 48, 21, 14) with
            {
                HullArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal, ArmorConstruction.BeamSlopeSpike),
                    new ArmorLayer(MaterialKind.HeavyArmor, ArmorConstruction.BeamSlopeDown),
                ]),
                DeckArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal),
                    new ArmorLayer(MaterialKind.LightweightAlloy, ArmorConstruction.BeamSlopeUp),
                ]),
                Superstructure = new SuperstructureSettings(
                    true, SuperstructureStyle.CenterIsland, 2, MaterialKind.Wood,
                    SuperstructureSmoothingMethod.HorizontalSlopeFill),
            }),
        };
        var smoothing = new[]
        {
            SmoothingMethod.None,
            SmoothingMethod.VerticalSlopeFill,
            SmoothingMethod.HorizontalSlopeFill,
            SmoothingMethod.CrossSectionSlopeFill,
            SmoothingMethod.CombinedSlopeFill,
            SmoothingMethod.HybridSlopeFill,
        };

        var matrix = new List<ParityCase>(profiles.Length * smoothing.Length * 2);
        foreach (var profile in profiles)
        foreach (var method in smoothing)
        foreach (var beamify in new[] { false, true })
        {
            matrix.Add(new ParityCase(
                $"{profile.Name}-{method}-{(beamify ? "beams" : "cubes")}",
                profile.Parameters with
                {
                    Beamify = beamify,
                    Smoothing = method,
                    HybridFillOffset = 3,
                }));
        }

        return matrix;
    }

    private static HullParameters Basis(string _, int length, int width, int height) =>
        HullParameters.Default with
        {
            Length = length,
            Width = width,
            Height = height,
            HasBulb = false,
            Bulb = null,
            Superstructure = null,
        };

    private static HullParameters ApplyPreset(string name, HullParameters parameters) =>
        (HullShapePreset.FindByName(name) ??
         throw new InvalidOperationException($"The required {name} parity preset is missing."))
        .Apply(parameters);

    private static HullParameters FlatBottomBasis(string name, int length, int width, int height)
    {
        var body = BodyShapeSettings.ForStyle(BodyStyle.FlatWide) with
        {
            Style = BodyStyle.Custom,
            SideShape = 0,
            Chine = 0.9,
        };
        var shape = HullShapeSettings.Default with
        {
            Bow = HullShapeSettings.Default.Bow with { EntranceLengthPercent = 10 },
            Stern = HullShapeSettings.Default.Stern with { RunLengthPercent = 10 },
            Body = body,
            Profile = HullProfileSettings.Flat,
        };
        return Basis(name, length, width, height) with
        {
            BowStyle = BowStyle.Pointed,
            SternStyle = SternStyle.Transom,
            Shape = shape,
            BottomArmor = ArmorLayout.Single(MaterialKind.Metal),
        };
    }

    private static void VerifyEquivalent(string name, GeneratedHull legacy, GeneratedHull composed)
    {
        Require(legacy.Parameters.Equals(composed.Parameters),
            $"{name}: effective generated parameters changed through composition.");
        Require((legacy.MinX, legacy.MaxX, legacy.MinY, legacy.MaxY, legacy.MinZ, legacy.MaxZ) ==
                (composed.MinX, composed.MaxX, composed.MinY, composed.MaxY, composed.MinZ, composed.MaxZ),
            $"{name}: occupied bounds changed through composition.");
        Require(legacy.ConstructionNotes.SequenceEqual(composed.ConstructionNotes),
            $"{name}: construction diagnostics changed through composition.");

        var legacyCells = NormalizeCells(legacy, name, "legacy");
        var composedCells = NormalizeCells(composed, name, "composed");
        Require(legacyCells.OrderBy(pair => pair.Key).SequenceEqual(composedCells.OrderBy(pair => pair.Key)),
            $"{name}: normalized cells changed material, structural family, origin, or occupancy.");

        var legacySmoothing = NativeSmoothing(legacy);
        var composedSmoothing = NativeSmoothing(composed);
        Require(legacySmoothing.SequenceEqual(composedSmoothing),
            $"{name}: a native smoothing placement changed shape/length, anchor, rotation, material, or origin.");
    }

    private static Dictionary<CellKey, CellIdentity> NormalizeCells(
        GeneratedHull hull,
        string caseName,
        string route)
    {
        var result = new Dictionary<CellKey, CellIdentity>();
        foreach (var placement in hull.Blocks)
        {
            var info = BlockShapeMetadata.Get(placement.Shape);
            var forward = info.Length == 1
                ? default
                : BlockRotations.GetRotationAxes(placement.Rotation).Forward;
            for (var step = 0; step < info.Length; step++)
            {
                // Expand independently from BlockPlacement.OccupiedCells: SizeInfo for Hull
                // Forge's supported vocabulary is anchor + BLR(local +Z) * step.
                var key = new CellKey(
                    placement.X + forward.X * step,
                    placement.Y + forward.Y * step,
                    placement.Z + forward.Z * step);
                var identity = new CellIdentity(placement.Material, info.Family, placement.Origin);
                Require(result.TryAdd(key, identity),
                    $"{caseName}: the {route} route overlaps occupied cell {key}.");
            }
        }

        return result;
    }

    private static IReadOnlyList<NativePlacementIdentity> NativeSmoothing(GeneratedHull hull) =>
        hull.Blocks
            .Where(block => block.Origin is BlockOrigin.Smoothing or BlockOrigin.SuperstructureSmoothing)
            .Select(block =>
            {
                var info = BlockShapeMetadata.Get(block.Shape);
                return new NativePlacementIdentity(
                    block.Shape, info.Family, info.Length, info.Mirrored,
                    block.Material, block.X, block.Y, block.Z, block.Rotation, block.Origin);
            })
            .OrderBy(item => item.Z)
            .ThenBy(item => item.Y)
            .ThenBy(item => item.X)
            .ThenBy(item => item.Shape)
            .ThenBy(item => item.Rotation)
            .ToArray();

    private static void VerifyFeatureFreeGuards()
    {
        var parameters = HullParameters.Default with { Length = 40, Width = 15, Height = 10 };
        var context = HullGenerator.CreateContext(parameters);
        var deck = context.EnumerateArmor().First(intent => intent.Role == HullCellRole.DeckArmor).Cell;
        var opened = HullGenerator.CreateContext(parameters, DeckOpeningMask.FromCells([deck]));
        RequireThrows<InvalidOperationException>(
            () => FeatureFreeHullCompositionAdapter.Compose(opened),
            "Feature-free composition silently ignored an authorized deck opening.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        RequireThrows<OperationCanceledException>(
            () => FeatureFreeHullCompositionAdapter.Compose(context, cancelled.Token),
            "Feature-free composition ignored a pre-cancelled revision.");

        var inverted = HullGenerator.CreateContext(parameters with
        {
            Smoothing = SmoothingMethod.InvertedTriangleFill,
        });
        RequireThrows<HullGenerationException>(
            () => FeatureFreeHullCompositionAdapter.Compose(inverted),
            "Feature-free composition enabled deferred inverted construction.");
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record ParityCase(string Name, HullParameters Parameters);
    private readonly record struct CellKey(int X, int Y, int Z) : IComparable<CellKey>
    {
        public int CompareTo(CellKey other)
        {
            var comparison = Z.CompareTo(other.Z);
            if (comparison != 0) return comparison;
            comparison = Y.CompareTo(other.Y);
            return comparison != 0 ? comparison : X.CompareTo(other.X);
        }
    }

    private readonly record struct CellIdentity(
        MaterialKind Material,
        StructuralFamily Family,
        BlockOrigin Origin);

    private readonly record struct NativePlacementIdentity(
        BlockShape Shape,
        StructuralFamily Family,
        int Length,
        bool Mirrored,
        MaterialKind Material,
        int X,
        int Y,
        int Z,
        int Rotation,
        BlockOrigin Origin);
}
