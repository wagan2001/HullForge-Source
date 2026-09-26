using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.Serialization.Decorations;

/// <summary>
/// D02 byte oracles. Fixture bytes are independently assembled from structural framing only; no
/// private game asset is committed and no decoration field is assigned rendering semantics.
/// </summary>
internal static class DecorationCodecTests
{
    private static readonly Guid FixturePreambleGuid =
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    public static void Run()
    {
        VerifyIndependentLiteralAndOpaqueChunks();
        VerifyEmptyIdsAndBounds();
        VerifyIndependentSegmentedAndSplitOffsetFixture();
        VerifyVehicleDataPreservationAndOrder();
        VerifyCorruptTruncatedAndOversizedFailures();
        VerifyExporterGateAndNormalNull();

        Console.WriteLine(
            "Decoration codec: independent opaque-chunk fixture, preamble GUID/LE framing, ids/offsets, " +
            "empty and segmented modules, preservation, corruption bounds and dark export gate passed.");
    }

    private static void VerifyIndependentLiteralAndOpaqueChunks()
    {
        // One record, id 0x010203, non-empty preamble GUID in .NET byte order, and three arbitrary
        // chunks (including a duplicate key and an empty payload). These bytes were assembled
        // independently of the production writer.
        var expected = LiteralFixture();
        Require(expected.Length == 76, "The independent literal fixture length changed.");
        var module = new DecorationModule([
            new DecorationRecord(0x010203, FixturePreambleGuid,
            [
                new DecorationChunk(0x1234, Hex("deadbeef")),
                new DecorationChunk(0xffff, []),
                new DecorationChunk(0x1234, Hex("aa55")),
            ]),
        ]);

        var encoded = DecorationModuleWriter.Write(module);
        Require(encoded.SequenceEqual(expected),
            $"Writer disagrees with the independent fixture. Actual={Convert.ToHexString(encoded)}");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(expected) == DecorationBinaryFormat.SetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(14)) == DecorationBinaryFormat.TypeKey,
            "The literal no longer pins little-endian module primitives.");
        Require(expected.AsSpan(38, 16).SequenceEqual(Hex("33221100554477668899aabbccddeeff")),
            "The literal no longer pins .NET GUID byte order for the structural preamble field.");

        var decoded = DecorationModuleReader.ReadExact(expected);
        var record = decoded.Module.Records.Single();
        Require(record.DecorationId == 0x010203 && record.PreambleGuid == FixturePreambleGuid,
            "Reader did not recover the structural record id/preamble GUID.");
        Require(record.Chunks.Length == 3 && record.Chunks[0].Key == 0x1234 &&
                record.Chunks[0].Payload.AsSpan().SequenceEqual(Hex("deadbeef")) &&
                record.Chunks[1].Key == 0xffff && record.Chunks[1].Payload.IsEmpty &&
                record.Chunks[2].Key == 0x1234 &&
                record.Chunks[2].Payload.AsSpan().SequenceEqual(Hex("aa55")),
            "Opaque chunk keys, order, duplicates, or payload bytes were interpreted or normalized.");
        Require(DecorationModuleWriter.Write(decoded.Module).SequenceEqual(expected),
            "The independent literal did not round-trip byte exactly.");
    }

    private static void VerifyEmptyIdsAndBounds()
    {
        var emptyExpected = Hex("9f710400000000000e00000000000f270000000000a0860100000000");
        var empty = DecorationModuleWriter.Write(DecorationModule.Empty);
        Require(empty.SequenceEqual(emptyExpected), "The empty manager no longer matches its 28-byte oracle.");
        Require(DecorationModuleReader.ReadExact(empty).Module.Records.IsEmpty,
            "The reader rejected the explicit empty manager.");

        var module = new DecorationModule([
            OpaqueRecord(1, 0x1001, Hex("01")),
            OpaqueRecord(5, 0x1002, Hex("0203")),
            OpaqueRecord(7, 0x1003, Hex("04")),
        ]);
        var encoded = DecorationModuleWriter.Write(module);
        Require(DecorationModuleReader.ReadExact(encoded).Module.Records
                .Select(record => record.DecorationId).SequenceEqual([1, 5, 7]),
            "Non-contiguous ids or their index offsets were not preserved.");

        ExpectInvalid(() => DecorationModuleWriter.Write(new DecorationModule([
            OpaqueRecord(4, 1, Hex("01")), OpaqueRecord(4, 2, Hex("02")),
        ])), "Duplicate ids must be rejected.");
        ExpectInvalid(() => DecorationModuleWriter.Write(new DecorationModule([
            OpaqueRecord(DecorationBinaryFormat.MaximumSplit24BitValue + 1, 1, Hex("01")),
        ])), "Ids outside unsigned 24-bit framing must be rejected.");
        ExpectInvalid(() => DecorationModuleWriter.Write(new DecorationModule([
            new DecorationRecord(1, Guid.Empty,
                [new DecorationChunk(1, new byte[byte.MaxValue + 1])]),
        ])), "Chunks outside byte-length framing must be rejected.");

        var sharedMaximumChunk = ImmutableArray.CreateRange(new byte[byte.MaxValue]);
        var oversizedRecord = new DecorationRecord(1, Guid.Empty,
            Enumerable.Range(0, 2600)
                .Select(index => new DecorationChunk((ushort)index, sharedMaximumChunk)));
        ExpectInvalid(() => DecorationModuleWriter.Write(new DecorationModule([oversizedRecord])),
            "An oversized record must be rejected during cumulative preflight.");
    }

    private static void VerifyIndependentSegmentedAndSplitOffsetFixture()
    {
        const int count = 1600;
        var independent = IndependentSegmentedModule(count);
        var decoded = DecorationModuleReader.ReadExact(independent);
        Require(decoded.PayloadLengthWords.SequenceEqual([
                ushort.MaxValue,
                checked((ushort)(decoded.ActualPayloadLength - ushort.MaxValue)),
            ]),
            "The reader did not decode the independent additive length words.");
        Require(decoded.Module.Records.Length == count &&
                decoded.Module.Records[^1].DecorationId == (count - 1) * 3,
            "The independent segmented fixture did not recover all structural records.");

        var model = new DecorationModule(Enumerable.Range(0, count).Select(index =>
            new DecorationRecord(index * 3, Guid.Empty,
            [
                new DecorationChunk(0x7777, RecordPayload(index)),
            ])));
        var produced = DecorationModuleWriter.Write(model);
        Require(produced.SequenceEqual(independent),
            "Writer disagrees with the independently constructed segmented/split-offset fixture.");

        var headerStart = 16;
        var lastEntry = headerStart + DecorationBinaryFormat.FixedDecorationHeaderLength +
            (count - 1) * DecorationBinaryFormat.IndexEntryLength;
        var expectedLastOffset = 26 * count + 15 * (count - 1);
        Require(produced[lastEntry + 3] == (byte)(expectedLastOffset >> 16) &&
                BinaryPrimitives.ReadUInt16LittleEndian(produced.AsSpan(lastEntry + 5)) ==
                    (ushort)(expectedLastOffset & 0xffff),
            "The independent fixture no longer exercises the split high-byte/low-u16 offset.");

        var tenWord = new DecorationModule(Enumerable.Range(0, 2100).Select(index =>
            new DecorationRecord(index, Guid.Empty,
            [
                new DecorationChunk(0x4321, Enumerable.Repeat((byte)(index & 0xff), 255)),
            ])));
        var tenWordBytes = DecorationModuleWriter.Write(tenWord);
        var tenWordDecoded = DecorationModuleReader.ReadExact(tenWordBytes);
        Require(tenWordDecoded.PayloadLengthWords.Length == 10 &&
                DecorationModuleWriter.Write(tenWordDecoded.Module).SequenceEqual(tenWordBytes),
            "The codec did not support the observed ten-word segmented-length bound.");

        var exactMultipleChunks = Enumerable.Range(0, 253)
            .Select(index => new DecorationChunk((ushort)index, new byte[255]))
            .Append(new DecorationChunk(253, new byte[232]));
        var exactMultiple = new DecorationModule([
            new DecorationRecord(0, Guid.Empty, exactMultipleChunks),
        ]);
        ExpectInvalid(() => DecorationModuleWriter.Write(exactMultiple),
            "The unobserved exact-65,535 payload terminator case must fail closed.");
    }

    private static void VerifyVehicleDataPreservationAndOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HullForge-D02-preserve-{Guid.NewGuid():N}");
        var before = ManualGenericModule(41, Hex("102030"), Hex("aabbccdd"));
        var after = ManualGenericModule(42, Hex("4050"), Hex("0102030405"));
        var oldDecoration = LiteralFixture();
        try
        {
            var fixture = CreateResolvedFixture(root);
            var replacement = new DecorationModule([
                OpaqueRecord(2, 0xabcd, Hex("010203")),
                OpaqueRecord(9, 0xdcba, Hex("04050607")),
            ]);
            var existing = before.Concat(oldDecoration).Concat(after).ToArray();
            var request = UnverifiedDecorationExportRequest.CaptureForControlledOfflinePreservationEvidence(
                fixture.Snapshot, replacement, existing);
            var updated = VehicleDataDecorationCodec.ReplaceOrAppendForControlledOfflinePreservationEvidence(
                fixture.Snapshot, request,
                UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly);
            Require(updated.AsSpan(0, before.Length).SequenceEqual(before) &&
                    updated.AsSpan(updated.Length - after.Length).SequenceEqual(after),
                "Replacement changed bytes or order of an unrelated VehicleData module.");
            Require(VehicleDataDecorationCodec.Read(updated)!.Records
                    .Select(record => record.DecorationId).SequenceEqual([2, 9]),
                "The replacement manager was not recovered.");

            var noDecoration = before.Concat(after).ToArray();
            var appendRequest = UnverifiedDecorationExportRequest
                .CaptureForControlledOfflinePreservationEvidence(
                    fixture.Snapshot, replacement, noDecoration);
            var appended = VehicleDataDecorationCodec.ReplaceOrAppendForControlledOfflinePreservationEvidence(
                fixture.Snapshot, appendRequest,
                UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly);
            Require(appended.AsSpan(0, noDecoration.Length).SequenceEqual(noDecoration),
                "Append changed or reordered existing VehicleData modules.");
            Require(VehicleDataDecorationCodec.Read(appended)!.Records.Length == 2,
                "The appended manager could not be read.");

            ExpectInvalidOperation(() =>
                VehicleDataDecorationCodec.ReplaceOrAppendForControlledOfflinePreservationEvidence(
                    fixture.Snapshot, request, null),
                "Codec mutation must fail closed without the explicit evidence capability.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void VerifyCorruptTruncatedAndOversizedFailures()
    {
        var fixture = LiteralFixture();
        ExpectInvalid(() => DecorationModuleReader.ReadExact(fixture[..^1]),
            "A truncated record must be rejected.");

        var oversizedHeader = fixture.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeader.AsSpan(8),
            DecorationBinaryFormat.MaximumHeaderBytes + 1u);
        ExpectInvalid(() => DecorationModuleReader.ReadExact(oversizedHeader),
            "An oversized header must be rejected before allocation.");

        var invalidOffset = fixture.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(invalidOffset.AsSpan(33), 0);
        ExpectInvalid(() => DecorationModuleReader.ReadExact(invalidOffset),
            "An index offset before the preamble must be rejected.");

        var invalidChunk = fixture.ToArray();
        invalidChunk[73] = 3;
        ExpectInvalid(() => DecorationModuleReader.ReadExact(invalidChunk),
            "A truncated opaque chunk must be rejected.");

        var duplicate = DecorationModuleWriter.Write(new DecorationModule([
            OpaqueRecord(3, 1, Hex("01")), OpaqueRecord(4, 2, Hex("02")),
        ]));
        duplicate[35] = 3;
        duplicate[36] = 0;
        duplicate[37] = 0;
        ExpectInvalid(() => DecorationModuleReader.ReadExact(duplicate),
            "Duplicate ids in an independently corrupted index must be rejected.");

        var saturatedUnknown = ManualSegmentedGenericModule(77, Hex("aa"), new byte[65536]);
        ExpectInvalid(() => VehicleDataDecorationCodec.Read(saturatedUnknown),
            "Segmented non-decoration framing is unobserved and must be rejected.");

        var exactMultipleTerminator = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(exactMultipleTerminator, DecorationBinaryFormat.SetId);
        BinaryPrimitives.WriteUInt32LittleEndian(exactMultipleTerminator.AsSpan(8), 14);
        BinaryPrimitives.WriteUInt16LittleEndian(exactMultipleTerminator.AsSpan(12), ushort.MaxValue);
        ExpectInvalid(() => DecorationModuleReader.ReadExact(exactMultipleTerminator),
            "The inferred zero terminator for an exact multiple must fail closed.");

        var tooManyContinuations = new byte[12 + 2 * DecorationBinaryFormat.MaximumLengthWordCount];
        BinaryPrimitives.WriteUInt32LittleEndian(tooManyContinuations, DecorationBinaryFormat.SetId);
        BinaryPrimitives.WriteUInt32LittleEndian(tooManyContinuations.AsSpan(8), 14);
        for (var offset = 12; offset < tooManyContinuations.Length; offset += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(tooManyContinuations.AsSpan(offset), ushort.MaxValue);
        ExpectInvalid(() => DecorationModuleReader.ReadExact(tooManyContinuations),
            "Continuation words must be bounded before looking for a terminator.");

        var oversized = new byte[DecorationBinaryFormat.MaximumVehicleDataBytes + 1];
        ExpectInvalid(() => VehicleDataDecorationCodec.Read(oversized),
            "Oversized VehicleData must be rejected before structural walking.");

        var excessiveModules = Enumerable.Range(0, DecorationBinaryFormat.MaximumVehicleDataModules + 1)
            .SelectMany(index => ManualGenericModule(checked((uint)(1000 + index)), [], []))
            .ToArray();
        ExpectInvalid(() => VehicleDataDecorationCodec.Read(excessiveModules),
            "The VehicleData module count must be bounded.");
    }

    private static void VerifyExporterGateAndNormalNull()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HullForge-D02-export-{Guid.NewGuid():N}");
        var blocked = root + "-blocked";
        try
        {
            var fixture = CreateResolvedFixture(root);
            var normal = new BlueprintExporter().Export(
                fixture.Snapshot, fixture.Document, fixture.Snapshot.Revision, root, "normal");
            using (var document = JsonDocument.Parse(File.ReadAllText(normal.FilePath)))
            {
                Require(document.RootElement.GetProperty("Blueprint").GetProperty("VehicleData").ValueKind ==
                        JsonValueKind.Null,
                    "Normal export must preserve VehicleData=null.");
            }

            var replacement = new DecorationModule([OpaqueRecord(7, 0x2468, Hex("123456"))]);
            var before = ManualGenericModule(301, Hex("0102"), Hex("aabb"));
            var after = ManualGenericModule(302, Hex("03"), Hex("ccddee"));
            var existing = before.Concat(LiteralFixture()).Concat(after).ToArray();
            var request = UnverifiedDecorationExportRequest
                .CaptureForControlledOfflinePreservationEvidence(
                    fixture.Snapshot, replacement, existing);

            ExpectInvalidOperation(() => new BlueprintExporter()
                    .ExportWithUnverifiedDecorationsForControlledOfflineEvidence(
                        fixture.Snapshot, fixture.Document, fixture.Snapshot.Revision,
                        blocked, "blocked", request, null),
                "The dark exporter path must reject a missing capability.");
            Require(!Directory.Exists(blocked), "A rejected evidence export created its destination.");

            var allowed = new BlueprintExporter().ExportWithUnverifiedDecorationsForControlledOfflineEvidence(
                fixture.Snapshot, fixture.Document, fixture.Snapshot.Revision,
                root, "allowed", request,
                UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly);
            using var allowedDocument = JsonDocument.Parse(File.ReadAllText(allowed.FilePath));
            var vehicleData = allowedDocument.RootElement.GetProperty("Blueprint")
                .GetProperty("VehicleData").GetBytesFromBase64();
            Require(vehicleData is not null &&
                    vehicleData.AsSpan(0, before.Length).SequenceEqual(before) &&
                    vehicleData.AsSpan(vehicleData.Length - after.Length).SequenceEqual(after) &&
                    VehicleDataDecorationCodec.Read(vehicleData)!.Records.Single().DecorationId == 7,
                "Authorized evidence export did not preserve peer bytes/order around the replacement.");

            ExpectInvalidOperation(() => new BlueprintExporter()
                    .ExportWithUnverifiedDecorationsForControlledOfflineEvidence(
                        fixture.Snapshot, fixture.Document, fixture.Snapshot.Revision + 1,
                        blocked, "stale", request,
                        UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly),
                "The evidence path bypassed current-revision validation.");

            var swapped = fixture.Snapshot with { Catalog = FtdBlockCatalog.Load(root) };
            ExpectDiagnostic(DecorationExportDiagnosticCodes.SnapshotMismatch, () =>
                VehicleDataDecorationCodec.ReplaceOrAppendForControlledOfflinePreservationEvidence(
                    swapped, request,
                    UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly));

            var divergentHull = fixture.Snapshot with { Hull = fixture.Snapshot.Hull with { } };
            Require(divergentHull.Document.DocumentId == fixture.Snapshot.Document.DocumentId &&
                    divergentHull.Revision == fixture.Snapshot.Revision &&
                    ReferenceEquals(divergentHull.Catalog, fixture.Snapshot.Catalog),
                "The divergent-snapshot regression did not retain document/revision/catalog identity.");
            ExpectDiagnostic(DecorationExportDiagnosticCodes.SnapshotMismatch, () =>
                VehicleDataDecorationCodec.ReplaceOrAppendForControlledOfflinePreservationEvidence(
                    divergentHull, request,
                    UnverifiedDecorationExportCapability.ForControlledOfflinePreservationEvidenceOnly));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(blocked)) Directory.Delete(blocked, true);
        }
    }

    private static DecorationRecord OpaqueRecord(int id, ushort key, byte[] payload) =>
        new(id, Guid.Empty, [new DecorationChunk(key, payload)]);

    private static byte[] RecordPayload(int index)
    {
        var result = Enumerable.Repeat((byte)0xa5, 12).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result, index);
        return result;
    }

    private static byte[] LiteralFixture() => Hex(
        "9f71040000000000150000002900" +
        "0f270000000000a0860100001a00" +
        "03020100001a00" +
        "00001033221100554477668899aabbccddeeff" +
        "01000403020100" +
        "341204deadbeefffff00341202aa55");

    private static byte[] IndependentSegmentedModule(int count)
    {
        var preambleLength = checked(26 * count);
        var payloadLength = checked(preambleLength + 15 * count);
        Require(payloadLength > ushort.MaxValue && payloadLength < 2 * ushort.MaxValue,
            "Independent helper expects exactly two length words.");
        var headerLength = checked(14 + 7 * count);
        var result = new byte[16 + headerLength + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result, DecorationBinaryFormat.SetId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)headerLength));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14),
            checked((ushort)(payloadLength - ushort.MaxValue)));

        var header = result.AsSpan(16, headerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, DecorationBinaryFormat.TypeKey);
        WriteUInt24(header[7..], DecorationBinaryFormat.ManagerId);
        header[10] = checked((byte)(preambleLength >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)preambleLength));
        for (var index = 0; index < count; index++)
        {
            var entry = header[(14 + 7 * index)..];
            WriteUInt24(entry, checked((uint)(index * 3)));
            var offset = checked(preambleLength + 15 * index);
            entry[3] = checked((byte)(offset >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[5..], (ushort)(offset & 0xffff));
        }

        var payload = result.AsSpan(16 + headerLength, payloadLength);
        var cursor = 0;
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload[cursor..], checked((ushort)(2 * index)));
            payload[cursor + 2] = 16;
            cursor += 19;
            BinaryPrimitives.WriteUInt16LittleEndian(payload[cursor..], checked((ushort)(2 * index + 1)));
            payload[cursor + 2] = 4;
            BinaryPrimitives.WriteInt32LittleEndian(payload[(cursor + 3)..], index * 3);
            cursor += 7;
        }
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload[cursor..], 0x7777);
            payload[cursor + 2] = 12;
            RecordPayload(index).CopyTo(payload[(cursor + 3)..]);
            cursor += 15;
        }
        Require(cursor == payload.Length, "Independent segmented fixture did not tile.");
        return result;
    }

    private static byte[] ManualGenericModule(uint setId, byte[] header, byte[] payload)
    {
        var result = new byte[14 + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, setId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)header.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), checked((ushort)payload.Length));
        header.CopyTo(result, 14);
        payload.CopyTo(result, 14 + header.Length);
        return result;
    }

    private static byte[] ManualSegmentedGenericModule(uint setId, byte[] header, byte[] payload)
    {
        var remainder = payload.Length - ushort.MaxValue;
        Require(remainder is > 0 and < ushort.MaxValue,
            "Segmented generic helper expects exactly two length words.");
        var result = new byte[16 + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, setId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)header.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), checked((ushort)remainder));
        header.CopyTo(result, 16);
        payload.CopyTo(result, 16 + header.Length);
        return result;
    }

    private static ResolvedFixture CreateResolvedFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets",
            "Mods", "Core_Structural"));
        var parameters = HullParameters.Default with
        {
            Length = 20,
            Width = 9,
            Height = 8,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var document = ShipDocument.FromLegacyParameters(parameters, "D02", "d02-preservation");
        var result = new ShipGenerationService().Generate(document, 17, FtdBlockCatalog.Load(root));
        Require(result.IsValid, string.Join(" | ", result.Diagnostics));
        return new ResolvedFixture(document, result.Snapshot!);
    }

    private static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value & 0xff);
        destination[1] = (byte)((value >> 8) & 0xff);
        destination[2] = (byte)(value >> 16);
    }

    private sealed record ResolvedFixture(ShipDocument Document, ShipGenerationSnapshot Snapshot);

    private static byte[] Hex(string text) => Convert.FromHexString(text);

    private static void ExpectInvalid(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void ExpectInvalidOperation(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void ExpectDiagnostic(string code, Action action)
    {
        try
        {
            action();
        }
        catch (DecorationExportValidationException exception) when (exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException($"Expected decoration diagnostic {code}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
