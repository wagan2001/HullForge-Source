using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Infrastructure;

/// <summary>
/// Bounded native catalog-audit harness for the sixteen fixed "Alternate Naval Sketchbook"
/// Shape V2 candidates. Every recorded number comes from the real current C# path
/// (<see cref="HullGenerator"/>, <see cref="HullGeometryValidator"/>,
/// <see cref="SurfaceQualityGeometry"/>, <see cref="SurfaceSectionQuery"/>,
/// <see cref="ShipGenerationService"/> and <see cref="FtdBlockCatalog"/>). No Python study is
/// an oracle here.
/// </summary>
internal static class SketchbookNativeAudit
{
    public const string JsonFileName = "sketchbook-native-audit.json";
    public const string MarkdownRelativePath = "artifacts/sketchbook-native-audit.md";

    private static readonly SmoothingMethod[] ComparisonMethods =
    [
        SmoothingMethod.None,
        SmoothingMethod.VerticalSlopeFill,
        SmoothingMethod.HorizontalSlopeFill,
        SmoothingMethod.HybridSlopeFill,
        SmoothingMethod.CombinedSlopeFill,
    ];

    private static readonly (string Left, string Right)[] NamedPairs =
    [
        ("01", "12"),
        ("03", "16"),
        ("06", "10"),
        ("10", "15"),
        ("05", "09"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // ---------------------------------------------------------------------------------
    // Entry points
    // ---------------------------------------------------------------------------------

    public static string FixturePath()
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(typeof(SketchbookNativeAudit).Assembly.Location)!, "Fixtures");
        return Path.Combine(directory, JsonFileName);
    }

    public static string Serialize(SketchbookEvidence evidence) =>
        JsonSerializer.Serialize(evidence, JsonOptions);

    internal static string SerializeValue<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static SketchbookEvidence Deserialize(string json) =>
        JsonSerializer.Deserialize<SketchbookEvidence>(json, JsonOptions)
        ?? throw new InvalidOperationException("The sketchbook native-audit fixture was empty.");

    /// <summary>Writes the deterministic JSON fixture and the human-readable evidence doc.</summary>
    public static (string JsonPath, string MarkdownPath) WriteEvidence(
        HullGenerator generator, FtdBlockCatalog catalog)
    {
        var repoRoot = FindRepositoryRoot();
        var evidence = Build(generator, catalog, includeFull: true);
        var jsonPath = Path.Combine(repoRoot, "FtdHullGenerator.SelfTest", "Fixtures", JsonFileName);
        var markdownPath = Path.Combine(repoRoot, MarkdownRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        File.WriteAllText(jsonPath, Serialize(evidence), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(markdownPath, RenderMarkdown(evidence), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (jsonPath, markdownPath);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Repository fixture root not found.");
    }

    // ---------------------------------------------------------------------------------
    // Build
    // ---------------------------------------------------------------------------------

    public static SketchbookEvidence Build(HullGenerator generator, FtdBlockCatalog catalog, bool includeFull)
    {
        SketchbookCandidates.ValidateMapping();
        var sizes = includeFull
            ? new[] { SketchbookSizeKind.Suggested, SketchbookSizeKind.Minimum }
            : new[] { SketchbookSizeKind.Suggested };

        var rows = new List<SketchbookBaseRow>();
        var hulls = new Dictionary<(string Id, SketchbookSizeKind Kind), GeneratedHull?>();
        foreach (var candidate in SketchbookCandidates.All)
        foreach (var kind in sizes)
        {
            var measurement = MeasureBase(candidate, kind, generator, includeFull: includeFull);
            rows.Add(measurement.Row);
            hulls[(candidate.Id, kind)] = measurement.Hull;
        }

        var smoothing = new List<SketchbookSmoothingRow>();
        var catalogRows = new List<SketchbookCatalogRow>();
        var nearest = new List<SketchbookNearestRow>();
        if (includeFull)
        {
            foreach (var candidate in SketchbookCandidates.All)
            {
                smoothing.AddRange(MeasureSmoothing(candidate, generator));
                foreach (var kind in sizes)
                    catalogRows.Add(MeasureCatalog(candidate, kind, generator, catalog));
                nearest.Add(MeasureNearest(candidate, hulls));
            }
        }

        var probes = MeasureProbes(generator, includeFull, hulls);
        var collapsed = DetectCollapsedPairs(rows);
        var findings = new List<string>();
        foreach (var probe in probes.TerminalColumns)
            if (probe.ZeroLength)
                findings.Add(
                    $"Candidate {probe.Id} ({probe.Size}): the bow-most one-cell-wide terminal run is zero metres.");
        if (!probes.Bulb.CentrelineContiguous && probes.Bulb.Note is { } bulbNote)
            findings.Add($"Candidate 13 bulb: {bulbNote}");
        if (probes.DeckRuler.Contaminated && probes.DeckRuler.Note is { } deckNote)
            findings.Add($"Candidate 13 deck-ruler contamination: {deckNote}");

        var pairs = includeFull
            ? BuildPairTable(hulls)
            : [];
        var statuses = BuildStatuses(rows, catalogRows, collapsed);
        foreach (var status in statuses.Where(status => status.Status == "BLOCKED"))
            findings.Add($"Candidate {status.Id} is BLOCKED: {status.Reason}");

        return new SketchbookEvidence(
            SketchbookCandidates.SchemaId,
            SketchbookCandidates.SchemaVersion,
            SketchbookCandidates.DefinitionsSha256(),
            catalog.GameVersion,
            SketchbookCandidates.All.Select(DefinitionOf).ToArray(),
            rows,
            smoothing,
            catalogRows,
            probes,
            pairs,
            nearest,
            collapsed,
            findings,
            statuses);
    }

    private static SketchbookCandidateDefinition DefinitionOf(SketchbookCandidate candidate) =>
        new(
            candidate.Id,
            candidate.Name,
            candidate.SuggestedLength, candidate.SuggestedWidth, candidate.SuggestedHeight,
            candidate.MinimumLength, candidate.MinimumWidth, candidate.MinimumHeight,
            candidate.BowStyle.ToString(), candidate.SternStyle.ToString(),
            new SketchbookBow(candidate.Bow.Fullness, candidate.Bow.Flare, candidate.Bow.EntranceLengthPercent),
            new SketchbookBody(
                candidate.Body.Style.ToString(), candidate.Body.Fullness, candidate.Body.SideShape,
                candidate.Body.Chine, candidate.Body.FlatBottom),
            new SketchbookStern(candidate.Stern.Fullness, candidate.Stern.SideShape, candidate.Stern.RunLengthPercent),
            ProfileOf(candidate.BaselineProfile),
            ProfileOf(candidate.ExpectedMinimumProfile),
            ProfileOf(candidate.MinimumSize.AppliedProfile),
            candidate.HasBulb,
            BulbOf(candidate.Bulb),
            candidate.Recommended);

    private static SketchbookProfile ProfileOf(HullProfileSettings profile) =>
        new(profile.BowDeckRise, profile.SternDeckRise, profile.BowKeelRise, profile.SternKeelRise);

    private static SketchbookBulb BulbOf(BulbSettings bulb) =>
        new(bulb.LengthPercent, bulb.WidthPercent, bulb.ForeAftPercent, bulb.RisePercent);

    // ---------------------------------------------------------------------------------
    // Base row
    // ---------------------------------------------------------------------------------

    private sealed record BaseMeasurement(GeneratedHull? Hull, SketchbookBaseRow Row);

    private static BaseMeasurement MeasureBase(
        SketchbookCandidate candidate, SketchbookSizeKind kind, HullGenerator generator, bool includeFull)
    {
        var size = candidate.Size(kind);
        var sizeLabel = kind == SketchbookSizeKind.Suggested ? "suggested" : "minimum";
        var parameters = SketchbookCandidates.ParametersFor(candidate, kind);
        var reproducer = Reproducer(candidate, sizeLabel, parameters);
        try
        {
            var hull = generator.Generate(parameters);
            var repeat = generator.Generate(parameters);
            var baseHash = HashNormalizedBase(hull.Blocks);
            var repeatBaseHash = HashNormalizedBase(repeat.Blocks);
            var nativeHash = HashNativePlacements(hull.Blocks);
            var repeatNativeHash = HashNativePlacements(repeat.Blocks);
            var deterministic = baseHash == repeatBaseHash && nativeHash == repeatNativeHash;

            var errors = HullGeometryValidator.Validate(hull);
            var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToArray();
            var shellComponents = CellComponents.Build(occupied).Count;

            var context = HullGenerator.CreateContext(parameters);
            var solid = new List<(int X, int Y, int Z)>();
            var roleCounts = new Dictionary<HullCellRole, int>();
            foreach (var role in Enum.GetValues<HullCellRole>())
                roleCounts[role] = 0;
            var cavity = new List<(int X, int Y, int Z)>();
            for (var x = context.MinX; x <= context.MaxX; x++)
            for (var y = context.MinY; y <= context.MaxY; y++)
            for (var z = context.MinZ; z <= context.MaxZ; z++)
            {
                var role = context.RoleAt(x, y, z);
                roleCounts[role]++;
                if (role != HullCellRole.Outside)
                    solid.Add((x, y, z));
                if (role == HullCellRole.Cavity)
                    cavity.Add((x, y, z));
            }

            var solidComponents = CellComponents.Build(solid).Count;
            var solidSet = solid.ToHashSet();
            var core = solid.Where(cell =>
                solidSet.Contains((cell.X - 1, cell.Y, cell.Z)) &&
                solidSet.Contains((cell.X + 1, cell.Y, cell.Z)) &&
                solidSet.Contains((cell.X, cell.Y - 1, cell.Z)) &&
                solidSet.Contains((cell.X, cell.Y + 1, cell.Z)) &&
                solidSet.Contains((cell.X, cell.Y, cell.Z - 1)) &&
                solidSet.Contains((cell.X, cell.Y, cell.Z + 1))).ToList();
            var coreComponents = core.Count == 0 ? 0 : CellComponents.Build(core).Count;

            // The export-effective coverage engine and the analytic section query are the
            // expensive part of the audit. They stay in the full profile so the everyday fast
            // gate does not pay for them; the committed fixture still records them and the full
            // profile verifies every value.
            SketchbookSurfaceRow? surface = null;
            SketchbookSectionRow? section = null;
            if (includeFull)
            {
                var geometry = new SurfaceQualityGeometry(hull);
                geometry.MeasureExposedSurface(out var totals);
                surface = new SketchbookSurfaceRow(
                    totals.Exposed, totals.Step, geometry.Coverage.Fragments.Count, geometry.Coverage.Notes.Count);
                section = MeasureSection(hull, geometry);
            }

            var status = "OK";
            string? statusReason = null;
            if (errors.Count > 0)
            {
                status = "BLOCKED";
                statusReason = $"HullGeometryValidator reported {errors.Count} error(s): {string.Join(" | ", errors)}";
            }
            else if (shellComponents != 1)
            {
                status = "BLOCKED";
                statusReason = $"The generated shell is not face-connected ({shellComponents} components).";
            }
            else if (solidComponents != 1)
            {
                status = "BLOCKED";
                statusReason = $"The analytic solid is not face-connected ({solidComponents} components).";
            }
            else if (cavity.Count == 0)
            {
                status = "BLOCKED";
                statusReason = "The hull has no usable cavity cells.";
            }

            var row = new SketchbookBaseRow(
                candidate.Id, sizeLabel, candidate.Name,
                size.Length, size.Width, size.Height,
                ProfileOf(size.AppliedProfile),
                candidate.HasBulb, BulbOf(candidate.Bulb), candidate.Recommended,
                hull.MinX, hull.MaxX, hull.MinY, hull.MaxY, hull.MinZ, hull.MaxZ,
                hull.OccupiedLength, hull.OccupiedWidth, hull.OccupiedHeight,
                hull.OccupiedCellCount, hull.BlockCount,
                hull.ConstructionNotes.ToArray(),
                baseHash, nativeHash, deterministic,
                errors.Count, errors.ToArray(),
                shellComponents, solidComponents, coreComponents, core.Count,
                cavity.Count, cavity.Count == 0 ? 0 : CellComponents.Build(cavity).Count,
                context.Diagnostics.Count,
                roleCounts.OrderBy(pair => pair.Key)
                    .Select(pair => new SketchbookRoleCount(pair.Key.ToString(), pair.Value)).ToArray(),
                surface, section,
                reproducer, status, statusReason, null);
            return new BaseMeasurement(hull, row);
        }
        catch (Exception exception)
        {
            var message = exception.GetBaseException().Message;
            var row = new SketchbookBaseRow(
                candidate.Id, sizeLabel, candidate.Name,
                size.Length, size.Width, size.Height,
                ProfileOf(size.AppliedProfile),
                candidate.HasBulb, BulbOf(candidate.Bulb), candidate.Recommended,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                [], "", "", false,
                0, [], 0, 0, 0, 0, 0, 0, 0,
                Enum.GetValues<HullCellRole>().Select(role => new SketchbookRoleCount(role.ToString(), 0)).ToArray(),
                null, null,
                reproducer, "BLOCKED", $"{exception.GetType().Name}: {message}",
                BlockedExplanations(candidate, sizeLabel, message));
            return new BaseMeasurement(null, row);
        }
    }

    private static IReadOnlyList<string> BlockedExplanations(
        SketchbookCandidate candidate, string sizeLabel, string message)
    {
        var explanations = new List<string>
        {
            "The fixed candidate input was preserved unchanged; nothing was retuned or repaired.",
            "The failure may be an intrinsic property of this Shape V2 bundle at this size.",
        };
        if (message.Contains("cavity", StringComparison.OrdinalIgnoreCase))
            explanations.Add("The profile may not retain a two-row cavity at this midship height.");
        if (message.Contains("connected", StringComparison.OrdinalIgnoreCase))
            explanations.Add("The regional sampler may not connect the shell inside this end-style combination.");
        if (message.Contains("rise", StringComparison.OrdinalIgnoreCase))
            explanations.Add("A profile rise may exceed the Height-2 bound for this size.");
        if (candidate.HasBulb)
            explanations.Add("The bulb may project outside the solid at this size.");
        return explanations;
    }

    private static string Reproducer(
        SketchbookCandidate candidate, string sizeLabel, HullParameters parameters)
    {
        var shape = parameters.Shape!;
        return $"candidate {candidate.Id} size {sizeLabel} " +
               $"{parameters.Length}x{parameters.Width}x{parameters.Height} " +
               $"bow({shape.Bow.Fullness:R},{shape.Bow.Flare:R},{shape.Bow.EntranceLengthPercent}) " +
               $"body({shape.Body.Style},{shape.Body.Fullness:R},{shape.Body.SideShape:R},{shape.Body.Chine:R},{shape.Body.FlatBottom:R}) " +
               $"stern({shape.Stern.Fullness:R},{shape.Stern.SideShape:R},{shape.Stern.RunLengthPercent}) " +
               $"profile({shape.Profile.BowDeckRise},{shape.Profile.SternDeckRise},{shape.Profile.BowKeelRise},{shape.Profile.SternKeelRise}) " +
               $"bulb({parameters.HasBulb},{parameters.Bulb?.ToString() ?? "(null)"}) beamify={parameters.Beamify} smoothing={parameters.Smoothing} " +
               $"bowStyle={parameters.BowStyle} sternStyle={parameters.SternStyle}";
    }

    private static SketchbookSectionRow MeasureSection(GeneratedHull hull, SurfaceQualityGeometry geometry)
    {
        var breadth = MeasureBreadth(hull, geometry);
        // Cut through the station's own cell centre. Cutting on stationZ + 0.5 would put the plane
        // exactly on a shared cell face, which SurfaceSectionQuery documents as the most degenerate
        // choice: every cube face there is coplanar and the stitched contour is ambiguous. The
        // cell-centre plane crosses the side faces cleanly, so the contour is real section evidence.
        var cut = new SurfaceSectionQuery(geometry).Cut(SectionAxis.Z, breadth.StationZ);
        return new SketchbookSectionRow(
            breadth.StationZ, breadth.StationZ,
            cut.Contours.Count, cut.Contours.Count(contour => contour.IsClosed),
            cut.OpenEndpoints, cut.BranchEndpoints,
            cut.CoplanarFaces, cut.DegenerateTouches,
            breadth.MaxHalfBreadth, breadth.MaxHalfBreadthY, breadth.TopY,
            breadth.TumblehomeDelta, cut.Contours.Any(contour => contour.IsClosed));
    }

    private sealed record BreadthStation(
        int StationZ, double MaxHalfBreadth, int MaxHalfBreadthY, int TopY,
        double HalfBreadthAtTopRow, double TumblehomeDelta);

    private static BreadthStation MeasureBreadth(GeneratedHull hull, SurfaceQualityGeometry geometry)
    {
        var centerX = (hull.MinX + hull.MaxX) / 2.0;
        var rowsByZ = new SortedDictionary<int, List<(int Y, double HalfBreadth)>>();
        foreach (var (row, maxX) in geometry.MaxXByRow)
        {
            var minX = geometry.MinXByRow[row];
            var halfBreadth = Math.Max(maxX - centerX, centerX - minX);
            if (!rowsByZ.TryGetValue(row.Z, out var list))
            {
                list = [];
                rowsByZ[row.Z] = list;
            }

            list.Add((row.Y, halfBreadth));
        }

        var bestStation = 0;
        var bestBreadth = double.NegativeInfinity;
        foreach (var (station, rows) in rowsByZ)
        {
            var stationBreadth = rows.Max(row => row.HalfBreadth);
            if (stationBreadth > bestBreadth)
            {
                bestBreadth = stationBreadth;
                bestStation = station;
            }
        }

        if (bestBreadth == double.NegativeInfinity)
            return new BreadthStation(0, 0, 0, 0, 0, 0);

        var chosen = rowsByZ[bestStation];
        var maxHalfBreadth = chosen.Max(row => row.HalfBreadth);
        var maxY = chosen.Where(row => row.HalfBreadth == maxHalfBreadth).Min(row => row.Y);
        var topY = chosen.Max(row => row.Y);
        var topHalfBreadth = chosen.Single(row => row.Y == topY).HalfBreadth;
        return new BreadthStation(bestStation, maxHalfBreadth, maxY, topY, topHalfBreadth,
            maxHalfBreadth - topHalfBreadth);
    }

    private static Dictionary<int, (int MinX, int MaxX)> StationExtents(SurfaceQualityGeometry geometry)
    {
        var extents = new Dictionary<int, (int MinX, int MaxX)>();
        foreach (var (row, maxX) in geometry.MaxXByRow)
        {
            var minX = geometry.MinXByRow[row];
            if (!extents.TryGetValue(row.Z, out var extent))
                extents[row.Z] = (minX, maxX);
            else
                extents[row.Z] = (Math.Min(extent.MinX, minX), Math.Max(extent.MaxX, maxX));
        }

        return extents;
    }

    // ---------------------------------------------------------------------------------
    // Smoothing
    // ---------------------------------------------------------------------------------

    private static IReadOnlyList<SketchbookSmoothingRow> MeasureSmoothing(
        SketchbookCandidate candidate, HullGenerator generator)
    {
        var parameters = SketchbookCandidates.ParametersFor(candidate, SketchbookSizeKind.Suggested);
        var baseline = generator.Generate(parameters with { Smoothing = SmoothingMethod.None });
        var baselineCells = baseline.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var baselineBaseHash = HashNormalizedBase(
            baseline.Blocks.Where(block => block.Origin != BlockOrigin.Smoothing));
        var rows = new List<SketchbookSmoothingRow>();
        foreach (var method in ComparisonMethods)
        {
            try
            {
                var hull = generator.Generate(parameters with { Smoothing = method });
                var errors = HullGeometryValidator.Validate(hull);
                var smoothing = hull.Blocks.Where(block => block.Origin == BlockOrigin.Smoothing).ToArray();
                var boundsUnchanged =
                    (hull.MinX, hull.MaxX, hull.MinY, hull.MaxY, hull.MinZ, hull.MaxZ) ==
                    (baseline.MinX, baseline.MaxX, baseline.MinY, baseline.MaxY, baseline.MinZ, baseline.MaxZ);
                var baseUnchanged = HashNormalizedBase(
                    hull.Blocks.Where(block => block.Origin != BlockOrigin.Smoothing)) == baselineBaseHash;
                var additive = smoothing.SelectMany(block => block.OccupiedCells)
                    .All(cell => !baselineCells.Contains(cell));
                rows.Add(new SketchbookSmoothingRow(
                    candidate.Id, method.ToString(),
                    errors.Count == 0, errors.Count,
                    boundsUnchanged, baseUnchanged,
                    HashNativePlacements(smoothing), smoothing.Length,
                    hull.OccupiedCellCount, additive,
                    IsRecommended(candidate, method)));
            }
            catch (Exception exception)
            {
                rows.Add(new SketchbookSmoothingRow(
                    candidate.Id, method.ToString(),
                    false, 0, false, false, "", 0, 0, false,
                    IsRecommended(candidate, method))
                {
                    Status = "BLOCKED",
                    StatusReason = $"{exception.GetType().Name}: {exception.GetBaseException().Message}",
                });
            }
        }

        return rows;
    }

    private static bool IsRecommended(SketchbookCandidate candidate, SmoothingMethod method) =>
        candidate.Recommended switch
        {
            "None" => method == SmoothingMethod.None,
            "Vertical" => method == SmoothingMethod.VerticalSlopeFill,
            "Horizontal" => method == SmoothingMethod.HorizontalSlopeFill,
            _ => false,
        };

    // ---------------------------------------------------------------------------------
    // Catalog / export
    // ---------------------------------------------------------------------------------

    private static SketchbookCatalogRow MeasureCatalog(
        SketchbookCandidate candidate, SketchbookSizeKind kind,
        HullGenerator generator, FtdBlockCatalog catalog)
    {
        var sizeLabel = kind == SketchbookSizeKind.Suggested ? "suggested" : "minimum";
        var parameters = SketchbookCandidates.ParametersFor(candidate, kind);
        try
        {
            var document = ShipDocument.FromLegacyParameters(
                parameters, candidate.Name, documentId: $"sketchbook-{candidate.Id}-{sizeLabel}");
            var result = new ShipGenerationService().Generate(document, 1, catalog);
            if (result.Snapshot is null)
            {
                return new SketchbookCatalogRow(
                    candidate.Id, sizeLabel, false, false, 0, null, null, 0,
                    result.Diagnostics.Select(diagnostic => diagnostic.ToString()).ToArray(),
                    "BLOCKED", "ShipGenerationService returned no snapshot.");
            }

            var resolved = result.Snapshot.Hull;
            var plain = generator.Generate(parameters);
            var parity = HashNormalizedBase(resolved.Blocks) == HashNormalizedBase(plain.Blocks);
            var pairs = resolved.Blocks
                .Select(block => (block.Material, Shape: block.KeepsFittedShape ? block.Shape : BlockShape.Cube))
                .Distinct()
                .ToArray();
            var fallbackPairs = pairs.Count(pair => catalog.Resolve(pair.Material, pair.Shape).IsFallback);
            var status = "OK";
            string? statusReason = null;
            if (!parity)
            {
                status = "BLOCKED";
                statusReason = "The catalog-resolved snapshot does not match the plain generator occupancy.";
            }
            else if (resolved.CatalogFallbackCount != 0)
            {
                status = "BLOCKED";
                statusReason = $"The catalog-resolved snapshot reports {resolved.CatalogFallbackCount} fallback(s).";
            }
            else if (fallbackPairs != 0)
            {
                status = "BLOCKED";
                statusReason = $"{fallbackPairs} distinct (material, shape) pair(s) used do not resolve in the installed catalog.";
            }

            return new SketchbookCatalogRow(
                candidate.Id, sizeLabel, true, parity,
                resolved.CatalogFallbackCount,
                resolved.ResolvedCatalogVersion, resolved.ResolvedCatalogFingerprint,
                fallbackPairs,
                result.Diagnostics.Select(diagnostic => diagnostic.ToString()).ToArray(),
                status, statusReason);
        }
        catch (Exception exception)
        {
            return new SketchbookCatalogRow(
                candidate.Id, sizeLabel, false, false, 0, null, null, 0,
                [],
                "BLOCKED", $"{exception.GetType().Name}: {exception.GetBaseException().Message}");
        }
    }

    // ---------------------------------------------------------------------------------
    // Probes
    // ---------------------------------------------------------------------------------

    private static SketchbookProbes MeasureProbes(
        HullGenerator generator, bool includeFull,
        IReadOnlyDictionary<(string Id, SketchbookSizeKind Kind), GeneratedHull?> hulls)
    {
        var terminal = new List<SketchbookTerminalColumnProbe>();
        foreach (var id in new[] { "05", "09" })
        {
            var candidate = SketchbookCandidates.Find(id);
            foreach (var kind in includeFull
                         ? new[] { SketchbookSizeKind.Suggested, SketchbookSizeKind.Minimum }
                         : new[] { SketchbookSizeKind.Suggested })
            {
                var sizeLabel = kind == SketchbookSizeKind.Suggested ? "suggested" : "minimum";
                var hull = hulls[(id, kind)];
                if (hull is null)
                {
                    terminal.Add(new SketchbookTerminalColumnProbe(id, sizeLabel, 0, true));
                    continue;
                }

                var extents = StationExtents(new SurfaceQualityGeometry(hull));
                var run = 0;
                for (var z = hull.MaxZ; z >= hull.MinZ; z--)
                {
                    if (!extents.TryGetValue(z, out var extent) || extent.MaxX - extent.MinX + 1 != 1)
                        break;
                    run++;
                }

                terminal.Add(new SketchbookTerminalColumnProbe(id, sizeLabel, run, run == 0));
            }
        }

        var bulbReset = MeasureBulbReset(generator);
        var bulb = MeasureBulbProbe(hulls[("13", SketchbookSizeKind.Suggested)]);
        var deckRuler = MeasureDeckRulerProbe();
        var tumblehome = MeasureTumblehomeProbe(hulls[("15", SketchbookSizeKind.Suggested)],
            hulls[("10", SketchbookSizeKind.Suggested)]);
        return new SketchbookProbes(terminal, bulbReset, bulb, deckRuler, tumblehome);
    }

    private static SketchbookBulbResetProbe MeasureBulbReset(HullGenerator generator)
    {
        var candidate = SketchbookCandidates.Find("10");
        var polluted = HullParameters.Default with
        {
            Length = 7,
            Width = 7,
            Height = 7,
            HasBulb = true,
            Bulb = new BulbSettings(12, 45, -4, 0),
            BowFullness = 0.9,
            SternFullness = -0.9,
            CrossSectionCurve = 0.9,
            Shape = new HullShapeSettings(
                new BowShapeSettings(0.9, 0.9, 80),
                new BodyShapeSettings(BodyStyle.Tumblehome, 0.9, -0.9, 0.9, 1),
                new SternShapeSettings(0.9, -0.9, 80),
                new HullProfileSettings(3, 3, 3, 3)),
            BowStyle = BowStyle.Axe,
            SternStyle = SternStyle.Fantail,
            Beamify = false,
            Smoothing = SmoothingMethod.CombinedSlopeFill,
            HybridFillOffset = 3,
        };
        var clean = SketchbookCandidates.ApplyTo(candidate, HullParameters.Default, candidate.SuggestedHeight);
        var reset = SketchbookCandidates.ApplyTo(candidate, polluted, candidate.SuggestedHeight);
        var cleanHash = HashNormalizedBase(generator.Generate(clean).Blocks);
        var resetHash = HashNormalizedBase(generator.Generate(reset).Blocks);
        return new SketchbookBulbResetProbe(
            candidate.Id,
            "suggested",
            reset.HasBulb,
            BulbOf(reset.EffectiveBulb),
            reset.Bulb == new BulbSettings(8, 35, 0, 0),
            cleanHash == resetHash,
            cleanHash,
            resetHash);
    }

    private static SketchbookBulbProbe MeasureBulbProbe(GeneratedHull? hull)
    {
        if (hull is null)
            return new SketchbookBulbProbe("13", "suggested", new SketchbookBulb(12, 45, -4, 0), true,
                false, 0, false, 0, 0, "Candidate 13 did not generate.", 0, []);

        var candidate = SketchbookCandidates.Find("13");
        var context = HullGenerator.CreateContext(
            SketchbookCandidates.ParametersFor(candidate, SketchbookSizeKind.Suggested));
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var centerX = (hull.MinX + hull.MaxX) / 2;
        var stemZ = occupied.Where(cell => cell.Y == hull.MaxY).Max(cell => cell.Z);

        // The forefoot-notch check is against the analytic solid, not the hollow shell: a bulb
        // is part of the hollow hull, so its centreline shell legitimately has a cavity between
        // the crown and the keel. An interior empty gap in the *solid* is the defect.
        var contiguous = true;
        var stations = new List<SketchbookBulbStation>();
        for (var z = stemZ + 1; z <= hull.MaxZ; z++)
        {
            var ys = Enumerable.Range(context.MinY, context.MaxY - context.MinY + 1)
                .Where(y => context.IsOccupied(centerX, y, z))
                .ToArray();
            if (ys.Length == 0)
                continue;
            var stationContiguous = ys[^1] - ys[0] + 1 == ys.Length;
            if (!stationContiguous)
                contiguous = false;
            stations.Add(new SketchbookBulbStation(z, ys[0], ys[^1], ys.Length, stationContiguous));
        }

        var shellComponents = CellComponents.Build(occupied).Count;
        var solid = new List<(int X, int Y, int Z)>();
        for (var x = context.MinX; x <= context.MaxX; x++)
        for (var y = context.MinY; y <= context.MaxY; y++)
        for (var z = context.MinZ; z <= context.MaxZ; z++)
            if (context.IsOccupied(x, y, z))
                solid.Add((x, y, z));
        var solidComponents = CellComponents.Build(solid).Count;
        var note = stations.Count == 0
            ? "No centreline bulb stations were found forward of the deck stem."
            : contiguous
                ? null
                : "The analytic solid's centreline column forward of the deck stem has an interior empty gap.";
        return new SketchbookBulbProbe("13", "suggested", new SketchbookBulb(12, 45, -4, 0), true,
            contiguous, stations.Count, shellComponents == 1, shellComponents, solidComponents,
            note, stemZ, stations);
    }

    private static SketchbookDeckRulerProbe MeasureDeckRulerProbe()
    {
        var candidate = SketchbookCandidates.Find("13");
        var parameters = SketchbookCandidates.ParametersFor(candidate, SketchbookSizeKind.Suggested);
        var withoutBulbParameters = parameters with { HasBulb = false };
        var withBulb = ShipGenerationService.ResolveDeckRuler(parameters);
        var withoutBulb = ShipGenerationService.ResolveDeckRuler(withoutBulbParameters);
        var bulbRulerResolved = withBulb is not null;
        var disabledRulerResolved = withoutBulb is not null;
        var identical = withBulb is null
            ? withoutBulb is null
            : withoutBulb is not null && withBulb.BowDatum == withoutBulb.BowDatum &&
              withBulb.SupportedEnd == withoutBulb.SupportedEnd;

        // The mission invariant is that bulb-only stations must not contaminate the ruler: the
        // authority must be derived from structural deck armour alone and the bulb must never
        // extend the supported interval. The generator instead shortens the whole stem by the
        // bulb's forward reach (HullGenerator: stemLength = Length - bulb.Stations), so the
        // supported deck interval legitimately moves aft. That is a recorded observation, not a
        // violation, so the real invariant is tested directly instead of requiring equality.
        var bulbContext = HullGenerator.CreateContext(parameters);
        var deckStations = bulbContext.EnumerateArmor()
            .Where(intent => intent.IsStructuralArmor && intent.Region == ArmorRegion.Deck)
            .Select(intent => intent.Cell.Z)
            .ToHashSet();

        // Stations forward of the ruler's own bow station are the ones a bulb can add. The
        // authority must ignore them: they must carry no structural deck armour, and the ruler
        // must not reach them or be extended by them.
        var interval = SupportedDeckRulerAuthority.Measure(bulbContext);
        var rulerBowStationIsDeckArmor = interval is not null && deckStations.Contains(interval.BowStation);
        var forwardOfRulerStations = interval is null
            ? []
            : Enumerable.Range(interval.BowStation + 1,
                Math.Max(0, bulbContext.MaxZ - interval.BowStation)).ToArray();
        var forwardOfRulerCarryDeckArmor = forwardOfRulerStations.Any(deckStations.Contains);
        var bulbExtendsSupportedInterval =
            withBulb is not null && withoutBulb is not null && withBulb.SupportedEnd > withoutBulb.SupportedEnd;
        var contaminated = forwardOfRulerCarryDeckArmor || bulbExtendsSupportedInterval;
        var stemShortenedMetres = withBulb is not null && withoutBulb is not null
            ? (withoutBulb.BowDatum - withBulb.BowDatum).Metres
            : 0;
        var note = contaminated
            ? "Bulb-only stations contaminate the supported deck ruler: " +
              $"forwardStationsCarryDeckArmor={forwardOfRulerCarryDeckArmor}, " +
              $"extendsInterval={bulbExtendsSupportedInterval}. The fixed candidate was preserved and not retuned."
            : "Observed, not a violation: the bulb replaces the forward stem, so the supported deck interval " +
              $"moves {stemShortenedMetres:0.###} m aft (bow datum {withoutBulb?.BowDatum} -> {withBulb?.BowDatum}, " +
              $"supported end {withoutBulb?.SupportedEnd} -> {withBulb?.SupportedEnd}). The ruler is derived from " +
              "structural deck armour only: its bow station carries structural deck armour, the " +
              $"{forwardOfRulerStations.Length} station(s) forward of it (to the bulb tip at Z={bulbContext.MaxZ}) " +
              "carry none, and the bulb does not extend the supported interval.";
        return new SketchbookDeckRulerProbe(
            candidate.Id, "suggested",
            bulbRulerResolved, disabledRulerResolved, identical, contaminated, note,
            withBulb?.BowDatum.ToString(), withBulb?.SupportedEnd.ToString(),
            withoutBulb?.BowDatum.ToString(), withoutBulb?.SupportedEnd.ToString(),
            interval?.BowStation ?? 0, bulbContext.MaxZ, forwardOfRulerStations.Length,
            forwardOfRulerCarryDeckArmor, rulerBowStationIsDeckArmor,
            bulbExtendsSupportedInterval, stemShortenedMetres);
    }

    private static SketchbookTumblehomeProbe MeasureTumblehomeProbe(
        GeneratedHull? primary, GeneratedHull? contrast) =>
        new(TumblehomeCase("15", primary), TumblehomeCase("10", contrast));

    private static SketchbookTumblehomeCase TumblehomeCase(string id, GeneratedHull? hull)
    {
        if (hull is null)
            return new SketchbookTumblehomeCase(id, "suggested", 0, 0, 0, 0, 0, 0, false, false, false);

        var geometry = new SurfaceQualityGeometry(hull);
        var breadth = MeasureBreadth(hull, geometry);
        var section = MeasureSection(hull, geometry);
        var belowTop = breadth.MaxHalfBreadthY < breadth.TopY;
        return new SketchbookTumblehomeCase(
            id, "suggested",
            breadth.MaxHalfBreadth, breadth.MaxHalfBreadthY, breadth.TopY, breadth.TumblehomeDelta,
            section.ContourCount, section.ClosedContourCount,
            belowTop, section.AnyClosedContour, belowTop && breadth.TumblehomeDelta > 0);
    }

    // ---------------------------------------------------------------------------------
    // Pairs and nearest neighbours
    // ---------------------------------------------------------------------------------

    private static IReadOnlyList<SketchbookCollapsedPair> DetectCollapsedPairs(IReadOnlyList<SketchbookBaseRow> rows)
    {
        var suggested = rows.Where(row => row.Size == "suggested" && row.Status == "OK").ToArray();
        var collapsed = new List<SketchbookCollapsedPair>();
        for (var left = 0; left < suggested.Length; left++)
        for (var right = left + 1; right < suggested.Length; right++)
            if (suggested[left].BaseNormalizedSha256 == suggested[right].BaseNormalizedSha256)
                collapsed.Add(new SketchbookCollapsedPair(
                    suggested[left].Id, suggested[right].Id, suggested[left].BaseNormalizedSha256));
        return collapsed;
    }

    private static IReadOnlyList<SketchbookPairRow> BuildPairTable(
        IReadOnlyDictionary<(string Id, SketchbookSizeKind Kind), GeneratedHull?> hulls)
    {
        var rows = new List<SketchbookPairRow>();
        foreach (var (leftId, rightId) in NamedPairs)
        {
            var left = hulls[(leftId, SketchbookSizeKind.Suggested)];
            var right = hulls[(rightId, SketchbookSizeKind.Suggested)];
            if (left is null || right is null)
            {
                rows.Add(new SketchbookPairRow(leftId, rightId, "", "", "", "", 0, 0, "", "", false, 0));
                continue;
            }

            var leftHash = HashNormalizedBase(left.Blocks);
            var rightHash = HashNormalizedBase(right.Blocks);
            rows.Add(new SketchbookPairRow(
                leftId, rightId,
                $"{left.OccupiedLength}x{left.OccupiedWidth}x{left.OccupiedHeight}",
                $"{right.OccupiedLength}x{right.OccupiedWidth}x{right.OccupiedHeight}",
                Bounds(left), Bounds(right),
                left.OccupiedCellCount, right.OccupiedCellCount,
                leftHash, rightHash, leftHash != rightHash,
                L2(Descriptor(left), Descriptor(right))));
        }

        return rows;
    }

    private static SketchbookNearestRow MeasureNearest(
        SketchbookCandidate candidate,
        IReadOnlyDictionary<(string Id, SketchbookSizeKind Kind), GeneratedHull?> hulls)
    {
        var hull = hulls[(candidate.Id, SketchbookSizeKind.Suggested)];
        if (hull is null)
            return new SketchbookNearestRow(candidate.Id, "", 0, []);
        var descriptor = Descriptor(hull);
        var nearestId = "";
        var nearestDistance = double.PositiveInfinity;
        foreach (var other in SketchbookCandidates.All)
        {
            if (other.Id == candidate.Id)
                continue;
            var otherHull = hulls[(other.Id, SketchbookSizeKind.Suggested)];
            if (otherHull is null)
                continue;
            var distance = L2(descriptor, Descriptor(otherHull));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestId = other.Id;
            }
        }

        return new SketchbookNearestRow(candidate.Id, nearestId, nearestDistance, descriptor);
    }

    /// <summary>
    /// 33 equally spaced longitudinal stations of occupied transverse width, normalized by the
    /// maximum width across the stations.
    /// </summary>
    private static IReadOnlyList<double> Descriptor(GeneratedHull hull)
    {
        var extents = StationExtents(new SurfaceQualityGeometry(hull));
        var widths = new double[33];
        for (var station = 0; station < 33; station++)
        {
            var z = hull.MinZ + (int)Math.Round((hull.MaxZ - hull.MinZ) * station / 32.0);
            widths[station] = extents.TryGetValue(z, out var extent) ? extent.MaxX - extent.MinX + 1 : 0;
        }

        var maximum = widths.Max();
        if (maximum <= 0)
            return widths;
        for (var index = 0; index < widths.Length; index++)
            widths[index] /= maximum;
        return widths;
    }

    private static double L2(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var total = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            var difference = left[index] - right[index];
            total += difference * difference;
        }

        return Math.Sqrt(total);
    }

    private static string Bounds(GeneratedHull hull) =>
        $"[{hull.MinX}..{hull.MaxX}]x[{hull.MinY}..{hull.MaxY}]x[{hull.MinZ}..{hull.MaxZ}]";

    // ---------------------------------------------------------------------------------
    // Statuses
    // ---------------------------------------------------------------------------------

    private static IReadOnlyList<SketchbookStatus> BuildStatuses(
        IReadOnlyList<SketchbookBaseRow> rows,
        IReadOnlyList<SketchbookCatalogRow> catalogRows,
        IReadOnlyList<SketchbookCollapsedPair> collapsed)
    {
        var statuses = new List<SketchbookStatus>();
        foreach (var candidate in SketchbookCandidates.All)
        {
            var reasons = new List<string>();
            foreach (var row in rows.Where(row => row.Id == candidate.Id && row.Status != "OK"))
                reasons.Add($"{row.Size}: {row.StatusReason}");
            foreach (var row in catalogRows.Where(row => row.Id == candidate.Id && row.Status != "OK"))
                reasons.Add($"catalog {row.Size}: {row.StatusReason}");
            if (collapsed.Any(pair => pair.LeftId == candidate.Id || pair.RightId == candidate.Id))
                reasons.Add("A distinct candidate collapsed to the same base-normalized geometry.");
            statuses.Add(new SketchbookStatus(
                candidate.Id, reasons.Count == 0 ? "OK" : "BLOCKED",
                reasons.Count == 0 ? null : string.Join(" | ", reasons)));
        }

        return statuses;
    }

    // ---------------------------------------------------------------------------------
    // Hashing (mirrors AutomaticSmoothingBaselineTests)
    // ---------------------------------------------------------------------------------

    internal static string HashNormalizedBase(IEnumerable<BlockPlacement> blocks)
    {
        var lines = blocks.SelectMany(block => block.OccupiedCells.Select(cell =>
        {
            var family = BlockShapeMetadata.Get(block.Shape).Family;
            return $"{cell.X},{cell.Y},{cell.Z}|{block.Material}|{family}|{block.Origin}|" +
                   $"{block.ArmorDepth}|{block.Construction}|{block.ArmorRegion}";
        })).Order(StringComparer.Ordinal);
        return Sha256(string.Join("\n", lines));
    }

    internal static string HashNativePlacements(IEnumerable<BlockPlacement> blocks)
    {
        var lines = blocks.Select(block =>
        {
            var info = BlockShapeMetadata.Get(block.Shape);
            return $"{block.X},{block.Y},{block.Z}|{block.Material}|{block.Shape}|{info.Family}|" +
                   $"{info.Length}|{info.Mirrored}|{info.ShortLength}|{block.Rotation}|{block.Origin}|" +
                   $"{block.ArmorDepth}|{block.Construction}|{block.ArmorRegion}";
        }).Order(StringComparer.Ordinal);
        return Sha256(string.Join("\n", lines));
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ---------------------------------------------------------------------------------
    // Markdown rendering
    // ---------------------------------------------------------------------------------

    private static string RenderMarkdown(SketchbookEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.Append("# Alternate Naval Sketchbook — native Shape V2 audit\n\n");
        builder.Append("This document is generated by `SketchbookNativeAudit` and must not be hand-edited. ");
        builder.Append("Every number comes from the current C# generator/validator/export path; the Python study is not an oracle.\n\n");
        builder.Append($"- Schema: `{evidence.SchemaId}` v{evidence.SchemaVersion}\n");
        builder.Append($"- Definitions SHA-256: `{evidence.DefinitionsSha256}`\n");
        builder.Append($"- Installed catalog game version: `{evidence.CatalogGameVersion}`\n");
        builder.Append($"- Regenerate with `dotnet run --project .\\FtdHullGenerator.SelfTest\\FtdHullGenerator.SelfTest.csproj -c Release -- --write-sketchbook-evidence`\n\n");

        builder.Append("## Mapping rules\n\n");
        builder.Append("Suggested size uses the exact suggested `L x W x H` and the exact baseline rises. ");
        builder.Append("Minimum size uses the exact minimum `L x W x H` and each baseline rise scaled independently from the immutable baseline:\n\n");
        builder.Append("```\napplied_rise = RoundAwayFromZero(baseline * (H_current - 1) / (H_suggested - 1))\n```\n\n");
        builder.Append("The `ApplyTo(candidate, basis, currentHeight)` initialiser overwrites Length/Width/Height, BowStyle, SternStyle, ");
        builder.Append("the whole Shape V2 bundle (with `Body.Style = Custom`), all four profile rises, flat bottom, the legacy scalar aliases ");
        builder.Append("`BowFullness = Shape.Bow.Fullness`, `SternFullness = Shape.Stern.Fullness`, `CrossSectionCurve = Shape.Body.Fullness`, ");
        builder.Append("and the complete explicit bulb state (`HasBulb` plus an explicit `Bulb`; disabled candidates carry `(8, 35, 0, 0)`). ");
        builder.Append("`Beamify = true` and base `Smoothing = None` are applied too.\n\n");

        builder.Append("| id | name | suggested | minimum | bow style | stern style | bow | body | stern | baseline profile | expected min profile | bulb | recommended |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var candidate in evidence.Candidates)
        {
            builder.Append($"| {candidate.Id} | {candidate.Name} | {candidate.SuggestedLength}x{candidate.SuggestedWidth}x{candidate.SuggestedHeight} | ");
            builder.Append($"{candidate.MinimumLength}x{candidate.MinimumWidth}x{candidate.MinimumHeight} | {candidate.BowStyle} | {candidate.SternStyle} | ");
            builder.Append($"({candidate.Bow.Fullness:R}, {candidate.Bow.Flare:R}, {candidate.Bow.EntranceLengthPercent}) | ");
            builder.Append($"({candidate.Body.Style}, {candidate.Body.Fullness:R}, {candidate.Body.SideShape:R}, {candidate.Body.Chine:R}, {candidate.Body.FlatBottom:R}) | ");
            builder.Append($"({candidate.Stern.Fullness:R}, {candidate.Stern.SideShape:R}, {candidate.Stern.RunLengthPercent}) | ");
            builder.Append($"{ProfileText(candidate.BaselineProfile)} | {ProfileText(candidate.ExpectedMinimumProfile)} | ");
            builder.Append($"{(candidate.HasBulb ? $"enabled ({candidate.Bulb.LengthPercent}, {candidate.Bulb.WidthPercent}, {candidate.Bulb.ForeAftPercent}, {candidate.Bulb.RisePercent})" : "disabled (8, 35, 0, 0)")} | ");
            builder.Append($"{candidate.Recommended} |\n");
        }

        builder.Append("\n## Candidate status\n\n");
        builder.Append("| id | status | reason |\n| --- | --- | --- |\n");
        foreach (var status in evidence.Statuses)
            builder.Append($"| {status.Id} | {status.Status} | {status.Reason ?? ""} |\n");

        builder.Append("\n## Suggested and minimum rows\n\n");
        builder.Append("| id | size | req LxWxH | applied profile | bounds | occ LxWxH | cells | blocks | base sha256 | native sha256 | det | validator | shell comp | solid comp | eroded core | cavity | cavity comp | ctx diag | surface exposed/step | section contours/closed (open/branch/coplanar) | max half-breadth @Y | top Y | tumblehome Δ | status |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var row in evidence.Rows)
        {
            builder.Append($"| {row.Id} | {row.Size} | {row.RequestedLength}x{row.RequestedWidth}x{row.RequestedHeight} | {ProfileText(row.AppliedProfile)} | ");
            builder.Append($"[{row.MinX}..{row.MaxX}]x[{row.MinY}..{row.MaxY}]x[{row.MinZ}..{row.MaxZ}] | ");
            builder.Append($"{row.OccupiedLength}x{row.OccupiedWidth}x{row.OccupiedHeight} | {row.OccupiedCellCount} | {row.BlockCount} | ");
            builder.Append($"`{Short(row.BaseNormalizedSha256)}` | `{Short(row.NativePlacementSha256)}` | {row.Deterministic} | {row.ValidatorErrorCount} | ");
            builder.Append($"{row.ShellComponentCount} | {row.SolidComponentCount} | {row.ErodedCoreCellCount} cells / {row.ErodedCoreComponentCount} comp | ");
            builder.Append($"{row.UsableCavityCellCount} | {row.CavityComponentCount} | {row.ContextDiagnosticCount} | ");
            builder.Append(row.Surface is { } surface
                ? $"{surface.HullSkinExposedArea:0.###}/{surface.HullSkinStepArea:0.###} | "
                : "— | ");
            builder.Append(row.Section is { } section
                ? $"{section.ContourCount}/{section.ClosedContourCount} (open {section.OpenEndpoints}, branch {section.BranchEndpoints}, coplanar {section.CoplanarFaces}) | {section.MaxHalfBreadth:0.###} @{section.MaxHalfBreadthY} | {section.TopOccupiedY} | {section.TumblehomeDelta:0.###} | "
                : "— | — | — | — | ");
            builder.Append($"{row.Status} |\n");
        }

        builder.Append("\n## Smoothing comparison (suggested size)\n\n");
        builder.Append("| id | method | valid | bounds unchanged | base unchanged | smoothing sha256 | placements | cells | additive | recommended | status |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var row in evidence.Smoothing)
            builder.Append($"| {row.Id} | {row.Method} | {row.Valid} | {row.BoundsUnchanged} | {row.BaseNormalizedUnchanged} | `{Short(row.SmoothingNativeSha256)}` | {row.SmoothingPlacementCount} | {row.OccupiedCellCount} | {row.Additive} | {row.IsRecommended} | {row.Status} |\n");

        builder.Append("\n## Catalog / export parity\n\n");
        builder.Append("| id | size | snapshot | occupancy parity | fallbacks | fallback pairs | catalog version | fingerprint | status |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var row in evidence.CatalogRows)
            builder.Append($"| {row.Id} | {row.Size} | {row.SnapshotPresent} | {row.OccupancyParity} | {row.CatalogFallbackCount} | {row.FallbackPairCount} | {row.ResolvedCatalogVersion ?? ""} | `{Short(row.ResolvedCatalogFingerprint)}` | {row.Status} |\n");

        builder.Append("\n## Special probes\n\n");
        builder.Append("### 05/09 terminal one-cell runs\n\n");
        builder.Append("| id | size | bow-most one-cell run (m) | zero-length finding |\n| --- | --- | --- | --- |\n");
        foreach (var probe in evidence.Probes.TerminalColumns)
            builder.Append($"| {probe.Id} | {probe.Size} | {probe.RunMetres} | {probe.ZeroLength} |\n");

        builder.Append("\n### 10 initialiser reset (no inherited bulb)\n\n");
        var reset = evidence.Probes.BulbReset;
        builder.Append($"- after `ApplyTo` over a polluted basis: `HasBulb = {reset.HasBulbAfter}`, `Bulb = ({reset.BulbAfter.LengthPercent}, {reset.BulbAfter.WidthPercent}, {reset.BulbAfter.ForeAftPercent}, {reset.BulbAfter.RisePercent})` (explicit tuple: {reset.ExplicitBulbTuple})\n");
        builder.Append($"- clean basis hash `{Short(reset.CleanSha256)}` == reset hash `{Short(reset.ResetSha256)}`: {reset.GeometryIdentical}\n");

        builder.Append("\n### 13 bulb\n\n");
        var bulb = evidence.Probes.Bulb;
        builder.Append($"- bulb `({bulb.Bulb.LengthPercent}, {bulb.Bulb.WidthPercent}, {bulb.Bulb.ForeAftPercent}, {bulb.Bulb.RisePercent})`, enabled: {bulb.BulbEnabled}\n");
        builder.Append($"- deck stem station: Z={bulb.DeckStemZ}\n");
        builder.Append($"- centreline Y interval contiguous forward of the deck stem: {bulb.CentrelineContiguous} over {bulb.CentrelineStationsChecked} station(s)\n");
        builder.Append($"- whole occupied set face-connected: {bulb.FaceConnected} (shell {bulb.ShellComponentCount}, analytic solid {bulb.SolidComponentCount} component(s))\n");
        if (bulb.Note is not null)
            builder.Append($"- note: {bulb.Note}\n");
        if (bulb.Stations.Count > 0)
        {
            builder.Append("\n| centreline station Z | min Y | max Y | cells | contiguous |\n| --- | --- | --- | --- | --- |\n");
            foreach (var station in bulb.Stations)
                builder.Append($"| {station.Z} | {station.MinY} | {station.MaxY} | {station.Count} | {station.Contiguous} |\n");
        }

        builder.Append("\n### 13 deck-ruler authority (bulb-only stations must not contaminate the ruler)\n\n");
        var ruler = evidence.Probes.DeckRuler;
        builder.Append($"- bulb on: BowDatum {ruler.BulbBowDatum ?? "(null)"}, SupportedEnd {ruler.BulbSupportedEnd ?? "(null)"}\n");
        builder.Append($"- bulb off: BowDatum {ruler.DisabledBowDatum ?? "(null)"}, SupportedEnd {ruler.DisabledSupportedEnd ?? "(null)"}\n");
        builder.Append($"- raw equality: {ruler.Identical}; observed stem shortening: {ruler.StemShortenedMetres:0.###} m\n");
        builder.Append($"- ruler bow station {ruler.RulerBowStation} carries structural deck armour: {ruler.RulerBowStationIsDeckArmor}\n");
        builder.Append($"- {ruler.ForwardOfRulerStationCount} station(s) forward of the ruler (to the bulb tip at Z={ruler.BulbTipStation}) carry structural deck armour: {ruler.ForwardOfRulerStationsCarryDeckArmor}\n");
        builder.Append($"- bulb extends the supported interval: {ruler.BulbExtendsSupportedInterval}\n");
        builder.Append($"- contaminated: {ruler.Contaminated}\n");
        if (ruler.Note is not null)
            builder.Append($"- {ruler.Note}\n");

        builder.Append("\n### 15 tumblehome (contrast 10)\n\n");
        builder.Append("| id | max half-breadth | @Y | top Y | tumblehome Δ | contours | closed | max below top | section closed | tumblehome |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var item in new[] { evidence.Probes.Tumblehome.Primary, evidence.Probes.Tumblehome.Contrast })
            builder.Append($"| {item.Id} | {item.MaxHalfBreadth:0.###} | {item.MaxHalfBreadthY} | {item.TopOccupiedY} | {item.TumblehomeDelta:0.###} | {item.ContourCount} | {item.ClosedContourCount} | {item.MaxBelowTopRow} | {item.SectionClosed} | {item.IsTumblehome} |\n");

        builder.Append("\n## Pair differences\n\n");
        builder.Append("| pair | dims | bounds | occupied cells | base sha256 | hashes differ | descriptor L2 |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var pair in evidence.Pairs)
            builder.Append($"| {pair.LeftId}/{pair.RightId} | {pair.LeftDimensions} vs {pair.RightDimensions} | {pair.LeftBounds} vs {pair.RightBounds} | {pair.LeftOccupiedCellCount} vs {pair.RightOccupiedCellCount} | `{Short(pair.LeftBaseSha256)}` vs `{Short(pair.RightBaseSha256)}` | {pair.BaseHashesDiffer} | {pair.DescriptorDistance:0.####} |\n");

        builder.Append("\n## Nearest neighbours (suggested size)\n\n");
        builder.Append("| id | nearest | L2 distance |\n| --- | --- | --- |\n");
        foreach (var row in evidence.NearestNeighbours)
            builder.Append($"| {row.Id} | {row.NearestId} | {row.L2Distance:0.####} |\n");

        builder.Append("\n## Collapsed pairs and findings\n\n");
        if (evidence.CollapsedPairs.Count == 0)
            builder.Append("- No distinct pair collapsed to the same base-normalized geometry.\n");
        foreach (var pair in evidence.CollapsedPairs)
            builder.Append($"- COLLAPSED: {pair.LeftId}/{pair.RightId} share `{pair.SharedSha256}`\n");
        foreach (var finding in evidence.Findings)
            builder.Append($"- {finding}\n");
        if (evidence.Findings.Count == 0)
            builder.Append("- No findings.\n");

        return builder.ToString();
    }

    private static string ProfileText(SketchbookProfile profile) =>
        $"({profile.BowDeckRise}, {profile.SternDeckRise}, {profile.BowKeelRise}, {profile.SternKeelRise})";

    private static string Short(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value[..Math.Min(12, value.Length)];
}

// ---------------------------------------------------------------------------------
// Evidence DTOs
// ---------------------------------------------------------------------------------

internal sealed record SketchbookEvidence(
    string SchemaId,
    int SchemaVersion,
    string DefinitionsSha256,
    string CatalogGameVersion,
    IReadOnlyList<SketchbookCandidateDefinition> Candidates,
    IReadOnlyList<SketchbookBaseRow> Rows,
    IReadOnlyList<SketchbookSmoothingRow> Smoothing,
    IReadOnlyList<SketchbookCatalogRow> CatalogRows,
    SketchbookProbes Probes,
    IReadOnlyList<SketchbookPairRow> Pairs,
    IReadOnlyList<SketchbookNearestRow> NearestNeighbours,
    IReadOnlyList<SketchbookCollapsedPair> CollapsedPairs,
    IReadOnlyList<string> Findings,
    IReadOnlyList<SketchbookStatus> Statuses);

internal sealed record SketchbookCandidateDefinition(
    string Id,
    string Name,
    int SuggestedLength,
    int SuggestedWidth,
    int SuggestedHeight,
    int MinimumLength,
    int MinimumWidth,
    int MinimumHeight,
    string BowStyle,
    string SternStyle,
    SketchbookBow Bow,
    SketchbookBody Body,
    SketchbookStern Stern,
    SketchbookProfile BaselineProfile,
    SketchbookProfile ExpectedMinimumProfile,
    SketchbookProfile AppliedMinimumProfile,
    bool HasBulb,
    SketchbookBulb Bulb,
    string Recommended);

internal sealed record SketchbookBow(double Fullness, double Flare, int EntranceLengthPercent);
internal sealed record SketchbookBody(string Style, double Fullness, double SideShape, double Chine, double FlatBottom);
internal sealed record SketchbookStern(double Fullness, double SideShape, int RunLengthPercent);
internal sealed record SketchbookProfile(int BowDeckRise, int SternDeckRise, int BowKeelRise, int SternKeelRise);
internal sealed record SketchbookBulb(int LengthPercent, int WidthPercent, int ForeAftPercent, int RisePercent);

internal sealed record SketchbookBaseRow(
    string Id,
    string Size,
    string Name,
    int RequestedLength,
    int RequestedWidth,
    int RequestedHeight,
    SketchbookProfile AppliedProfile,
    bool HasBulb,
    SketchbookBulb Bulb,
    string Recommended,
    int MinX,
    int MaxX,
    int MinY,
    int MaxY,
    int MinZ,
    int MaxZ,
    int OccupiedLength,
    int OccupiedWidth,
    int OccupiedHeight,
    int OccupiedCellCount,
    int BlockCount,
    IReadOnlyList<string> ConstructionNotes,
    string BaseNormalizedSha256,
    string NativePlacementSha256,
    bool Deterministic,
    int ValidatorErrorCount,
    IReadOnlyList<string> ValidatorErrors,
    int ShellComponentCount,
    int SolidComponentCount,
    int ErodedCoreComponentCount,
    int ErodedCoreCellCount,
    int UsableCavityCellCount,
    int CavityComponentCount,
    int ContextDiagnosticCount,
    IReadOnlyList<SketchbookRoleCount> RoleHistogram,
    SketchbookSurfaceRow? Surface,
    SketchbookSectionRow? Section,
    string Reproducer,
    string Status,
    string? StatusReason,
    IReadOnlyList<string>? CompetingExplanations);

internal sealed record SketchbookRoleCount(string Role, int Count);

internal sealed record SketchbookSurfaceRow(
    double HullSkinExposedArea,
    double HullSkinStepArea,
    int FragmentCount,
    int CoverageNoteCount);

internal sealed record SketchbookSectionRow(
    int StationZ,
    double CutOffset,
    int ContourCount,
    int ClosedContourCount,
    int OpenEndpoints,
    int BranchEndpoints,
    int CoplanarFaces,
    int DegenerateTouches,
    double MaxHalfBreadth,
    int MaxHalfBreadthY,
    int TopOccupiedY,
    double TumblehomeDelta,
    bool AnyClosedContour);

internal sealed record SketchbookSmoothingRow(
    string Id,
    string Method,
    bool Valid,
    int ValidatorErrorCount,
    bool BoundsUnchanged,
    bool BaseNormalizedUnchanged,
    string SmoothingNativeSha256,
    int SmoothingPlacementCount,
    int OccupiedCellCount,
    bool Additive,
    bool IsRecommended)
{
    public string Status { get; init; } = "OK";
    public string? StatusReason { get; init; }
}

internal sealed record SketchbookCatalogRow(
    string Id,
    string Size,
    bool SnapshotPresent,
    bool OccupancyParity,
    int CatalogFallbackCount,
    string? ResolvedCatalogVersion,
    string? ResolvedCatalogFingerprint,
    int FallbackPairCount,
    IReadOnlyList<string> Diagnostics,
    string Status,
    string? StatusReason);

internal sealed record SketchbookProbes(
    IReadOnlyList<SketchbookTerminalColumnProbe> TerminalColumns,
    SketchbookBulbResetProbe BulbReset,
    SketchbookBulbProbe Bulb,
    SketchbookDeckRulerProbe DeckRuler,
    SketchbookTumblehomeProbe Tumblehome);

internal sealed record SketchbookTerminalColumnProbe(
    string Id, string Size, int RunMetres, bool ZeroLength);

internal sealed record SketchbookBulbResetProbe(
    string Id,
    string Size,
    bool HasBulbAfter,
    SketchbookBulb BulbAfter,
    bool ExplicitBulbTuple,
    bool GeometryIdentical,
    string CleanSha256,
    string ResetSha256);

internal sealed record SketchbookBulbProbe(
    string Id,
    string Size,
    SketchbookBulb Bulb,
    bool BulbEnabled,
    bool CentrelineContiguous,
    int CentrelineStationsChecked,
    bool FaceConnected,
    int ShellComponentCount,
    int SolidComponentCount,
    string? Note,
    int DeckStemZ,
    IReadOnlyList<SketchbookBulbStation> Stations);

internal sealed record SketchbookBulbStation(
    int Z, int MinY, int MaxY, int Count, bool Contiguous);

internal sealed record SketchbookDeckRulerProbe(
    string Id,
    string Size,
    bool BulbRulerResolved,
    bool DisabledRulerResolved,
    bool Identical,
    bool Contaminated,
    string? Note,
    string? BulbBowDatum,
    string? BulbSupportedEnd,
    string? DisabledBowDatum,
    string? DisabledSupportedEnd,
    int RulerBowStation,
    int BulbTipStation,
    int ForwardOfRulerStationCount,
    bool ForwardOfRulerStationsCarryDeckArmor,
    bool RulerBowStationIsDeckArmor,
    bool BulbExtendsSupportedInterval,
    double StemShortenedMetres);

internal sealed record SketchbookTumblehomeProbe(
    SketchbookTumblehomeCase Primary,
    SketchbookTumblehomeCase Contrast);

internal sealed record SketchbookTumblehomeCase(
    string Id,
    string Size,
    double MaxHalfBreadth,
    int MaxHalfBreadthY,
    int TopOccupiedY,
    double TumblehomeDelta,
    int ContourCount,
    int ClosedContourCount,
    bool MaxBelowTopRow,
    bool SectionClosed,
    bool IsTumblehome);

internal sealed record SketchbookPairRow(
    string LeftId,
    string RightId,
    string LeftDimensions,
    string RightDimensions,
    string LeftBounds,
    string RightBounds,
    int LeftOccupiedCellCount,
    int RightOccupiedCellCount,
    string LeftBaseSha256,
    string RightBaseSha256,
    bool BaseHashesDiffer,
    double DescriptorDistance);

internal sealed record SketchbookNearestRow(
    string Id,
    string NearestId,
    double L2Distance,
    IReadOnlyList<double> Descriptor);

internal sealed record SketchbookCollapsedPair(
    string LeftId, string RightId, string SharedSha256);

internal sealed record SketchbookStatus(string Id, string Status, string? Reason);
