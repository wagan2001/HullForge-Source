using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Composition;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Geometry.HullSources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FtdHullGenerator.Geometry.Composition;

public sealed record HistoricalEnvelopeCompositionEvaluation(
    HullBuildContext Context,
    GeneratedHull Hull,
    HistoricalFidelityMetric VoxelQuantization,
    HistoricalFallbackValidation Validation);

/// <summary>
/// Explicit fallback adapter from one validated historical envelope to the existing composition
/// context. It voxelizes only the immutable envelope contract and never invokes regional formulas
/// or native smoothing.
/// </summary>
public static class HistoricalEnvelopeCompositionAdapter
{
    private sealed record ArmorAssignment(ArmorLayer Layer, int Depth, ArmorRegion Region);

    public static HullBuildContext CreateContext(
        LoadedHistoricalEnvelopeAsset loaded,
        HullParameters parameters,
        int maxPhysicalCells = 8_000_000,
        CancellationToken cancellationToken = default) =>
        Evaluate(loaded, parameters, maxPhysicalCells, cancellationToken).Context;

    public static HistoricalEnvelopeCompositionEvaluation Evaluate(
        LoadedHistoricalEnvelopeAsset loaded,
        HullParameters parameters,
        int maxPhysicalCells = 8_000_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return EvaluateCore(loaded.SampledSource, loaded.Asset.Metadata, parameters,
            maxPhysicalCells, loaded.Asset.Fidelity.VoxelQuantization.AcceptanceThresholdMetres ?? 0.5,
            cancellationToken);
    }

    /// <summary>Authoring-only evaluation before a package exists.</summary>
    public static HistoricalEnvelopeCompositionEvaluation EvaluateForAuthoring(
        SampledHullEnvelope source,
        HistoricalEnvelopeMetadata metadata,
        HullParameters parameters,
        double voxelAcceptanceMetres = 0.5,
        int maxPhysicalCells = 8_000_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(source.SourceContentHash, metadata.SourceContentHash,
                StringComparison.OrdinalIgnoreCase) || source.SourceTransform != metadata.SourceTransform)
            throw new HullGenerationException([
                "Historical authoring metadata does not match the sampled source hash/transform."]);
        return EvaluateCore(source, metadata, parameters, maxPhysicalCells,
            voxelAcceptanceMetres, cancellationToken);
    }

    private static HistoricalEnvelopeCompositionEvaluation EvaluateCore(
        SampledHullEnvelope source,
        HistoricalEnvelopeMetadata metadata,
        HullParameters parameters,
        int maxPhysicalCells,
        double voxelAcceptanceMetres,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxPhysicalCells is < 1 or > 64_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxPhysicalCells));
        if (!double.IsFinite(voxelAcceptanceMetres) || voxelAcceptanceMetres is < 0 or
            > HistoricalFidelityReport.MaximumVoxelAcceptanceMetres)
            throw new ArgumentOutOfRangeException(nameof(voxelAcceptanceMetres));
        var parameterErrors = parameters.Validate();
        if (parameterErrors.Count != 0)
            throw new HullGenerationException(parameterErrors);
        if (parameters.Smoothing != SmoothingMethod.None)
            throw new HullGenerationException([
                "Sampled historical fallback does not support native smoothing; select None."]);
        if (!source.IsUsable || source.UnsupportedRows.Count != 0)
            throw new HullGenerationException(["Historical envelope contains unsupported rows."]);
        if (source.Bounds.Length > 2_048.5 || source.Bounds.Beam > 1_024.5 ||
            source.Bounds.Depth > 512.5 ||
            metadata.DeckDatumMetres - metadata.KeelDatumMetres > 512.5)
            throw new HullGenerationException([
                "Historical envelope dimensions exceed the supported composition bounds."]);

        var expectedLength = Math.Max(HullParameters.MinimumLength,
            (int)Math.Round(source.Bounds.Length, MidpointRounding.AwayFromZero));
        var expectedWidth = Math.Max(HullParameters.MinimumWidth,
            (int)Math.Round(source.Bounds.Beam, MidpointRounding.AwayFromZero));
        var expectedHeight = Math.Max(HullParameters.MinimumHeight,
            (int)Math.Round(metadata.DeckDatumMetres - metadata.KeelDatumMetres,
                MidpointRounding.AwayFromZero));
        if (parameters.Length != expectedLength || parameters.Width != expectedWidth ||
            parameters.Height != expectedHeight)
            throw new HullGenerationException([
                $"Historical envelope requires {expectedLength}×{expectedWidth}×{expectedHeight} parameters; " +
                "implicit nonuniform scaling is not supported."]);

        var nominalMinX = -(parameters.Width / 2);
        var nominalMaxX = nominalMinX + parameters.Width - 1;
        var nominalMinZ = -(parameters.Length / 2);
        var nominalMaxZ = nominalMinZ + parameters.Length - 1;
        var nominalMinY = (int)Math.Floor(source.Bounds.MinY - metadata.KeelDatumMetres);
        var nominalMaxY = (int)Math.Ceiling(source.Bounds.MaxY - metadata.KeelDatumMetres) - 1;
        var candidateHeight = nominalMaxY - (long)nominalMinY + 1;
        if (candidateHeight <= 0 || candidateHeight > 1_024)
            throw new HullGenerationException([
                "Historical envelope has an invalid or unsupported vertical voxel domain."]);
        var candidateVolume = checked((long)parameters.Width * parameters.Length * candidateHeight);
        if (candidateVolume > maxPhysicalCells)
            throw new HullGenerationException([
                $"Historical envelope voxel domain {candidateVolume:N0} exceeds the {maxPhysicalCells:N0}-cell cap."]);

        var occupied = Voxelize(source, metadata.KeelDatumMetres,
            nominalMinX, nominalMaxX, nominalMinY, nominalMaxY, nominalMinZ, nominalMaxZ,
            maxPhysicalCells, cancellationToken);
        if (occupied.Count == 0 || !IsFaceConnected(occupied, cancellationToken))
            throw new HullGenerationException([
                "Historical envelope voxelization is empty or face-disconnected."]);

        var minX = occupied.Min(cell => cell.X);
        var maxX = occupied.Max(cell => cell.X);
        var minY = occupied.Min(cell => cell.Y);
        var maxY = occupied.Max(cell => cell.Y);
        var minZ = occupied.Min(cell => cell.Z);
        var maxZ = occupied.Max(cell => cell.Z);
        var occupiedRows = IndexRows(occupied, cancellationToken);
        var tops = new Dictionary<int, int>();
        var floors = new Dictionary<int, int>();
        var forward = new Dictionary<int, int>();
        var aft = new Dictionary<int, int>();
        foreach (var ((z, y), _) in occupiedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!floors.TryGetValue(z, out var floor) || y < floor) floors[z] = y;
            if (!tops.TryGetValue(z, out var top) || y > top) tops[z] = y;
            if (!aft.TryGetValue(y, out var aftZ) || z < aftZ) aft[y] = z;
            if (!forward.TryGetValue(y, out var forwardZ) || z > forwardZ) forward[y] = z;
        }

        // The sampled bounds include local sheer/stem extrema and underwater bulb volume. A
        // station owns structural deck semantics only when its solid crosses the declared deck
        // datum; its local exposed top may then rise above that datum. Bulb-only stations remain
        // ordinary side/bottom shell and cannot extend the arrangement ruler.
        var deckDatumY = (int)Math.Ceiling(
            metadata.DeckDatumMetres - metadata.KeelDatumMetres - 1e-9) - 1;
        var decks = new Dictionary<int, int>();
        foreach (var (z, top) in tops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (top >= deckDatumY && occupiedRows.ContainsKey((z, deckDatumY)))
                decks[z] = top;
        }
        if (decks.Count == 0)
            throw new HullGenerationException([
                "Historical envelope has no occupied station crossing its declared deck datum."]);

        bool IsDeckInterior(int x, int y, int z)
        {
            var cell = new HullCell(x, y, z);
            return occupied.Contains(cell) && decks.TryGetValue(z, out var deck) && y == deck &&
                   occupied.Contains(cell with { Y = y - 1 }) &&
                   occupied.Contains(cell with { X = x - 1 }) &&
                   occupied.Contains(cell with { X = x + 1 });
        }

        var shell = new HashSet<HullCell>();
        foreach (var cell in occupied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Neighbours(cell).Any(neighbour => !occupied.Contains(neighbour))) shell.Add(cell);
        }
        var armor = BuildArmor(occupied, occupiedRows, shell, decks, parameters, cancellationToken);
        var protectedShell = armor.Where(pair => pair.Value.Depth == 0 &&
                pair.Value.Region != ArmorRegion.Deck && !pair.Value.Layer.IsAir)
            .Select(pair => pair.Key).ToHashSet();
        var intents = armor.ToDictionary(pair => pair.Key, pair => new HullCellIntent(
            pair.Key,
            pair.Value.Layer.IsAir ? HullCellRole.ReservedArmorAir : pair.Value.Depth > 0
                ? HullCellRole.InternalArmor : pair.Value.Region switch
                {
                    ArmorRegion.Bottom => HullCellRole.BottomArmor,
                    ArmorRegion.Deck => HullCellRole.DeckArmor,
                    _ => HullCellRole.SideArmor,
                },
            pair.Value.Layer.Material,
            pair.Value.Region,
            pair.Value.Depth,
            pair.Value.Layer.Construction));

        var blocks = new List<BlockPlacement>(armor.Count);
        var blockIndex = 0;
        foreach (var pair in armor.OrderBy(pair => pair.Key.Z).ThenBy(pair => pair.Key.Y)
                     .ThenBy(pair => pair.Key.X))
        {
            if ((blockIndex++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (pair.Value.Layer.IsAir) continue;
            blocks.Add(new BlockPlacement(BlockShape.Cube, pair.Value.Layer.Material!.Value,
                pair.Key.X, pair.Key.Y, pair.Key.Z, 0)
            {
                ArmorDepth = pair.Value.Depth,
                ArmorRegion = pair.Value.Region,
                Construction = pair.Value.Layer.Construction,
                UsePoles = pair.Value.Layer.UsePoles,
            });
        }
        if (blocks.Count == 0)
            throw new HullGenerationException(["Historical envelope produced no structural armor."]);
        var hull = new GeneratedHull(parameters, blocks,
            blocks.Min(block => block.X), blocks.Max(block => block.X),
            blocks.Min(block => block.Y), blocks.Max(block => block.Y),
            blocks.Min(block => block.Z), blocks.Max(block => block.Z));
        HullGeometryValidator.EnsureValid(hull);
        var boundsMatch = hull.MinX == minX && hull.MaxX == maxX && hull.MinY == minY &&
                          hull.MaxY == maxY && hull.MinZ == minZ && hull.MaxZ == maxZ;
        if (!boundsMatch)
            throw new HullGenerationException([
                "Historical armor does not close every tightened occupied bound."]);

        var usableCavity = 0;
        foreach (var cell in occupied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!armor.ContainsKey(cell) && !IsDeckInterior(cell.X, cell.Y, cell.Z)) usableCavity++;
        }
        if (usableCavity == 0)
            throw new HullGenerationException([
                "Historical envelope and armor stack leave no usable cavity."]);

        GeneratedHull Compose(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return hull;
        }

        var context = new HullBuildContext(minX, maxX, minY, maxY, minZ, maxZ,
            parameters.Height - 1,
            (x, y, z) => occupied.Contains(new HullCell(x, y, z)), IsDeckInterior,
            z => decks.GetValueOrDefault(z, int.MinValue),
            z => floors.GetValueOrDefault(z, int.MinValue),
            intents, protectedShell, DeckOpeningMask.None, [], [], Compose);
        var metric = MeasureVoxelFidelity(source, metadata, occupiedRows,
            floors, tops, aft, forward,
            nominalMinX, nominalMaxX, nominalMinZ, nominalMaxZ,
            minX, maxX, minY, maxY, minZ, maxZ, voxelAcceptanceMetres, cancellationToken);
        var parameterJson = JsonSerializer.Serialize(parameters);
        var parameterHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parameterJson)))
            .ToLowerInvariant();
        var validation = new HistoricalFallbackValidation(parameterHash, parameters.Smoothing,
            occupied.Count, shell.Count,
            context.StructuralArmorCellCount, context.ReservedAirCellCount, usableCavity,
            minX, maxX, minY, maxY, minZ, maxZ,
            FaceConnected: true, ContextHullBoundsMatch: boundsMatch,
            "Exact sampled-envelope adapter output; tightened occupied bounds and armor intents");
        return new HistoricalEnvelopeCompositionEvaluation(context, hull, metric, validation);
    }

    private static Dictionary<(int Z, int Y), HullCell[]> IndexRows(
        IEnumerable<HullCell> occupied,
        CancellationToken cancellationToken)
    {
        var mutable = new Dictionary<(int Z, int Y), List<HullCell>>();
        foreach (var cell in occupied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mutable.TryGetValue((cell.Z, cell.Y), out var row))
                mutable[(cell.Z, cell.Y)] = row = [];
            row.Add(cell);
        }
        return mutable.ToDictionary(pair => pair.Key,
            pair => pair.Value.OrderBy(cell => cell.X).ToArray());
    }

    private static HistoricalFidelityMetric MeasureVoxelFidelity(
        SampledHullEnvelope source,
        HistoricalEnvelopeMetadata metadata,
        IReadOnlyDictionary<(int Z, int Y), HullCell[]> occupiedRows,
        IReadOnlyDictionary<int, int> floors,
        IReadOnlyDictionary<int, int> tops,
        IReadOnlyDictionary<int, int> aft,
        IReadOnlyDictionary<int, int> forward,
        int nominalMinX, int nominalMaxX, int nominalMinZ, int nominalMaxZ,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ,
        double threshold,
        CancellationToken cancellationToken)
    {
        var errors = new List<double>();
        var sourceCenterX = (source.Bounds.MinX + source.Bounds.MaxX) / 2;
        var targetCenterX = (nominalMinX + nominalMaxX) / 2.0;
        var sourceCenterZ = (source.Bounds.MinZ + source.Bounds.MaxZ) / 2;
        var targetCenterZ = (nominalMinZ + nominalMaxZ) / 2.0;
        double CandidateX(double boundary) => sourceCenterX + boundary - targetCenterX;
        double CandidateZ(double boundary) => sourceCenterZ + boundary - targetCenterZ;
        void Add(double left, double right) => errors.Add(Math.Abs(left - right));

        Add(source.Bounds.MinX, CandidateX(minX - 0.5));
        Add(source.Bounds.MaxX, CandidateX(maxX + 0.5));
        Add(source.Bounds.MinY, metadata.KeelDatumMetres + minY);
        Add(source.Bounds.MaxY, metadata.KeelDatumMetres + maxY + 1);
        Add(source.Bounds.MinZ, CandidateZ(minZ - 0.5));
        Add(source.Bounds.MaxZ, CandidateZ(maxZ + 0.5));
        // Declared datums are independent references, not aliases for bulb/local-sheer extrema.
        Add(metadata.KeelDatumMetres, metadata.KeelDatumMetres);
        Add(metadata.DeckDatumMetres, metadata.KeelDatumMetres +
            (int)Math.Round(metadata.DeckDatumMetres - metadata.KeelDatumMetres,
                MidpointRounding.AwayFromZero));

        for (var z = nominalMinZ; z <= nominalMaxZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedZ = (z - nominalMinZ + 0.5) / (nominalMaxZ - nominalMinZ + 1.0);
            for (var y = minY; y <= maxY; y++)
            {
                var normalizedY = (metadata.KeelDatumMetres + y + 0.5 - source.Bounds.MinY) /
                                  source.Bounds.Depth;
                var status = source.QueryNormalized(normalizedZ, normalizedY, out var sourceInterval);
                var hasCandidate = occupiedRows.TryGetValue((z, y), out var row);
                if (status == HullEnvelopeQueryStatus.Empty && !hasCandidate) continue;
                if (status != HullEnvelopeQueryStatus.Available || !hasCandidate)
                {
                    errors.Add(source.Bounds.Beam);
                    continue;
                }
                Add(sourceInterval.MinX, CandidateX(row![0].X - 0.5));
                Add(sourceInterval.MaxX, CandidateX(row[^1].X + 0.5));
            }
        }

        for (var z = minZ; z <= maxZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedZ = (z - nominalMinZ + 0.5) / (nominalMaxZ - nominalMinZ + 1.0);
            var sourceRows = new List<int>();
            for (var row = 0; row < source.RowCount; row++)
                if (source.QueryNormalized(normalizedZ, source.GetNormalizedY(row), out _) ==
                    HullEnvelopeQueryStatus.Available) sourceRows.Add(row);
            if (sourceRows.Count == 0 || !floors.TryGetValue(z, out var candidateFloor) ||
                !tops.TryGetValue(z, out var candidateTop))
            {
                errors.Add(source.Bounds.Depth);
                continue;
            }
            Add(source.Bounds.MinY + AxisLower(source, sourceRows[0], yAxis: true) * source.Bounds.Depth,
                metadata.KeelDatumMetres + candidateFloor);
            Add(source.Bounds.MinY + AxisUpper(source, sourceRows[^1], yAxis: true) * source.Bounds.Depth,
                metadata.KeelDatumMetres + candidateTop + 1);
        }

        for (var y = minY; y <= maxY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedY = (metadata.KeelDatumMetres + y + 0.5 - source.Bounds.MinY) /
                              source.Bounds.Depth;
            var sourceStations = new List<int>();
            for (var station = 0; station < source.StationCount; station++)
                if (source.QueryNormalized(source.GetNormalizedZ(station), normalizedY, out _) ==
                    HullEnvelopeQueryStatus.Available) sourceStations.Add(station);
            if (sourceStations.Count == 0 || !aft.TryGetValue(y, out var candidateAft) ||
                !forward.TryGetValue(y, out var candidateForward))
            {
                errors.Add(source.Bounds.Length);
                continue;
            }
            Add(source.Bounds.MinZ + AxisLower(source, sourceStations[0], yAxis: false) * source.Bounds.Length,
                CandidateZ(candidateAft - 0.5));
            Add(source.Bounds.MinZ + AxisUpper(source, sourceStations[^1], yAxis: false) * source.Bounds.Length,
                CandidateZ(candidateForward + 0.5));
        }

        var waterlineY = (metadata.WaterlineMetres - source.Bounds.MinY) / source.Bounds.Depth;
        var candidateWaterlineY = (int)Math.Floor(metadata.WaterlineMetres - metadata.KeelDatumMetres);
        for (var z = minZ; z <= maxZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedZ = (z - nominalMinZ + 0.5) / (nominalMaxZ - nominalMinZ + 1.0);
            var status = source.QueryNormalized(normalizedZ, waterlineY, out var interval);
            var hasCandidate = occupiedRows.TryGetValue((z, candidateWaterlineY), out var row);
            if (status == HullEnvelopeQueryStatus.Empty && !hasCandidate) continue;
            if (status != HullEnvelopeQueryStatus.Available || !hasCandidate)
            {
                errors.Add(source.Bounds.Beam);
                continue;
            }
            Add(interval.MinX, CandidateX(row![0].X - 0.5));
            Add(interval.MaxX, CandidateX(row[^1].X + 0.5));
        }

        var maximum = errors.Max();
        var mean = errors.Average();
        var rms = Math.Sqrt(errors.Sum(error => error * error) / errors.Count);
        return new HistoricalFidelityMetric(
            maximum <= threshold ? HistoricalFidelityStageStatus.Passed :
                HistoricalFidelityStageStatus.Failed,
            maximum, mean, rms, errors.Count, threshold,
            "Source envelope versus exact adapter-produced voxels in X/Y/Z, declared datums and silhouettes");
    }

    private static double AxisLower(SampledHullEnvelope source, int index, bool yAxis)
    {
        var current = yAxis ? source.GetNormalizedY(index) : source.GetNormalizedZ(index);
        if (index == 0) return 0;
        var previous = yAxis ? source.GetNormalizedY(index - 1) : source.GetNormalizedZ(index - 1);
        return (previous + current) / 2;
    }

    private static double AxisUpper(SampledHullEnvelope source, int index, bool yAxis)
    {
        var count = yAxis ? source.RowCount : source.StationCount;
        var current = yAxis ? source.GetNormalizedY(index) : source.GetNormalizedZ(index);
        if (index == count - 1) return 1;
        var next = yAxis ? source.GetNormalizedY(index + 1) : source.GetNormalizedZ(index + 1);
        return (current + next) / 2;
    }

    private static HashSet<HullCell> Voxelize(
        SampledHullEnvelope source,
        double keelDatum,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ,
        int maxCells,
        CancellationToken cancellationToken)
    {
        var occupied = new HashSet<HullCell>();
        var sourceCenter = (source.Bounds.MinX + source.Bounds.MaxX) / 2;
        var targetCenter = (minX + maxX) / 2.0;
        for (var z = minZ; z <= maxZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedZ = (z - minZ + 0.5) / (maxZ - minZ + 1.0);
            for (var y = minY; y <= maxY; y++)
            {
                var sourceY = keelDatum + y + 0.5;
                var normalizedY = (sourceY - source.Bounds.MinY) / source.Bounds.Depth;
                var status = source.QueryNormalized(normalizedZ, normalizedY, out var interval);
                if (status is HullEnvelopeQueryStatus.Empty or HullEnvelopeQueryStatus.OutsideSampleDomain)
                    continue;
                if (status != HullEnvelopeQueryStatus.Available)
                    throw new HullGenerationException([
                        "Historical envelope row cannot be represented as one interval."]);
                var rowMin = Math.Max(minX, (int)Math.Ceiling(
                    targetCenter + interval.MinX - sourceCenter - 1e-9));
                var rowMax = Math.Min(maxX, (int)Math.Floor(
                    targetCenter + interval.MaxX - sourceCenter + 1e-9));
                for (var x = rowMin; x <= rowMax; x++)
                {
                    if (!occupied.Add(new HullCell(x, y, z))) continue;
                    if (occupied.Count > maxCells)
                        throw new HullGenerationException([
                            $"Historical envelope exceeded the {maxCells:N0}-cell voxel cap."]);
                }
            }
        }
        return occupied;
    }

    private static Dictionary<HullCell, ArmorAssignment> BuildArmor(
        HashSet<HullCell> occupied,
        IReadOnlyDictionary<(int Z, int Y), HullCell[]> occupiedRows,
        HashSet<HullCell> shell,
        IReadOnlyDictionary<int, int> decks,
        HullParameters parameters,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<HullCell, ArmorAssignment>();
        var sideSeeds = shell.Where(cell =>
            !(decks.TryGetValue(cell.Z, out var deck) && cell.Y == deck &&
              occupied.Contains(cell with { Y = cell.Y - 1 }) &&
              occupied.Contains(cell with { X = cell.X - 1 }) &&
              occupied.Contains(cell with { X = cell.X + 1 })) &&
            occupied.Contains(cell with { Y = cell.Y - 1 })).ToArray();
        var bottomSeeds = shell.Where(cell =>
            !occupied.Contains(cell with { Y = cell.Y - 1 })).ToArray();
        Grow(parameters.HullArmor, sideSeeds, ArmorRegion.Side, priority: 0);
        Grow(parameters.EffectiveBottomArmor, bottomSeeds, ArmorRegion.Bottom, priority: 1);

        if (parameters.DeckArmor is { } deckLayout)
        {
            for (var depth = 0; depth < deckLayout.Thickness; depth++)
            foreach (var (z, deckY) in decks.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!occupiedRows.TryGetValue((z, deckY - depth), out var row)) continue;
                foreach (var cell in row)
                {
                    var candidate = new ArmorAssignment(deckLayout.Layers[depth], depth,
                        ArmorRegion.Deck);
                    if (!result.TryGetValue(cell, out var existing) || depth < existing.Depth)
                        result[cell] = candidate;
                }
            }
        }
        RequireEveryLayer(parameters.HullArmor, ArmorRegion.Side, "side");
        RequireEveryLayer(parameters.EffectiveBottomArmor, ArmorRegion.Bottom, "bottom");
        if (parameters.DeckArmor is { } requiredDeck)
            RequireEveryLayer(requiredDeck, ArmorRegion.Deck, "deck");
        return result;

        void RequireEveryLayer(ArmorLayout layout, ArmorRegion region, string label)
        {
            var present = result.Values.Where(value => value.Region == region)
                .Select(value => value.Depth).ToHashSet();
            for (var depth = 0; depth < layout.Thickness; depth++)
                if (!present.Contains(depth))
                    throw new HullGenerationException([
                        $"Historical envelope {label} armor layer {depth + 1} does not fit."]);
        }

        void Grow(ArmorLayout layout, IEnumerable<HullCell> seeds, ArmorRegion region, int priority)
        {
            var distances = new Dictionary<HullCell, int>();
            var queue = new Queue<HullCell>();
            foreach (var seed in seeds.OrderBy(cell => cell.Z).ThenBy(cell => cell.Y)
                         .ThenBy(cell => cell.X))
                if (distances.TryAdd(seed, 0)) queue.Enqueue(seed);
            while (queue.TryDequeue(out var cell))
            {
                if ((queue.Count & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var depth = distances[cell];
                var candidate = new ArmorAssignment(layout.Layers[depth], depth, region);
                if (!result.TryGetValue(cell, out var existing) || depth < existing.Depth ||
                    depth == existing.Depth && priority == 0 && existing.Region != ArmorRegion.Side)
                    result[cell] = candidate;
                if (depth + 1 >= layout.Thickness) continue;
                foreach (var neighbour in Neighbours(cell))
                    if (occupied.Contains(neighbour) && distances.TryAdd(neighbour, depth + 1))
                        queue.Enqueue(neighbour);
            }
        }
    }

    private static bool IsFaceConnected(HashSet<HullCell> occupied, CancellationToken cancellationToken)
    {
        var remaining = new HashSet<HullCell>(occupied);
        var queue = new Queue<HullCell>();
        var first = remaining.First();
        remaining.Remove(first);
        queue.Enqueue(first);
        while (queue.TryDequeue(out var cell))
        {
            if ((queue.Count & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            foreach (var neighbour in Neighbours(cell))
                if (remaining.Remove(neighbour)) queue.Enqueue(neighbour);
        }
        return remaining.Count == 0;
    }

    private static IEnumerable<HullCell> Neighbours(HullCell cell)
    {
        yield return cell with { X = cell.X - 1 };
        yield return cell with { X = cell.X + 1 };
        yield return cell with { Y = cell.Y - 1 };
        yield return cell with { Y = cell.Y + 1 };
        yield return cell with { Z = cell.Z - 1 };
        yield return cell with { Z = cell.Z + 1 };
    }
}
