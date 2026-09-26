using System.Buffers.Binary;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;

namespace FtdHullGenerator.Serialization.Decorations;

/// <summary>
/// Deliberately conspicuous capability required for the unverified decoration export path.
/// Only controlled offline preservation evidence may opt in until a game load/re-save verifies it.
/// </summary>
internal sealed class UnverifiedDecorationExportCapability
{
    private UnverifiedDecorationExportCapability()
    {
    }

    internal static UnverifiedDecorationExportCapability ForControlledOfflinePreservationEvidenceOnly { get; } = new();
}

internal static class DecorationExportDiagnosticCodes
{
    public const string SnapshotMismatch = "DCE201";
}

internal sealed class DecorationExportValidationException : InvalidOperationException
{
    public DecorationExportValidationException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Immutable, semantics-free replacement captured against one exact export snapshot. The module
/// contains only generic record framing and opaque chunks; none of its bytes enter normal export.
/// </summary>
internal sealed class UnverifiedDecorationExportRequest
{
    private UnverifiedDecorationExportRequest(
        ShipGenerationSnapshot snapshot,
        DecorationModule replacement,
        byte[]? existingVehicleData)
    {
        SourceSnapshot = snapshot;
        SourceDocumentId = snapshot.Document.DocumentId;
        SourceRevision = snapshot.Revision;
        SourceCatalog = snapshot.Catalog;
        SourceCatalogFingerprint = ShipCatalogFingerprint.Compute(snapshot.Catalog);
        Replacement = replacement;
        ExistingVehicleData = existingVehicleData?.ToArray();
    }

    public ShipGenerationSnapshot SourceSnapshot { get; }
    public string SourceDocumentId { get; }
    public long SourceRevision { get; }
    public FtdBlockCatalog SourceCatalog { get; }
    public string SourceCatalogFingerprint { get; }
    public DecorationModule Replacement { get; }
    public byte[]? ExistingVehicleData { get; }

    public static UnverifiedDecorationExportRequest CaptureForControlledOfflinePreservationEvidence(
        ShipGenerationSnapshot snapshot,
        DecorationModule replacement,
        byte[]? existingVehicleData = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(replacement);
        return new UnverifiedDecorationExportRequest(snapshot, replacement, existingVehicleData);
    }
}

/// <summary>Locates and replaces a decoration manager without rewriting peer modules.</summary>
internal static class VehicleDataDecorationCodec
{
    private readonly record struct ModuleSlice(int Start, int Length, bool IsDecorations);

    public static byte[] ReplaceOrAppendForControlledOfflinePreservationEvidence(
        ShipGenerationSnapshot snapshot,
        UnverifiedDecorationExportRequest request,
        UnverifiedDecorationExportCapability? capability)
    {
        if (!ReferenceEquals(capability,
                UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly))
        {
            throw new InvalidOperationException(
                "Decoration export is unverified and requires the controlled offline-preservation capability.");
        }
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ValidateSnapshot(snapshot, request);

        var replacement = DecorationModuleWriter.Write(request.Replacement);
        var existing = request.ExistingVehicleData;
        if (existing is null || existing.Length == 0)
            return replacement;
        if (existing.Length > DecorationBinaryFormat.MaximumVehicleDataBytes)
            throw new InvalidDataException("VehicleData exceeds the defensive codec limit.");

        var modules = WalkModules(existing);
        var decorations = modules.Where(module => module.IsDecorations).ToArray();
        if (decorations.Length > 1)
            throw new InvalidDataException("VehicleData contains more than one decorations manager.");

        if (decorations.Length == 0)
        {
            var resultLength = checked(existing.Length + replacement.Length);
            if (resultLength > DecorationBinaryFormat.MaximumVehicleDataBytes)
                throw new InvalidDataException("The updated VehicleData exceeds the defensive codec limit.");
            var appended = new byte[resultLength];
            existing.CopyTo(appended, 0);
            replacement.CopyTo(appended, existing.Length);
            return appended;
        }

        var target = decorations[0];
        var updatedLength = checked(existing.Length - target.Length + replacement.Length);
        if (updatedLength > DecorationBinaryFormat.MaximumVehicleDataBytes)
            throw new InvalidDataException("The updated VehicleData exceeds the defensive codec limit.");
        var updated = new byte[updatedLength];
        existing.AsSpan(0, target.Start).CopyTo(updated);
        replacement.CopyTo(updated, target.Start);
        existing.AsSpan(target.Start + target.Length)
            .CopyTo(updated.AsSpan(target.Start + replacement.Length));
        return updated;
    }

    public static DecorationModule? Read(byte[] vehicleData)
    {
        ArgumentNullException.ThrowIfNull(vehicleData);
        var modules = WalkModules(vehicleData);
        var decorations = modules.Where(module => module.IsDecorations).ToArray();
        if (decorations.Length == 0)
            return null;
        if (decorations.Length > 1)
            throw new InvalidDataException("VehicleData contains more than one decorations manager.");
        return DecorationModuleReader.ReadExact(
            vehicleData.AsSpan(decorations[0].Start, decorations[0].Length)).Module;
    }

    private static void ValidateSnapshot(
        ShipGenerationSnapshot snapshot,
        UnverifiedDecorationExportRequest request)
    {
        var catalogFingerprint = ShipCatalogFingerprint.Compute(snapshot.Catalog);
        if (!ReferenceEquals(request.SourceSnapshot, snapshot) ||
            !ReferenceEquals(request.SourceCatalog, snapshot.Catalog) ||
            request.SourceRevision != snapshot.Revision ||
            !string.Equals(request.SourceDocumentId, snapshot.Document.DocumentId, StringComparison.Ordinal) ||
            !string.Equals(request.SourceCatalogFingerprint, catalogFingerprint, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Hull.ResolvedCatalogFingerprint, catalogFingerprint, StringComparison.Ordinal))
        {
            throw new DecorationExportValidationException(
                DecorationExportDiagnosticCodes.SnapshotMismatch,
                "The replacement was not captured from this exact final resolved snapshot/catalog.");
        }
    }

    private static IReadOnlyList<ModuleSlice> WalkModules(byte[] vehicleData)
    {
        if (vehicleData.Length > DecorationBinaryFormat.MaximumVehicleDataBytes)
            throw new InvalidDataException("VehicleData exceeds the defensive codec limit.");
        var modules = new List<ModuleSlice>();
        var cursor = 0;
        while (cursor < vehicleData.Length)
        {
            if (modules.Count >= DecorationBinaryFormat.MaximumVehicleDataModules)
                throw new InvalidDataException("VehicleData exceeds the bounded manager-module count.");
            if (vehicleData.Length - cursor < DecorationBinaryFormat.MinimumModulePrefixLength)
                throw new InvalidDataException($"VehicleData has a truncated module prefix at byte {cursor}.");
            var setId = ReadUInt32(vehicleData, cursor);
            if (ReadUInt32(vehicleData, cursor + 4) != 0)
                throw new InvalidDataException($"VehicleData module at byte {cursor} has a non-zero reserved field.");
            var headerLengthValue = ReadUInt32(vehicleData, cursor + 8);
            if (headerLengthValue > DecorationBinaryFormat.MaximumHeaderBytes)
                throw new InvalidDataException($"VehicleData module at byte {cursor} has an oversized header.");
            var headerLength = checked((int)headerLengthValue);
            var length = DecorationModuleReader.ReadSegmentedLength(
                vehicleData, cursor + DecorationBinaryFormat.GenericModuleFixedPrefixLength);
            var headerStart = length.NextOffset;
            var payloadStart = checked(headerStart + headerLength);
            var end = checked(payloadStart + length.Value);
            if (end > vehicleData.Length)
                throw new InvalidDataException($"VehicleData module at byte {cursor} has a truncated payload.");

            var isDecorationSet = setId == DecorationBinaryFormat.SetId;
            var isDecorations = isDecorationSet && HasDecorationIdentity(vehicleData, headerStart, headerLength);
            if (isDecorationSet && !isDecorations)
            {
                throw new InvalidDataException(
                    $"A module at byte {cursor} uses the decoration set id without the confirmed identity.");
            }
            if (length.Words.Length > 1 && !isDecorations)
            {
                throw new InvalidDataException(
                    $"A non-decoration module at byte {cursor} uses unobserved segmented framing.");
            }
            if (isDecorations)
                _ = DecorationModuleReader.ReadExact(vehicleData.AsSpan(cursor, end - cursor));
            modules.Add(new ModuleSlice(cursor, end - cursor, isDecorations));
            cursor = end;
        }
        return modules;
    }

    private static bool HasDecorationIdentity(byte[] source, int headerStart, int headerLength)
    {
        if (headerLength < DecorationBinaryFormat.FixedDecorationHeaderLength ||
            headerStart < 0 || headerStart > source.Length - headerLength)
            return false;
        return ReadUInt32(source, headerStart) == DecorationBinaryFormat.TypeKey &&
            source[headerStart + 4] == 0 && source[headerStart + 5] == 0 &&
            source[headerStart + 6] == 0 &&
            ReadUInt24(source, headerStart + 7) == DecorationBinaryFormat.ManagerId &&
            source[headerStart + 11] == 0;
    }

    private static uint ReadUInt32(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));

    private static uint ReadUInt24(byte[] source, int offset) =>
        (uint)(source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16);
}
