using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization.Decorations;

namespace FtdHullGenerator.Serialization;

public sealed record BlueprintExportResult(
    string FilePath,
    int BlockCount,
    double MaterialCost,
    int ShapeFallbackCount,
    string GameVersion,
    int BeamCount,
    int PoleCount,
    int BeamSlopeCount,
    int OccupiedCellCount,
    int SlopeCount)
{
    /// <summary>Optional debug sidecar containing the exact generation parameters.</summary>
    public string? GenerationParametersPath { get; init; }
}

/// <summary>
/// Writes a self-contained native .blueprint from resolved placements and catalog GUIDs.
/// </summary>
/// <remarks>
/// The four format rules this writer is built on, all corpus-confirmed:
/// BLP/BLR/BCI/BlockIds are the block-length arrays; ItemDictionary numbers are file-local while
/// GUIDs are stable, so the dictionary is rebuilt per export and numeric ids are never copied
/// from an example file; MinCords/MaxCords are occupied-volume bounds, so they come from expanded
/// cells rather than anchors; and the game rewrites several fields on re-save, so a neutral
/// envelope is emitted rather than copied player state.
/// Native export emits no mimics or decorations unless the central runtime exposure policy allows
/// an explicit resolved refinement through the bounded decoration codec. Generated-stream game
/// load/re-save remains Unresolved.
/// Export is validated before the file is written; see BlueprintExportValidator below.
/// </remarks>
public sealed class BlueprintExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string[] DefaultColors =
    [
        "0,0,0,0",
        "1,0,0,0.5",
        "0,1,0,0.5",
        "0,0,1,0.5",
        "0.1,0.1,0.1,0.5",
        "0.3,0.3,0.3,0.5",
        "0.5,0.5,0.5,0.5",
        "1,1,1,0.1",
        "1,1,1,0.5",
        "1,1,1,0.99",
        "1,1,0,0.99",
        "0,1,1,0.99",
        "1,0,1,0.99",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
        "0,0,0,0",
    ];

    /// <summary>
    /// Exports the already-resolved physical snapshot for the current editor revision. The
    /// captured catalog and placement list are reused verbatim; this overload never invokes a
    /// generator and refuses a stale snapshot before creating an output directory.
    /// </summary>
    public BlueprintExportResult Export(
        ShipGenerationSnapshot snapshot,
        ShipDocument currentDocument,
        long currentRevision,
        string outputDirectory,
        string requestedName,
        bool recordGenerationParameters = false,
        FeatureExposurePolicy? exposure = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentDocument);
        ValidateResolvedSnapshot(snapshot, currentDocument, currentRevision);
        if (snapshot.Refinement is { } refinement)
        {
            refinement.ValidateFor(snapshot);
            if (!refinement.Extensions.IsEmpty)
            {
                // The manual/legacy explicit-anchor editor remains experimental. The normal
                // parameter-free Deco Vertical/Horizontal choices are derived automatically and
                // must export without the Experimental Features toggle.
                if (snapshot.Document.Smoothing.ExplicitRefinement is not null)
                {
                    exposure ??= new FeatureExposurePolicy(experimentalFeaturesEnabled: false);
                    if (!exposure.IsAvailable(ProductFeature.ExplicitSlopeRefinement))
                        throw new InvalidOperationException(exposure.UnavailableReason(ProductFeature.ExplicitSlopeRefinement));
                }
                return ExportRefinedForControlledOfflineEvidence(snapshot, currentDocument, currentRevision,
                    outputDirectory, requestedName, recordGenerationParameters: recordGenerationParameters);
            }
        }
        else if (snapshot.Document.Smoothing.ExplicitRefinement is not null)
            throw new InvalidOperationException("DEC105: Explicit refinement has no resolved visual snapshot.");
        return ExportCore(snapshot.Hull, snapshot.Catalog, outputDirectory, requestedName,
            recordGenerationParameters, null);
    }

    public BlueprintExportResult Export(
        GeneratedHull hull,
        FtdBlockCatalog catalog,
        string outputDirectory,
        string requestedName,
        bool recordGenerationParameters = false) =>
        ExportCore(hull, catalog, outputDirectory, requestedName, recordGenerationParameters, null);

    /// <summary>
    /// Dark evidence-only channel for byte-preservation checks. It accepts opaque chunks and does
    /// not interpret or produce decoration geometry.
    /// </summary>
    internal BlueprintExportResult ExportWithUnverifiedDecorationsForControlledOfflineEvidence(
        ShipGenerationSnapshot snapshot,
        ShipDocument currentDocument,
        long currentRevision,
        string outputDirectory,
        string requestedName,
        UnverifiedDecorationExportRequest request,
        UnverifiedDecorationExportCapability? capability,
        bool recordGenerationParameters = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentDocument);
        ArgumentNullException.ThrowIfNull(request);
        ValidateResolvedSnapshot(snapshot, currentDocument, currentRevision);
        var vehicleData = VehicleDataDecorationCodec.ReplaceOrAppendForControlledOfflinePreservationEvidence(
            snapshot, request, capability);
        return ExportCore(snapshot.Hull, snapshot.Catalog, outputDirectory, requestedName,
            recordGenerationParameters, vehicleData);
    }

    internal BlueprintExportResult ExportRefinedForControlledOfflineEvidence(
        ShipGenerationSnapshot snapshot, ShipDocument currentDocument, long currentRevision,
        string outputDirectory, string requestedName, byte[]? existingVehicleData = null,
        bool recordGenerationParameters = false)
    {
        ValidateResolvedSnapshot(snapshot, currentDocument, currentRevision);
        var refinement = snapshot.Refinement ?? throw new InvalidOperationException("DEC105: No resolved refinement.");
        refinement.ValidateFor(snapshot);
        if (refinement.Extensions.IsEmpty && existingVehicleData is null)
            return ExportCore(snapshot.Hull, snapshot.Catalog, outputDirectory, requestedName, recordGenerationParameters, null);
        // Existing records (including opaque chunks and IDs) are preserved; allocate new IDs above them.
        var existing = existingVehicleData is null ? null : VehicleDataDecorationCodec.Read(existingVehicleData);
        var records = existing?.Records ?? System.Collections.Immutable.ImmutableArray<FtdHullGenerator.Domain.Decorations.DecorationRecord>.Empty;
        var nextId = records.IsEmpty ? 0 : checked(records.Max(record => record.DecorationId) + 1);
        var added = refinement.Module.Records.Select(record => record with { DecorationId = checked(nextId++) });
        var module = new FtdHullGenerator.Domain.Decorations.DecorationModule(records.Concat(added));
        return ExportWithUnverifiedDecorationsForControlledOfflineEvidence(snapshot, currentDocument, currentRevision,
            outputDirectory, requestedName,
            UnverifiedDecorationExportRequest.CaptureForControlledOfflinePreservationEvidence(snapshot, module, existingVehicleData),
            UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly, recordGenerationParameters);
    }

    private static void ValidateResolvedSnapshot(
        ShipGenerationSnapshot snapshot,
        ShipDocument currentDocument,
        long currentRevision)
    {
        if (snapshot.Refinement is not null && !ReferenceEquals(snapshot.Document, currentDocument) ||
            !snapshot.IsCurrent(currentRevision) || snapshot.Hull.SourceRevision != currentRevision ||
            !string.Equals(snapshot.Document.DocumentId, currentDocument.DocumentId,
                StringComparison.Ordinal) ||
            !string.Equals(snapshot.Hull.SourceDocumentId, snapshot.Document.DocumentId,
                StringComparison.Ordinal) ||
            !string.Equals(snapshot.Hull.ResolvedCatalogVersion, snapshot.Catalog.GameVersion,
                StringComparison.Ordinal) ||
            !string.Equals(snapshot.Hull.ResolvedCatalogFingerprint,
                ShipCatalogFingerprint.Compute(snapshot.Catalog), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The resolved ship snapshot belongs to document '{snapshot.Document.DocumentId}' at " +
                $"revision {snapshot.Revision}; document '{currentDocument.DocumentId}' at revision " +
                $"{currentRevision} is current. Generate and validate the current document revision before export.");
        }

        if (snapshot.Diagnostics.Any(diagnostic => diagnostic.IsError))
            throw new InvalidOperationException("A ship snapshot with composition errors cannot be exported.");
    }

    private BlueprintExportResult ExportCore(
        GeneratedHull hull,
        FtdBlockCatalog catalog,
        string outputDirectory,
        string requestedName,
        bool recordGenerationParameters,
        byte[]? vehicleData)
    {
        ArgumentNullException.ThrowIfNull(hull);
        ArgumentNullException.ThrowIfNull(catalog);

        if (hull.Blocks.Count == 0)
            throw new InvalidOperationException("A hull must contain at least one block before it can be exported.");

        // A cube substitute changes the joins in a coordinated surface assembly.
        // Resolve every fitted part before creating a directory or output file.
        if (hull.Parameters.Smoothing == SmoothingMethod.InvertedTriangleFill)
        {
            var missing = hull.Blocks
                .Where(placement => placement.Origin == BlockOrigin.Smoothing && placement.Shape != BlockShape.Cube)
                .Select(placement => (placement.Material, placement.Shape)).Distinct()
                .Where(part => catalog.Resolve(part.Material, part.Shape).IsFallback)
                .OrderBy(part => part.Material).ThenBy(part => part.Shape).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("Cannot export the fitted hull: the game catalog is missing " +
                    string.Join(", ", missing.Select(part => $"{part.Material} {part.Shape}")) +
                    ". Refresh the game catalog or choose another smoothing method.");
        }

        Directory.CreateDirectory(outputDirectory);
        var fileName = MakeFileName(requestedName);
        var targetPath = CreateAvailablePath(outputDirectory, fileName);

        // Beams, poles, and per-layer beam slopes keep their own item and rotation 0.
        // Additive smoothing slopes keep their own item and real rotation (vertical
        // 0/2/4/6/8/10/12/14 — the descending branch uses 0/2 treads and 8/10 risers —
        // horizontal 16–19/22/23, and cross-section 5/7/9/11). The passes in
        // Geometry/Smoothing are the authority for that set; this comment is a summary.
        // The vertical and horizontal
        // layouts match independent, game-saved hand corrections; the cross-section
        // layout uses rotations measured from Kevin's saved corpus but still needs
        // its own in-game correction pass. Every other non-cube shape is exported
        // as a full block: the generator identifies candidate fitted cells, but
        // their exact game rotations have not passed an in-game fixture yet, so
        // full blocks keep all six connection
        // faces on every occupied lattice cell.
        var entries = new List<(BlockPlacement Placement, CatalogBlock Block, bool ReplacedFittedShape)>(hull.Blocks.Count);
        foreach (var placement in hull.Blocks)
        {
            if (!placement.KeepsFittedShape)
            {
                entries.Add((
                    placement.Shape == BlockShape.Cube ? placement : placement with { Shape = BlockShape.Cube, Rotation = 0 },
                    catalog.Resolve(placement.Material, BlockShape.Cube),
                    placement.Shape != BlockShape.Cube));
                continue;
            }

            if (placement.CellLength > 1)
            {
                var part = catalog.Resolve(placement.Material, placement.Shape);
                if (!part.IsFallback)
                {
                    entries.Add((placement, part, false));
                    continue;
                }

                // Resolve falls back to the material's one-metre cube. Emitting that at
                // the anchor alone would leave the rest of the footprint empty, so
                // expand it back into individual cubes instead. A slope degrades to a
                // blocky step; a beam or pole keeps its occupied lattice cells.
                var cube = catalog.Resolve(placement.Material, BlockShape.Cube);
                foreach (var cell in placement.OccupiedCells)
                {
                    entries.Add((
                        placement with { Shape = BlockShape.Cube, X = cell.X, Y = cell.Y, Z = cell.Z, Rotation = 0 },
                        cube,
                        true));
                }
                continue;
            }

            // What is left is a one-metre smoothing slope or pole. Both retain their
            // installed catalog item when it is available.
            var fittedPart = catalog.Resolve(placement.Material, placement.Shape);
            entries.Add(fittedPart.IsFallback
                ? (placement with { Shape = BlockShape.Cube, Rotation = 0 }, catalog.Resolve(placement.Material, BlockShape.Cube), true)
                : (placement, fittedPart, false));
        }

        var resolved = entries.ToArray();
        var itemIds = new Dictionary<Guid, int> { [FtdBlockCatalog.ConstructableVehicleGuid] = 1 };
        foreach (var pair in resolved)
        {
            var block = pair.Block;
            if (!itemIds.ContainsKey(block.Guid))
                itemIds.Add(block.Guid, itemIds.Count + 1);
        }

        var materialCost = resolved.Sum(pair => pair.Block.MaterialCost);
        var model = new BlueprintFileModel
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            SavedTotalBlockCount = resolved.Length,
            SavedMaterialCost = materialCost,
            ItemDictionary = itemIds.ToDictionary(pair => pair.Value.ToString(CultureInfo.InvariantCulture), pair => pair.Key.ToString()),
            Blueprint = new BlueprintModel
            {
                ContainedMaterialCost = 0,
                SpecialInfo = Enumerable.Repeat(-1d, 80).ToArray(),
                Colors = DefaultColors,
                SubConstructs = Array.Empty<object>(),
                Positions = resolved.Select(pair => FormatPosition(pair.Placement)).ToArray(),
                Rotations = resolved.Select(pair => pair.Placement.Rotation).ToArray(),
                BlockParameter1 = null,
                BlockParameter2 = null,
                ColorIndices = Enumerable.Repeat(0, resolved.Length).ToArray(),
                BlockExtraInfo = null,
                BlockData = Array.Empty<byte>(),
                VehicleData = vehicleData,
                BlueprintName = Path.GetFileNameWithoutExtension(fileName),
                SerialisedInfo = new SerialisedInfoModel(),
                LegacyName = null,
                ItemNumber = itemIds[FtdBlockCatalog.ConstructableVehicleGuid],
                LocalPosition = "0,0,0",
                LocalRotation = "0,0,0,1",
                ForceId = Random.Shared.Next(1, int.MaxValue),
                TotalBlockCount = resolved.Length,
                MaxCoordinates = FormatPosition(hull.MaxX, hull.MaxY, hull.MaxZ),
                MinCoordinates = FormatPosition(hull.MinX, hull.MinY, hull.MinZ),
                BlockIds = resolved.Select(pair => itemIds[pair.Block.Guid]).ToArray(),
                BlockState = resolved.Length == 1 ? "0" : $"=0,{resolved.Length.ToString(CultureInfo.InvariantCulture)}",
                AliveCount = resolved.Length,
                BlockStringData = Array.Empty<string>(),
                BlockStringDataIds = Array.Empty<int>(),
                GameVersion = catalog.GameVersion,
                PersistentSubObjectIndex = -1,
                PersistentBlockIndex = -1,
                AuthorDetails = new AuthorDetailsModel(),
                BlockCount = resolved.Length,
            },
        };

        var json = JsonSerializer.Serialize(model, JsonOptions);
        BlueprintExportValidator.Validate(json, resolved.Length);

        var generationParametersPath = recordGenerationParameters
            ? GenerationParametersWriter.GetParametersPath(targetPath)
            : null;
        var temporaryPath = targetPath + ".tmp";
        try
        {
            if (recordGenerationParameters)
                GenerationParametersWriter.Write(hull, targetPath, catalog.GameVersion);

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, targetPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(targetPath);
            if (generationParametersPath is not null)
                TryDelete(generationParametersPath);
            throw;
        }

        return new BlueprintExportResult(
            targetPath,
            resolved.Length,
            materialCost,
            resolved.Count(pair => pair.ReplacedFittedShape || pair.Block.IsFallback),
            catalog.GameVersion,
            resolved.Count(pair => pair.Placement.Shape
                is BlockShape.Beam2 or BlockShape.Beam3 or BlockShape.Beam4),
            resolved.Count(pair => pair.Placement.Shape
                is BlockShape.Pole1 or BlockShape.Pole2 or BlockShape.Pole3 or BlockShape.Pole4),
            resolved.Count(pair => BlockShapeMetadata.Get(pair.Placement.Shape).Family == StructuralFamily.BeamSlope),
            resolved.Sum(pair => pair.Placement.CellLength),
            resolved.Count(pair => pair.Placement.Shape
                is BlockShape.Slope1 or BlockShape.Slope2 or BlockShape.Slope3 or BlockShape.Slope4))
        {
            GenerationParametersPath = generationParametersPath,
        };

        static void TryDelete(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Cleanup is best-effort to avoid masking the original export failure.
                }
            }
        }
    }

    private static string MakeFileName(string requestedName)
    {
        var name = string.IsNullOrWhiteSpace(requestedName) ? "Generated_hull" : requestedName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = string.Join('_', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        name = name.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(name))
            name = "Generated_hull";
        if (!name.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase))
            name += ".blueprint";
        return name;
    }

    private static string CreateAvailablePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName}_{suffix}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException("Could not find a free file name for the generated blueprint.");
    }

    private static string FormatPosition(BlockPlacement placement) => FormatPosition(placement.X, placement.Y, placement.Z);

    private static string FormatPosition(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"{x},{y},{z}");

    private sealed class BlueprintFileModel
    {
        [JsonPropertyName("FileModelVersion")]
        public FileModelVersion FileModelVersion { get; init; } = new();

        [JsonPropertyName("Name")]
        public required string Name { get; init; }

        [JsonPropertyName("Version")]
        public int Version { get; init; } = 1;

        [JsonPropertyName("SavedTotalBlockCount")]
        public required int SavedTotalBlockCount { get; init; }

        [JsonPropertyName("SavedMaterialCost")]
        public required double SavedMaterialCost { get; init; }

        [JsonPropertyName("ContainedMaterialCost")]
        public double ContainedMaterialCost { get; init; }

        [JsonPropertyName("ItemDictionary")]
        public required Dictionary<string, string> ItemDictionary { get; init; }

        [JsonPropertyName("Blueprint")]
        public required BlueprintModel Blueprint { get; init; }
    }

    private sealed class FileModelVersion
    {
        [JsonPropertyName("Major")]
        public int Major { get; init; } = 1;

        [JsonPropertyName("Minor")]
        public int Minor { get; init; }
    }

    private sealed class BlueprintModel
    {
        [JsonPropertyName("ContainedMaterialCost")]
        public double ContainedMaterialCost { get; init; }

        [JsonPropertyName("CSI")]
        public required double[] SpecialInfo { get; init; }

        [JsonPropertyName("COL")]
        public required string[] Colors { get; init; }

        [JsonPropertyName("SCs")]
        public required object[] SubConstructs { get; init; }

        [JsonPropertyName("BLP")]
        public required string[] Positions { get; init; }

        [JsonPropertyName("BLR")]
        public required int[] Rotations { get; init; }

        [JsonPropertyName("BP1")]
        public object? BlockParameter1 { get; init; }

        [JsonPropertyName("BP2")]
        public object? BlockParameter2 { get; init; }

        [JsonPropertyName("BCI")]
        public required int[] ColorIndices { get; init; }

        [JsonPropertyName("BEI")]
        public object? BlockExtraInfo { get; init; }

        [JsonPropertyName("BlockData")]
        public required byte[] BlockData { get; init; }

        [JsonPropertyName("VehicleData")]
        public byte[]? VehicleData { get; init; }

        [JsonPropertyName("designChanged")]
        public bool DesignChanged { get; init; }

        [JsonPropertyName("blueprintVersion")]
        public int BlueprintVersion { get; init; } = 1;

        [JsonPropertyName("blueprintName")]
        public required string BlueprintName { get; init; }

        [JsonPropertyName("SerialisedInfo")]
        public required SerialisedInfoModel SerialisedInfo { get; init; }

        [JsonPropertyName("Name")]
        public string? LegacyName { get; init; }

        [JsonPropertyName("ItemNumber")]
        public int ItemNumber { get; init; }

        [JsonPropertyName("LocalPosition")]
        public required string LocalPosition { get; init; }

        [JsonPropertyName("LocalRotation")]
        public required string LocalRotation { get; init; }

        [JsonPropertyName("ForceId")]
        public int ForceId { get; init; }

        [JsonPropertyName("TotalBlockCount")]
        public int TotalBlockCount { get; init; }

        [JsonPropertyName("MaxCords")]
        public required string MaxCoordinates { get; init; }

        [JsonPropertyName("MinCords")]
        public required string MinCoordinates { get; init; }

        [JsonPropertyName("BlockIds")]
        public required int[] BlockIds { get; init; }

        [JsonPropertyName("BlockState")]
        public required string BlockState { get; init; }

        [JsonPropertyName("AliveCount")]
        public int AliveCount { get; init; }

        [JsonPropertyName("BlockStringData")]
        public required string[] BlockStringData { get; init; }

        [JsonPropertyName("BlockStringDataIds")]
        public required int[] BlockStringDataIds { get; init; }

        [JsonPropertyName("GameVersion")]
        public required string GameVersion { get; init; }

        [JsonPropertyName("PersistentSubObjectIndex")]
        public int PersistentSubObjectIndex { get; init; }

        [JsonPropertyName("PersistentBlockIndex")]
        public int PersistentBlockIndex { get; init; }

        [JsonPropertyName("AuthorDetails")]
        public required AuthorDetailsModel AuthorDetails { get; init; }

        [JsonPropertyName("BlockCount")]
        public int BlockCount { get; init; }
    }

    private sealed class SerialisedInfoModel
    {
        [JsonPropertyName("JsonDictionary")]
        public Dictionary<string, object> JsonDictionary { get; init; } = [];

        [JsonPropertyName("IsEmpty")]
        public bool IsEmpty { get; init; } = true;
    }

    private sealed class AuthorDetailsModel
    {
        [JsonPropertyName("Valid")]
        public bool Valid { get; init; }

        [JsonPropertyName("ForeignBlocks")]
        public int ForeignBlocks { get; init; }

        [JsonPropertyName("CreatorId")]
        public string CreatorId { get; init; } = Guid.Empty.ToString();

        [JsonPropertyName("ObjectId")]
        public string ObjectId { get; init; } = Guid.Empty.ToString();

        [JsonPropertyName("CreatorReadableName")]
        public string CreatorReadableName { get; init; } = string.Empty;

        [JsonPropertyName("HashV1")]
        public string HashV1 { get; init; } = string.Empty;
    }
}

internal static class BlueprintExportValidator
{
    public static void Validate(string json, int expectedBlockCount)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var blueprint = root.GetProperty("Blueprint");
        var arrays = new[] { "BLP", "BLR", "BCI", "BlockIds", "BlockState" };
        foreach (var name in arrays)
        {
            var value = blueprint.GetProperty(name);
            var count = name == "BlockState"
                ? ExpandRunLengthCount(value.GetString()!)
                : value.GetArrayLength();
            if (count != expectedBlockCount)
                throw new InvalidOperationException($"Blueprint writer produced a mismatched {name} array.");
        }

        var colors = blueprint.GetProperty("COL");
        if (colors.ValueKind != JsonValueKind.Array || colors.GetArrayLength() != 32)
            throw new InvalidOperationException("Blueprint writer must emit the game's 32-entry COL palette.");

        var specialInfo = blueprint.GetProperty("CSI");
        if (specialInfo.ValueKind != JsonValueKind.Array || specialInfo.GetArrayLength() != 80)
            throw new InvalidOperationException("Blueprint writer must emit the game's 80-entry CSI array.");

        var colorIndices = blueprint.GetProperty("BCI").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (colorIndices.Any(index => index < 0 || index >= colors.GetArrayLength()))
            throw new InvalidOperationException("A generated block references a color outside the COL palette.");

        if (blueprint.GetProperty("BlockCount").GetInt32() != expectedBlockCount ||
            blueprint.GetProperty("TotalBlockCount").GetInt32() != expectedBlockCount ||
            blueprint.GetProperty("AliveCount").GetInt32() != expectedBlockCount)
        {
            throw new InvalidOperationException("Blueprint writer produced inconsistent block totals.");
        }

        var itemDictionary = root.GetProperty("ItemDictionary");
        foreach (var id in blueprint.GetProperty("BlockIds").EnumerateArray())
        {
            if (!itemDictionary.TryGetProperty(id.GetInt32().ToString(CultureInfo.InvariantCulture), out _))
                throw new InvalidOperationException("A generated block references a missing item dictionary entry.");
        }
    }

    private static int ExpandRunLengthCount(string blockState)
    {
        if (blockState == "0")
            return 1;
        if (blockState.StartsWith("=0,", StringComparison.Ordinal) &&
            int.TryParse(blockState.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return count;
        }
        throw new InvalidOperationException("Blueprint writer produced an unsupported BlockState encoding.");
    }
}
