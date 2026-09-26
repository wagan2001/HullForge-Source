using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;

/// <summary>
/// Independently constrained ARM-01/ARM-02 regressions for armor ownership,
/// reserved air, regional fill-level transitions, and native structural contact.
/// </summary>
public static class ArmorSeamReliabilityTests
{
    public static void Run(HullGenerator generator, bool full)
    {
        ArgumentNullException.ThrowIfNull(generator);

        VerifyMinimizedContactDefect(generator);
        VerifyOwnershipAirAndCavity(generator);
        VerifyProfileBulbAndEndCuts(generator);
        VerifyFlatBottomBaseline(generator);
        var cases = VerifyConstructionMatrix(generator, full);
        if (full)
            cases += VerifyLargeCases(generator);

        Console.WriteLine(
            $"Armor seams: minimized ARM001 before/after, ownership, reserved air, cavity, " +
            $"raised profile, bulb/end cuts, flat-bottom, and {cases} construction cases passed.");
    }

    private static void VerifyMinimizedContactDefect(HullGenerator generator)
    {
        var parameters = MinimizedSeamBasis();

        // Beamify=false preserves the independently enumerable source cells and their
        // requested construction metadata. Calling the public packer directly reproduces
        // the pre-G02 output before HullGenerator's post-packing contact correction.
        var cubeSource = generator.Generate(parameters with { Beamify = false });
        var reproduced = BeamOptimizer.Optimize(cubeSource, mergeFlattenedShellCandidates: true);
        var failures = HullGeometryValidator.FindUnsupportedNativeArmorContacts(reproduced);
        var failureKeys = failures.Select(block =>
                (block.Shape, block.X, block.Y, block.Z, block.Rotation, block.ArmorRegion, block.ArmorDepth))
            .OrderBy(item => item.X)
            .ToArray();
        var expected = new[]
        {
            (BlockShape.BeamSlopeMirrored4, -4, 4, -3, 12, ArmorRegion.Bottom, 0),
            (BlockShape.BeamSlopeMirrored4, -4, 5, -3, 12, ArmorRegion.Side, 0),
            (BlockShape.BeamSlope4, 4, 4, -3, 12, ArmorRegion.Bottom, 0),
            (BlockShape.BeamSlope4, 4, 5, -3, 12, ArmorRegion.Side, 0),
        };
        Require(failureKeys.SequenceEqual(expected),
            "The minimized side/bottom seam no longer reproduces the four contactless native members: " +
            string.Join(", ", failureKeys));
        Require(HullGeometryValidator.Validate(reproduced).Any(error =>
                error.StartsWith($"{HullGeometryValidator.NativeArmorContactDiagnosticCode}:", StringComparison.Ordinal)),
            "The validator did not classify the reproduced occupied-cell-only join as ARM001.");

        var repaired = generator.Generate(parameters with { Beamify = true });
        Require(HullGeometryValidator.FindUnsupportedNativeArmorContacts(repaired).Count == 0,
            "The minimized side/bottom seam retained a contactless native armor member.");
        Require(HullGeometryValidator.Validate(repaired).Count == 0,
            "The minimized repaired seam failed final geometry validation.");
        Require(repaired.ConstructionNotes.Count(note => note.StartsWith(
                    $"{HullGeometryValidator.NativeArmorContactDiagnosticCode} contact fallback:", StringComparison.Ordinal)) == 1,
            "The native contact fallback did not leave one precise construction diagnostic.");
        Require(CellMaterials(cubeSource).OrderBy(pair => pair.Key).SequenceEqual(
                    CellMaterials(repaired).OrderBy(pair => pair.Key)),
            "The native contact fallback changed occupied cells or materials.");
        Require(reproduced.Blocks.Count == repaired.Blocks.Count,
            "The native contact fallback changed the placement count.");
        var changed = reproduced.Blocks.Zip(repaired.Blocks)
            .Where(pair => pair.First.Shape != pair.Second.Shape ||
                           pair.First.Rotation != pair.Second.Rotation ||
                           pair.First.Construction != pair.Second.Construction ||
                           pair.First.UsePoles != pair.Second.UsePoles)
            .Select(pair => pair.Second)
            .ToArray();
        var changedKeys = changed.Select(block =>
                (block.Shape, block.X, block.Y, block.Z, block.Rotation, block.ArmorRegion, block.ArmorDepth))
            .OrderBy(item => item.X)
            .ToArray();
        var expectedFallbacks = new[]
        {
            (BlockShape.Beam4, -4, 4, -3, 0, ArmorRegion.Bottom, 0),
            (BlockShape.Beam4, -4, 5, -3, 0, ArmorRegion.Side, 0),
            (BlockShape.Beam4, 4, 4, -3, 0, ArmorRegion.Bottom, 0),
            (BlockShape.Beam4, 4, 5, -3, 0, ArmorRegion.Side, 0),
        };
        Require(changedKeys.SequenceEqual(expectedFallbacks),
            "The minimized repair must replace exactly the four ARM001 members: " +
            string.Join(", ", changedKeys));
        foreach (var fallback in changed)
        {
            Require(HasPositiveAreaContact(repaired, fallback),
                $"The full-volume fallback at {fallback.Position} still has no meaningful native contact.");
        }
    }

    private static void VerifyOwnershipAirAndCavity(HullGenerator generator)
    {
        var parameters = MinimizedSeamBasis();
        var context = HullGenerator.CreateContext(parameters);
        var hull = generator.Generate(parameters);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();

        // Literal expectations for this 12x9x7 input pin hull-wins deck rims,
        // side/bottom ownership, unequal depths, and all three armor stacks.
        RequireIntent(context, new HullCell(-4, 6, 0), HullCellRole.SideArmor,
            MaterialKind.Metal, ArmorRegion.Side, 0, ArmorConstruction.BeamSlopeSpike);
        RequireIntent(context, new HullCell(-3, 6, 0), HullCellRole.DeckArmor,
            MaterialKind.Metal, ArmorRegion.Deck, 0, ArmorConstruction.Solid);
        RequireIntent(context, new HullCell(-4, 4, 0), HullCellRole.BottomArmor,
            MaterialKind.Wood, ArmorRegion.Bottom, 0, ArmorConstruction.BeamSlopeDown);
        RequireIntent(context, new HullCell(-2, 3, 0), HullCellRole.InternalArmor,
            MaterialKind.LightweightAlloy, ArmorRegion.Bottom, 1, ArmorConstruction.BeamSlopeUp);
        RequireIntent(context, new HullCell(-1, 4, 0), HullCellRole.InternalArmor,
            MaterialKind.Lead, ArmorRegion.Deck, 2, ArmorConstruction.Pole);
        RequireIntent(context, new HullCell(-2, 5, 0), HullCellRole.InternalArmor,
            MaterialKind.HeavyArmor, ArmorRegion.Side, 2, ArmorConstruction.Pole);

        var reserved = new[]
        {
            new HullCell(-3, 4, 0),
            new HullCell(-3, 5, 0),
            new HullCell(-1, 5, 0),
            new HullCell(0, 5, 0),
            new HullCell(1, 5, 0),
            new HullCell(3, 4, 0),
            new HullCell(3, 5, 0),
        };
        Require(reserved.All(cell => context.IsReservedArmorAir(cell.X, cell.Y, cell.Z)),
            "The minimized input lost a literal reserved-air ownership cell.");
        Require(reserved.All(cell => !occupied.Contains((cell.X, cell.Y, cell.Z))),
            "Seam correction filled a deliberately reserved armor-air cell.");
        Require(context.IsUsableCavity(0, 3, 0) && !occupied.Contains((0, 3, 0)),
            "Seam correction closed the independently selected usable cavity cell.");
    }

    private static void VerifyProfileBulbAndEndCuts(HullGenerator generator)
    {
        foreach (var width in new[] { 13, 14 })
        foreach (var bulb in new[] { false, true })
        {
            var parameters = RaisedProfileBasis() with
            {
                Width = width,
                HasBulb = bulb,
                Bulb = bulb ? new BulbSettings(20, 30, -2, 1) : null,
            };
            var context = HullGenerator.CreateContext(parameters);
            var hull = generator.Generate(parameters);
            Require(context.DeckYAt(context.MinZ) != context.DeckYAt(0) ||
                    context.FloorYAt(context.MinZ) != context.FloorYAt(0),
                $"The {width} m profile case did not exercise a stern deck/floor step.");
            Require(context.DeckYAt(context.MaxZ) != context.DeckYAt(0) ||
                    context.FloorYAt(context.MaxZ) != context.FloorYAt(0),
                $"The {width} m profile case did not exercise a bow deck/floor step.");
            Require(HullGeometryValidator.FindUnsupportedNativeArmorContacts(hull).Count == 0 &&
                    HullGeometryValidator.Validate(hull).Count == 0,
                $"The {width} m {(bulb ? "bulb" : "plain")} raised/end-cut case retained an invalid seam.");
        }
    }

    private static void VerifyFlatBottomBaseline(HullGenerator generator)
    {
        var hull = generator.Generate(FlatBottomBasis());
        var midship = hull.Blocks.SelectMany(block => block.OccupiedCells)
            .Where(cell => cell.Z == 0).ToHashSet();
        var floorY = midship.Min(cell => cell.Y);
        Require(Enumerable.Range(hull.MinX, hull.OccupiedWidth).All(x => midship.Contains((x, floorY, 0))),
            "The protected Flat/Wide midship floor is no longer one full-width horizontal run.");
        Require(midship.Contains((hull.MinX, floorY + 1, 0)) &&
                midship.Contains((hull.MaxX, floorY + 1, 0)),
            "The protected flat floor no longer meets both side walls directly.");
        Require(hull.Blocks.All(block => block.Origin != BlockOrigin.Smoothing),
            "Smoothing None stopped being a no-op on the protected flat-bottom input.");
        Require(!hull.ConstructionNotes.Any(note => note.StartsWith(
                    HullGeometryValidator.NativeArmorContactDiagnosticCode, StringComparison.Ordinal)) &&
                HullGeometryValidator.Validate(hull).Count == 0,
            "The valid full-volume flat-bottom case was unnecessarily repaired or rejected.");
    }

    private static int VerifyConstructionMatrix(HullGenerator generator, bool full)
    {
        var constructions = full
            ? new[] { ArmorConstruction.Pole, ArmorConstruction.BeamSlopeUp,
                ArmorConstruction.BeamSlopeDown, ArmorConstruction.BeamSlopeSpike }
            : new[] { ArmorConstruction.Pole, ArmorConstruction.BeamSlopeSpike };
        var smoothing = full
            ? new[] { SmoothingMethod.None, SmoothingMethod.VerticalSlopeFill,
                SmoothingMethod.HorizontalSlopeFill, SmoothingMethod.HybridSlopeFill,
                SmoothingMethod.CombinedSlopeFill }
            : new[] { SmoothingMethod.None };
        var widths = full ? new[] { 13, 14 } : new[] { 13 };
        var compared = 0;

        foreach (var construction in constructions)
        foreach (var method in smoothing)
        foreach (var width in widths)
        {
            var basis = RaisedProfileBasis() with
            {
                Width = width,
                HullArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal, construction),
                    new ArmorLayer(MaterialKind.HeavyArmor),
                ]),
                BottomArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Wood, construction),
                    new ArmorLayer(MaterialKind.LightweightAlloy),
                    new ArmorLayer(MaterialKind.Lead),
                ]),
                DeckArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal),
                    new ArmorLayer(MaterialKind.Rubber, construction),
                ]),
                Smoothing = method,
            };
            var cubes = generator.Generate(basis with { Beamify = false });
            var beams = generator.Generate(basis with { Beamify = true });
            Require(CellMaterials(cubes).OrderBy(pair => pair.Key).SequenceEqual(
                        CellMaterials(beams).OrderBy(pair => pair.Key)),
                $"{construction}/{method}/{width}: cube and beam routes changed occupied cells or materials.");
            Require(HullGeometryValidator.FindUnsupportedNativeArmorContacts(beams).Count == 0 &&
                    HullGeometryValidator.Validate(beams).Count == 0,
                $"{construction}/{method}/{width}: beam route retained an unsupported native contact.");
            compared++;
        }
        return compared;
    }

    private static int VerifyLargeCases(HullGenerator generator)
    {
        var cases = new[]
        {
            (Length: 100, Method: SmoothingMethod.VerticalSlopeFill),
            (Length: 200, Method: SmoothingMethod.HorizontalSlopeFill),
            (Length: 300, Method: SmoothingMethod.HybridSlopeFill),
        };
        foreach (var (length, method) in cases)
        {
            var basis = RaisedProfileBasis() with
            {
                Length = length,
                Width = 21,
                Height = 12,
                HullArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal, ArmorConstruction.BeamSlopeSpike),
                    new ArmorLayer(MaterialKind.HeavyArmor),
                ]),
                BottomArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Wood, ArmorConstruction.BeamSlopeDown),
                    new ArmorLayer(MaterialKind.LightweightAlloy),
                    new ArmorLayer(MaterialKind.Lead),
                ]),
                DeckArmor = new ArmorLayout([
                    new ArmorLayer(MaterialKind.Metal),
                    new ArmorLayer(MaterialKind.Rubber, ArmorConstruction.Pole),
                ]),
                Smoothing = method,
            };
            var cubes = generator.Generate(basis with { Beamify = false });
            var beams = generator.Generate(basis with { Beamify = true });
            Require(CellMaterials(cubes).OrderBy(pair => pair.Key).SequenceEqual(
                        CellMaterials(beams).OrderBy(pair => pair.Key)),
                $"{length}m/{method}: large cube and beam routes changed occupied cells or materials.");
            Require(HullGeometryValidator.FindUnsupportedNativeArmorContacts(beams).Count == 0 &&
                    HullGeometryValidator.Validate(beams).Count == 0,
                $"{length}m/{method}: large route retained an unsupported native contact.");
        }
        return cases.Length;
    }

    private static HullParameters MinimizedSeamBasis()
    {
        var side = new ArmorLayout([
            new ArmorLayer(MaterialKind.Metal, ArmorConstruction.BeamSlopeSpike),
            ArmorLayer.Air,
            new ArmorLayer(MaterialKind.HeavyArmor, ArmorConstruction.Pole),
        ]);
        var bottom = new ArmorLayout([
            new ArmorLayer(MaterialKind.Wood, ArmorConstruction.BeamSlopeDown),
            new ArmorLayer(MaterialKind.LightweightAlloy, ArmorConstruction.BeamSlopeUp),
        ]);
        var deck = new ArmorLayout([
            new ArmorLayer(MaterialKind.Metal),
            ArmorLayer.Air,
            new ArmorLayer(MaterialKind.Lead, ArmorConstruction.Pole),
        ]);
        return HullParameters.Default with
        {
            Length = 12,
            Width = 9,
            Height = 7,
            HullArmor = side,
            BottomArmor = bottom,
            DeckArmor = deck,
            Beamify = true,
            Smoothing = SmoothingMethod.None,
            HasBulb = false,
            Bulb = null,
        };
    }

    private static HullParameters RaisedProfileBasis()
    {
        var basis = MinimizedSeamBasis() with { Length = 24, Width = 13, Height = 10 };
        return basis with
        {
            BowStyle = BowStyle.Raked,
            SternStyle = SternStyle.Counter,
            Shape = basis.EffectiveShape with
            {
                Bow = basis.EffectiveShape.Bow with { EntranceLengthPercent = 30 },
                Stern = basis.EffectiveShape.Stern with { RunLengthPercent = 30 },
                Profile = new HullProfileSettings(3, 2, 2, 1),
            },
        };
    }

    private static HullParameters FlatBottomBasis()
    {
        var basis = HullParameters.Default with
        {
            Length = 24,
            Width = 13,
            Height = 8,
            BowStyle = BowStyle.Blunt,
            SternStyle = SternStyle.Square,
            HullArmor = ArmorLayout.Single(MaterialKind.Metal),
            BottomArmor = ArmorLayout.Single(MaterialKind.Wood),
            DeckArmor = ArmorLayout.Single(MaterialKind.Metal),
            Beamify = true,
            Smoothing = SmoothingMethod.None,
            HasBulb = false,
            Bulb = null,
        };
        return basis with
        {
            Shape = basis.EffectiveShape with
            {
                Bow = basis.EffectiveShape.Bow with { EntranceLengthPercent = 10 },
                Stern = basis.EffectiveShape.Stern with { RunLengthPercent = 10 },
                Body = BodyShapeSettings.ForStyle(BodyStyle.FlatWide),
                Profile = HullProfileSettings.Flat,
            },
        };
    }

    private static Dictionary<(int X, int Y, int Z), MaterialKind> CellMaterials(GeneratedHull hull)
    {
        var result = new Dictionary<(int X, int Y, int Z), MaterialKind>();
        foreach (var block in hull.Blocks)
        foreach (var cell in block.OccupiedCells)
            Require(result.TryAdd(cell, block.Material), $"Duplicate occupied cell {cell} in test input.");
        return result;
    }

    private static bool HasPositiveAreaContact(GeneratedHull hull, BlockPlacement target)
    {
        var targetCells = target.OccupiedCells.ToHashSet();
        return hull.Blocks.Where(block => block != target).Any(block =>
            block.OccupiedCells.Any(cell => Neighbours(cell).Any(targetCells.Contains)) &&
            StructuralShapeGeometry.TouchingArea(target, block) > 1e-5);

        static IEnumerable<(int X, int Y, int Z)> Neighbours((int X, int Y, int Z) cell)
        {
            yield return (cell.X - 1, cell.Y, cell.Z);
            yield return (cell.X + 1, cell.Y, cell.Z);
            yield return (cell.X, cell.Y - 1, cell.Z);
            yield return (cell.X, cell.Y + 1, cell.Z);
            yield return (cell.X, cell.Y, cell.Z - 1);
            yield return (cell.X, cell.Y, cell.Z + 1);
        }
    }

    private static void RequireIntent(
        HullBuildContext context,
        HullCell cell,
        HullCellRole role,
        MaterialKind material,
        ArmorRegion region,
        int depth,
        ArmorConstruction construction)
    {
        Require(context.TryGetArmor(cell.X, cell.Y, cell.Z, out var intent),
            $"Expected armor intent is missing at {cell}.");
        Require(intent.Role == role && intent.Material == material && intent.Region == region &&
                intent.Depth == depth && intent.Construction == construction,
            $"Armor ownership at {cell} was {intent.Role}/{intent.Material}/{intent.Region}/" +
            $"d{intent.Depth}/{intent.Construction}, expected {role}/{material}/{region}/d{depth}/{construction}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
