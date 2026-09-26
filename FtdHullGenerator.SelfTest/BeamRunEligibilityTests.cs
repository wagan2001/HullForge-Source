using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;

/// <summary>
/// The reported defect: the editor's startup hull draws many exterior placements as
/// one-metre cubes inside straight longitudinal rows. The cause was the beam run
/// grouping: the shell classifier tags 45-degree staircase cells as fitted one-metre
/// slopes and corners, the exporter flattens every such candidate to a full cube, and
/// the preview draws it as one, but <c>BeamOptimizer</c> treated the candidate as a
/// run blocker. Each straight exterior run therefore fragmented into one-metre cubes
/// around cells that were never going to ship as anything but a cube.
/// </summary>
/// <remarks>
/// The oracle here is independent of <c>BeamOptimizer</c>'s split formula. It derives
/// maximal longitudinal runs directly from the cube hull's occupied cells and asserts
/// that every eligible run of two or more cells is covered entirely by native multi-cell
/// members, with no isolated one-metre cube left inside it.
/// </remarks>
internal static class BeamRunEligibilityTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        // The exact editor startup path: HullEditorSettings.Default is 100x21x12 Dolphin
        // with one Metal layer on every surface, Vertical slope fill, and Beamify on.
        var editorDefault = HullEditorSettings.Default;
        Require(editorDefault.Smoothing == SmoothingMethod.VerticalSlopeFill && editorDefault.Beamify,
            "The editor startup default no longer selects vertical fill with beamification enabled.");

        VerifyEligibleRunsBecomeNativeMembers(generator, editorDefault, "editor default (vertical fill)");
        VerifyEligibleRunsBecomeNativeMembers(generator,
            editorDefault with { Smoothing = SmoothingMethod.None }, "editor default (no fill)");
        VerifyBeamifyOffRetainsFittedCandidates(generator, editorDefault);
        VerifyUpwardSlopeOrientation();

        // More than one hull size, and odd/even widths. Even widths place the symmetry
        // plane between columns, which is the case a chirality bug would break.
        foreach (var (length, width, height, smoothing, label) in new[]
                 {
                     (40, 15, 11, SmoothingMethod.None, "small odd 40x15x11"),
                     (40, 16, 11, SmoothingMethod.None, "small even 40x16x11"),
                     (30, 9, 6, SmoothingMethod.VerticalSlopeFill, "tiny odd 30x9x6"),
                     (64, 21, 14, SmoothingMethod.HorizontalSlopeFill, "medium odd 64x21x14"),
                     (48, 20, 12, SmoothingMethod.None, "medium even 48x20x12"),
                 })
        {
            VerifyEligibleRunsBecomeNativeMembers(generator,
                editorDefault with { Length = length, Width = width, Height = height, Smoothing = smoothing },
                label);
        }

        // Representative Rounded (default), U, and V sections keep merging correctly.
        foreach (var style in new[] { BodyStyle.Rounded, BodyStyle.U, BodyStyle.V })
        {
            VerifyEligibleRunsBecomeNativeMembers(generator,
                editorDefault with
                {
                    Smoothing = SmoothingMethod.None,
                    Shape = editorDefault.EffectiveShape with { Body = BodyShapeSettings.ForStyle(style) },
                },
                $"body {style}");
        }

        // Layered armor keeps its material identity, and the inner pole layer keeps poles.
        VerifyLayeredArmor(generator, editorDefault);
        VerifyConstructionRuns(generator, editorDefault);
        VerifyOrderIndependence(generator, editorDefault);
        VerifyExportMatchesPreview(generator, catalog, editorDefault);
        VerifyLegacyRegressionScoping(generator);

        Console.WriteLine(
            "Beam run eligibility: exterior longitudinal runs merge into native members with no isolated one-metre cubes.");
    }

    /// <summary>
    /// The core oracle. A run is a maximal contiguous Z sequence, inside one
    /// (X, Y, material, construction, region) column, of placements the exporter writes as a
    /// single full cube. Every run of two or more cells must be covered by native 2-4 m
    /// members whose family matches the requested construction, and no one-metre cube may
    /// be left inside it.
    /// </summary>
    private static void VerifyEligibleRunsBecomeNativeMembers(
        HullGenerator generator,
        HullParameters parameters,
        string label)
    {
        var cube = generator.Generate(parameters with { Beamify = false });
        var beam = generator.Generate(parameters with { Beamify = true });

        Require(HullGeometryValidator.Validate(beam).Count == 0, $"{label}: beamified hull failed validation.");
        Require(Cells(cube.Blocks).SetEquals(Cells(beam.Blocks)),
            $"{label}: beamifying changed the occupied cell set.");
        Require((cube.MinX, cube.MaxX, cube.MinY, cube.MaxY, cube.MinZ, cube.MaxZ) ==
                (beam.MinX, beam.MaxX, beam.MinY, beam.MaxY, beam.MinZ, beam.MaxZ),
            $"{label}: beamifying changed the bounds.");
        Require(NoOverlap(beam.Blocks), $"{label}: beamified footprints overlap.");

        // Every cell keeps the material its cube-hull source had. A run is keyed by
        // material, so a merge must never move a material across a cell boundary.
        var cubeMaterials = CellMaterials(cube.Blocks);
        var beamMaterials = CellMaterials(beam.Blocks);
        Require(cubeMaterials.Count == beamMaterials.Count &&
                cubeMaterials.All(pair => beamMaterials.TryGetValue(pair.Key, out var material) && material == pair.Value),
            $"{label}: beamifying changed a cell's material.");

        var memberByCell = new Dictionary<(int X, int Y, int Z), BlockPlacement>();
        foreach (var block in beam.Blocks)
        foreach (var cell in block.OccupiedCells)
            memberByCell[cell] = block;

        var mirrorSum = cube.MinX + cube.MaxX;
        var eligibleRuns = 0;
        var mergedRuns = 0;
        var isolatedCubes = 0;
        var failures = new List<string>();
        foreach (var column in cube.Blocks.GroupBy(ColumnKey))
        {
            var ordered = column.OrderBy(block => block.Z).ToArray();
            var index = 0;
            while (index < ordered.Length)
            {
                if (!IsExportedAsCube(ordered[index]))
                {
                    index++;
                    continue;
                }

                var end = index + 1;
                while (end < ordered.Length &&
                       IsExportedAsCube(ordered[end]) &&
                       ordered[end].Z == ordered[end - 1].Z + 1)
                {
                    end++;
                }

                if (end - index >= 2)
                {
                    eligibleRuns++;
                    var runMerged = true;
                    foreach (var source in ordered[index..end])
                    {
                        if (!memberByCell.TryGetValue((source.X, source.Y, source.Z), out var member))
                        {
                            runMerged = false;
                            isolatedCubes++;
                        }
                        else if (member.CellLength < 2)
                        {
                            runMerged = false;
                            if (member.Shape == BlockShape.Cube)
                                isolatedCubes++;
                        }
                        else if (BlockShapeMetadata.Get(member.Shape).Family != ExpectedFamily(source, mirrorSum))
                        {
                            runMerged = false;
                            failures.Add(
                                $"{label}: run at ({source.X},{source.Y}) z={source.Z} used {member.Shape} " +
                                $"instead of a {ExpectedFamily(source, mirrorSum)} member.");
                        }
                    }

                    if (runMerged)
                        mergedRuns++;
                    else if (failures.Count == 0)
                        failures.Add(
                            $"{label}: eligible run at ({ordered[index].X},{ordered[index].Y}) " +
                            $"z={ordered[index].Z}..{ordered[end - 1].Z} still contains a one-metre cube.");
                }

                index = end;
            }
        }

        Require(eligibleRuns > 0, $"{label}: no eligible longitudinal run was found to check.");
        Require(mergedRuns == eligibleRuns,
            $"{label}: {eligibleRuns - mergedRuns} of {eligibleRuns} eligible runs were not merged. " +
            string.Join(" ", failures.Take(3)));
        Require(isolatedCubes == 0,
            $"{label}: {isolatedCubes} isolated one-metre cube(s) remain inside otherwise eligible runs.");
    }

    /// <summary>
    /// Beamify off is the debug path that keeps every source cell as a one-metre placement.
    /// It must still carry the fitted shell vocabulary, and the beamified output must have
    /// absorbed those candidates rather than leaving them as one-metre cubes.
    /// </summary>
    private static void VerifyBeamifyOffRetainsFittedCandidates(HullGenerator generator, HullParameters parameters)
    {
        var cube = generator.Generate(parameters with { Beamify = false });
        Require(cube.Blocks.Any(block => block.Origin == BlockOrigin.Shell &&
                                         block.Shape is BlockShape.Slope1 or BlockShape.CornerLeft or BlockShape.CornerRight),
            "Beamify off no longer retains the fitted shell slope/corner vocabulary.");

        var beam = generator.Generate(parameters with { Beamify = true });
        Require(!beam.Blocks.Any(block => block.Origin == BlockOrigin.Shell &&
                                          block.Shape is BlockShape.Slope1 or BlockShape.CornerLeft or BlockShape.CornerRight),
            "A flattened shell candidate survived beamification as its own placement.");

        // The cube hull's fitted candidates and the beam hull's members must cover the same cells.
        Require(Cells(cube.Blocks).SetEquals(Cells(beam.Blocks)),
            "Beamifying a hull with fitted shell candidates changed the occupied cells.");
    }

    /// <summary>
    /// "Up" and "down" are one binary choice, not two independent ones: the two beam slopes
    /// are a lateral mirror pair, so starboard and port must take opposite members of the
    /// pair and only the phase that decides which member is "up" is free. Pin that phase
    /// against the real envelope rather than a comment. An up run keeps its material against
    /// the inboard face and presents its diagonal up and outboard at rotation 0; a down run
    /// takes the same member rolled 180° about the length axis, which turns the cut to the
    /// bottom outboard corner and presents the diagonal down and outboard at rotation 12.
    /// The spike pattern alternates the two by row.
    /// </summary>
    private static void VerifyUpwardSlopeOrientation()
    {
        foreach (var (construction, expectsUp) in new[]
                 {
                     (ArmorConstruction.BeamSlopeUp, true),
                     (ArmorConstruction.BeamSlopeDown, false),
                 })
        {
            var source = (from x in new[] { -3, 3 }
                          from z in Enumerable.Range(0, 6)
                          select new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, x, 0, z, 0)
                          {
                              Construction = construction,
                              ArmorRegion = ArmorRegion.Side,
                          }).ToArray();
            var merged = BeamOptimizer.Merge(source);
            foreach (var (column, outboard) in new[] { (3, 1), (-3, -1) })
            {
                var beam = merged.First(block => block.X == column);
                Require(BlockShapeMetadata.Get(beam.Shape).Family == StructuralFamily.BeamSlope,
                    $"{construction} did not produce a handed beam slope at x={column}.");
                Require(beam.Rotation == (expectsUp ? 0 : 12),
                    $"{construction} at x={column} was placed at rotation {beam.Rotation}.");

                // World faces apply the placement's rotation, so this is the diagonal the
                // preview draws and the game receives, not the part's local envelope.
                var diagonal = StructuralShapeGeometry.WorldFaces(beam)
                    .Single(face => Math.Abs(face.Normal.X) > 0.1f && Math.Abs(face.Normal.Y) > 0.1f);
                Require(Math.Sign(diagonal.Normal.X) == outboard,
                    $"{construction} at x={column} does not present its slope outboard.");
                Require(diagonal.Normal.Y > 0 == expectsUp,
                    $"The {construction} beam slope at x={column} leans the wrong way: its exposed "
                    + $"slope faces {(diagonal.Normal.Y > 0 ? "up" : "down")}.");
            }
        }
    }

    /// <summary>
    /// Every per-layer construction keeps its native family: solid runs become beams,
    /// pole layers become poles, and beam-slope layers become handed beam slopes. A run
    /// must not mix families, and no eligible run may contain a one-metre cube.
    /// </summary>
    private static void VerifyConstructionRuns(HullGenerator generator, HullParameters parameters)
    {
        foreach (var construction in new[]
                 {
                     ArmorConstruction.Solid,
                     ArmorConstruction.BeamSlopeUp,
                     ArmorConstruction.BeamSlopeDown,
                     ArmorConstruction.BeamSlopeSpike,
                 })
        {
            var layout = new ArmorLayout([new ArmorLayer(MaterialKind.Metal, construction)]);
            var constructionParameters = parameters with
            {
                HullArmor = layout,
                BottomArmor = layout,
                DeckArmor = layout,
                Smoothing = SmoothingMethod.None,
            };
            VerifyEligibleRunsBecomeNativeMembers(generator, constructionParameters, construction.ToString());
        }

        var poleParameters = parameters with
        {
            HullArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.HeavyArmor, usePoles: true),
            ]),
            BottomArmor = new ArmorLayout([
                new ArmorLayer(MaterialKind.Metal),
                ArmorLayer.Air,
                new ArmorLayer(MaterialKind.LightweightAlloy, usePoles: true),
            ]),
            Smoothing = SmoothingMethod.None,
        };
        VerifyEligibleRunsBecomeNativeMembers(generator, poleParameters, "pole layers");
    }

    /// <summary>
    /// Preview and export consume the same placement list, so the exported file must expand
    /// back to exactly the cube hull's cells and must contain the native multi-cell members
    /// the preview draws. A catalog that cannot supply a beam expands it into cubes, which is
    /// reported separately; the installed catalog resolves every beam this test emits.
    /// </summary>
    private static void VerifyExportMatchesPreview(
        HullGenerator generator,
        FtdBlockCatalog catalog,
        HullParameters parameters)
    {
        var cube = generator.Generate(parameters with { Beamify = false });
        var beam = generator.Generate(parameters with { Beamify = true });
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"HullForgeBeamRun-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(beam, catalog, tempDirectory, "Beam run eligibility");
            Require(export.BeamCount > 0, "The default beamified export contained no native beams.");
            Require(export.ShapeFallbackCount == 0,
                "The installed catalog unexpectedly fell back for a native beam or smoothing slope.");

            using var document = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var root = document.RootElement;
            var craft = root.GetProperty("Blueprint");
            var shapes = catalog.Blocks.ToDictionary(block => block.Guid, block => block.Shape);
            var items = root.GetProperty("ItemDictionary").EnumerateObject()
                .ToDictionary(entry => int.Parse(entry.Name), entry => Guid.Parse(entry.Value.GetString()!));
            var exportedCells = new List<(int X, int Y, int Z)>();
            var exportedBeams = 0;
            for (var index = 0; index < craft.GetProperty("BLP").GetArrayLength(); index++)
            {
                var shape = shapes[items[craft.GetProperty("BlockIds")[index].GetInt32()]];
                var xyz = craft.GetProperty("BLP")[index].GetString()!.Split(',').Select(int.Parse).ToArray();
                var placement = new BlockPlacement(shape, beam.Parameters.SurfaceMaterial,
                    xyz[0], xyz[1], xyz[2], craft.GetProperty("BLR")[index].GetInt32());
                exportedCells.AddRange(placement.OccupiedCells);
                if (BlockShapeMetadata.Get(shape).Family is StructuralFamily.Box && placement.CellLength > 1)
                    exportedBeams++;
            }

            Require(exportedCells.Count == exportedCells.Distinct().Count(),
                "The beamified export contained overlapping block footprints.");
            Require(exportedCells.ToHashSet().SetEquals(Cells(cube.Blocks)),
                "The beamified export did not describe the same hull as the cube preview.");
            Require(exportedBeams > 0, "The beamified export expanded to no multi-cell box members.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The frozen pre-V2 regression sampler must reproduce its historical beam grouping
    /// exactly, so it keeps treating a flattened shell candidate as a run blocker. The
    /// Shape V2 fix must not leak into that path.
    /// </summary>
    private static void VerifyLegacyRegressionScoping(HullGenerator generator)
    {
        var parameters = HullParameters.Default with
        {
            Length = 96,
            Width = 25,
            Height = 15,
            BowFullness = 0.54,
            SternFullness = 0,
            CrossSectionCurve = 0.35,
            Beamify = true,
            Smoothing = SmoothingMethod.None,
            Shape = null,
        };
        var legacy = generator.GenerateLegacyRegression(parameters);
        Require(HullGeometryValidator.Validate(legacy).Count == 0,
            "The frozen legacy regression hull failed geometry validation after the V2 beam fix.");
        Require(legacy.Blocks.Any(block => block.Origin == BlockOrigin.Shell && block.Shape != BlockShape.Cube),
            "The frozen legacy regression path no longer reproduces its historical fitted shell placements.");
    }

    /// <summary>
    /// A layered hull merges each material independently, so the occupied cells keep the
    /// material their layer assigned and no run crosses a material boundary.
    /// </summary>
    private static void VerifyLayeredArmor(HullGenerator generator, HullParameters parameters)
    {
        var layered = parameters with
        {
            HullArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.HeavyArmor, MaterialKind.Rubber]),
            BottomArmor = new ArmorLayout([MaterialKind.Metal, MaterialKind.LightweightAlloy]),
            DeckArmor = new ArmorLayout([MaterialKind.Wood, MaterialKind.Lead]),
            Smoothing = SmoothingMethod.None,
        };
        VerifyEligibleRunsBecomeNativeMembers(generator, layered, "layered armor");
    }

    /// <summary>
    /// Beam merging is a deterministic repartitioning, so it must not depend on the order
    /// the generator happened to hand it the placement list.
    /// </summary>
    private static void VerifyOrderIndependence(HullGenerator generator, HullParameters parameters)
    {
        var cube = generator.Generate(parameters with { Beamify = false });
        var canonical = BeamOptimizer.Merge(cube.Blocks, mergeFlattenedShellCandidates: true);
        var reversed = BeamOptimizer.Merge(cube.Blocks.Reverse().ToArray(), mergeFlattenedShellCandidates: true);
        Require(canonical.SequenceEqual(reversed),
            "Beam merging depended on the order of the input placement list.");
    }

    private static Dictionary<(int X, int Y, int Z), MaterialKind> CellMaterials(
        IReadOnlyList<BlockPlacement> blocks)
    {
        var result = new Dictionary<(int X, int Y, int Z), MaterialKind>();
        foreach (var block in blocks)
        foreach (var cell in block.OccupiedCells)
            Require(result.TryAdd(cell, block.Material), $"Overlapping materials at {cell}.");
        return result;
    }

    private static bool IsExportedAsCube(BlockPlacement block) => !block.KeepsFittedShape;

    private static StructuralFamily ExpectedFamily(BlockPlacement source, int mirrorSum)
    {
        var construction = source.UsePoles ? ArmorConstruction.Pole : source.Construction;
        return construction switch
        {
            ArmorConstruction.Pole => StructuralFamily.Pole,
            ArmorConstruction.Solid => StructuralFamily.Box,
            // A chiral beam slope cannot mirror onto itself on the odd-width centreline,
            // so the generator deliberately keeps that column solid: a beam, not a slope.
            _ when 2 * source.X == mirrorSum => StructuralFamily.Box,
            _ => StructuralFamily.BeamSlope,
        };
    }

    private static (int X, int Y, MaterialKind Material, ArmorConstruction Construction, ArmorRegion Region)
        ColumnKey(BlockPlacement block)
    {
        var construction = block.UsePoles ? ArmorConstruction.Pole : block.Construction;
        var region = construction == ArmorConstruction.BeamSlopeSpike ? block.ArmorRegion : ArmorRegion.Side;
        return (block.X, block.Y, block.Material, construction, region);
    }

    private static bool NoOverlap(IReadOnlyList<BlockPlacement> blocks)
    {
        var cells = new HashSet<(int X, int Y, int Z)>();
        foreach (var block in blocks)
        foreach (var cell in block.OccupiedCells)
        {
            if (!cells.Add(cell))
                return false;
        }

        return true;
    }

    private static HashSet<(int X, int Y, int Z)> Cells(IEnumerable<BlockPlacement> blocks) =>
        blocks.SelectMany(block => block.OccupiedCells).ToHashSet();

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
