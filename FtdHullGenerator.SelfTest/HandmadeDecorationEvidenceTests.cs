using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Serialization.Decorations;

/// <summary>
/// Evidence-gated D01 checks for the two user-supplied handmade decoration blueprints.
///
/// Registered in the fast/full self-test profiles by the maintainer. The checks use immutable files under
/// self-test fixtures, never the user's Constructables directory.
/// </summary>
internal static class HandmadeDecorationEvidenceTests
{
    private const int DecorationsSetId = 291231;
    private const int FixedHeaderLength = 14;
    private const int IndexEntryLength = 7;
    private const string MetalSlope4Guid = "db9ed060-d556-435b-945c-19c923e233d3";

    public static void Run()
    {
        VerifyFixture("handmade_hfill_decorated.blueprint", expectedNativeBlocks: 204,
            expectedRecords: 32, expectedRotations: new HashSet<int> { 16, 17, 18, 19 });
        VerifyFixture("handmade_vfill_decorated.blueprint", expectedNativeBlocks: 3158,
            expectedRecords: 64, expectedRotations: new HashSet<int> { 4, 6, 12, 14 });
        Console.WriteLine(
            "Handmade decoration evidence: H/V source hashes, 96 D02 records, raw fields, " +
            "host rotations, mirror pairs and byte-identical re-encode passed.");
    }

    private static void VerifyFixture(
        string fileName,
        int expectedNativeBlocks,
        int expectedRecords,
        IReadOnlySet<int> expectedRotations)
    {
        var fixturePath = FindRepositoryFile(
            Path.Combine("FtdHullGenerator.SelfTest", "Fixtures", "HandmadeDecorations", fileName));
        var mappingPath = FindRepositoryFile(
            Path.Combine("FtdHullGenerator.SelfTest", "Fixtures", "HandmadeDecorations",
                "handmade-refinement-mapping.json"));
        var fixtureBytes = File.ReadAllBytes(fixturePath);
        var sourceHash = fileName == "handmade_hfill_decorated.blueprint"
            ? "9aade945a0ab872b89baa9c673a506d3f51b4d99e1d4cc296ddf630232f3d94e"
            : "33109ead3630e46a9f09f732d4d7b7eb333d6b2e2cad9ae73ea2cd26baf245f7";
        Require(Hash(fixtureBytes) == sourceHash,
            $"{fileName} differs from the independently supplied immutable oracle.");
        using var document = JsonDocument.Parse(fixtureBytes);
        var blueprint = document.RootElement.GetProperty("Blueprint");
        var vehicleData = Convert.FromBase64String(blueprint.GetProperty("VehicleData").GetString()!);
        var module = ReadDecorationModule(vehicleData);
        var decoded = DecorationModuleReader.ReadExact(module.Bytes);
        var records = decoded.Module.Records;

        using var mappingDocument = JsonDocument.Parse(File.ReadAllText(mappingPath));
        var fixtureMapping = mappingDocument.RootElement.GetProperty("fixtures")
            .EnumerateArray()
            .Single(item => item.GetProperty("fixture").GetString() == fileName);
        Require(Hash(fixtureBytes) == fixtureMapping.GetProperty("fixture_sha256").GetString(),
            $"{fileName} source hash differs from its provenance mapping.");
        Require(fixtureBytes.Length == fixtureMapping.GetProperty("fixture_bytes").GetInt32(),
            $"{fileName} source size differs from its provenance mapping.");
        Require(module.Bytes.Length > 0 && Hash(module.Bytes) ==
                fixtureMapping.GetProperty("decoration_module").GetProperty("module_sha256").GetString(),
            $"{fileName} decoration module hash differs from its mapping.");
        Require(decoded.Module.Records.Length == expectedRecords &&
                decoded.Module.Records.Length == fixtureMapping.GetProperty("records").GetArrayLength(),
            $"{fileName} decoration record count changed.");
        Require(blueprint.GetProperty("BlockCount").GetInt32() == expectedNativeBlocks,
            $"{fileName} native block count changed.");
        Require(DecorationModuleWriter.Write(decoded.Module).SequenceEqual(module.Bytes),
            $"{fileName} D02 decode/re-encode is not byte-identical.");

        var hostByPosition = NativeHosts(blueprint, document.RootElement.GetProperty("ItemDictionary"));
        var mirrorSum = fixtureMapping.GetProperty("mirror_sum").GetInt32();
        var recordsById = records.ToDictionary(record => record.DecorationId);
        var mappingRecords = fixtureMapping.GetProperty("records").EnumerateArray().ToArray();
        var observedRotations = new HashSet<int>();
        foreach (var (record, expected) in records.Zip(mappingRecords))
        {
            var id = expected.GetProperty("decoration_id").GetInt32();
            Require(record.DecorationId == id, $"{fileName} record id order changed at {id}.");
            var rawRecord = module.RecordBytes[record.DecorationId];
            Require(Convert.ToHexString(rawRecord).ToLowerInvariant() ==
                    expected.GetProperty("raw_record_hex").GetString(),
                $"{fileName} record {id} raw bytes changed.");
            var expectedKeys = expected.GetProperty("field_keys").EnumerateArray()
                .Select(value => value.GetInt32()).ToArray();
            Require(record.Chunks.Select(chunk => (int)chunk.Key).SequenceEqual(expectedKeys),
                $"{fileName} record {id} field order changed.");
            Require(expected.GetProperty("omitted_field_keys").EnumerateArray()
                        .Select(value => value.GetInt32()).SequenceEqual([4, 6, 7]),
                $"{fileName} record {id} stopped preserving the handmade omissions.");
            Require(record.Chunks.Length == 5 && record.Chunks.All(chunk =>
                    chunk.Key is 0 or 1 or 2 or 3 or 5),
                $"{fileName} record {id} has an unsupported or invented field.");
            var tether = ReadInt32x3(record.Chunks.Single(chunk => chunk.Key == 5).Payload);
            if (!hostByPosition.TryGetValue(tether, out var hosts) || hosts.Count != 1)
                throw new InvalidOperationException(
                    $"{fileName} record {id} tether {tether} does not map to exactly one native host.");
            var host = hosts[0];
            var expectedHost = expected.GetProperty("native_host");
            Require(host.Index == expectedHost.GetProperty("block_index").GetInt32() &&
                    host.Rotation == expectedHost.GetProperty("rotation").GetInt32() &&
                    host.Guid == expectedHost.GetProperty("part_guid").GetString() &&
                    host.Guid == MetalSlope4Guid,
                $"{fileName} record {id} host mapping changed.");
            observedRotations.Add(host.Rotation);
            var mirrorTether = expected.GetProperty("mirror_tether").EnumerateArray()
                .Select(value => value.GetInt32()).ToArray();
            Require(mirrorTether.Length == 3 && mirrorTether[0] == mirrorSum - tether.X &&
                    mirrorTether[1] == tether.Y && mirrorTether[2] == tether.Z,
                $"{fileName} record {id} mirror datum changed.");
            var partnerId = expected.GetProperty("mirrored_partner_decoration_id").GetInt32();
            Require(recordsById.ContainsKey(partnerId) &&
                    recordsById[partnerId].Chunks.Single(chunk => chunk.Key == 5).Payload
                        .AsSpan().SequenceEqual(Int32x3(mirrorTether)),
                $"{fileName} record {id} has no matching mirror host.");
            var meshGuid = new Guid(record.Chunks.Single(chunk => chunk.Key == 0).Payload.AsSpan());
            Require(meshGuid.ToString() == expected.GetProperty("source_part_guid").GetString(),
                $"{fileName} record {id} source mesh mapping changed.");
            foreach (var key in new[] { 1, 2, 3 })
            {
                var payload = record.Chunks.Single(chunk => chunk.Key == key).Payload;
                var expectedBits = expected.GetProperty("fields").GetProperty(key.ToString())
                    .GetProperty("float32_le_hex").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray();
                Require(payload.Length == 12 && Enumerable.Range(0, 3).All(index =>
                        Convert.ToHexString(payload.AsSpan(index * 4, 4)).ToLowerInvariant() == expectedBits[index]),
                    $"{fileName} record {id} field {key} float32 bits changed.");
            }
            var scale = record.Chunks.Single(chunk => chunk.Key == 1).Payload;
            var sourceLength = expected.GetProperty("source_mesh_length_metres").GetInt32();
            var expectedLength = expected.GetProperty("requested_visual_length_metres").GetInt32();
            Require(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(scale.AsSpan(8, 4)))
                    * sourceLength == expectedLength && expectedLength is >= 5 and <= 12,
                $"{fileName} record {id} requested length mapping changed.");
        }

        Require(observedRotations.SetEquals(expectedRotations),
            $"{fileName} native host rotations changed: {string.Join(',', observedRotations.Order())}.");
        Require(records.All(record =>
                record.Chunks.Single(chunk => chunk.Key == 0).Payload.Length == 16 &&
                record.Chunks.Single(chunk => chunk.Key == 1).Payload.Length == 12 &&
                record.Chunks.Single(chunk => chunk.Key == 2).Payload.Length == 12 &&
                record.Chunks.Single(chunk => chunk.Key == 3).Payload.Length == 12 &&
                record.Chunks.Single(chunk => chunk.Key == 5).Payload.Length == 12),
            $"{fileName} field payload lengths changed.");
    }

    private static Dictionary<(int X, int Y, int Z), List<NativeHost>> NativeHosts(
        JsonElement blueprint,
        JsonElement itemDictionary)
    {
        var guids = itemDictionary.EnumerateObject().ToDictionary(
            property => int.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture),
            property => property.Value.GetString()!.ToLowerInvariant());
        var positions = blueprint.GetProperty("BLP").EnumerateArray()
            .Select(value =>
            {
                var coordinates = value.GetString()!.Split(',').Select(int.Parse).ToArray();
                return (X: coordinates[0], Y: coordinates[1], Z: coordinates[2]);
            }).ToArray();
        var blockIds = blueprint.GetProperty("BlockIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var rotations = blueprint.GetProperty("BLR").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var result = new Dictionary<(int X, int Y, int Z), List<NativeHost>>();
        for (var index = 0; index < positions.Length; index++)
        {
            if (!guids.TryGetValue(blockIds[index], out var guid))
                throw new InvalidOperationException($"Native block {blockIds[index]} has no ItemDictionary GUID.");
            if (!result.TryGetValue(positions[index], out var hosts))
                result[positions[index]] = hosts = [];
            hosts.Add(new NativeHost(index, guid, rotations[index]));
        }
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln"))) return candidate;
        }
        throw new InvalidOperationException($"Could not locate '{relativePath}' from the test output directory.");
    }

    private static (byte[] Bytes, Dictionary<int, byte[]> RecordBytes) ReadDecorationModule(byte[] vehicleData)
    {
        var cursor = 0;
        while (cursor < vehicleData.Length)
        {
            var setId = BinaryPrimitives.ReadUInt32LittleEndian(vehicleData.AsSpan(cursor));
            var headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                vehicleData.AsSpan(cursor + 8)));
            var (payloadLength, lengthEnd) = ReadSegmentedLength(vehicleData, cursor + 12);
            var payloadStart = checked(lengthEnd + headerLength);
            var end = checked(payloadStart + payloadLength);
            if (setId != DecorationsSetId)
            {
                cursor = end;
                continue;
            }

            var header = vehicleData.AsSpan(lengthEnd, headerLength);
            var count = (header.Length - FixedHeaderLength) / IndexEntryLength;
            var payload = vehicleData.AsSpan(payloadStart, payloadLength);
            var recordBytes = new Dictionary<int, byte[]>();
            for (var index = 0; index < count; index++)
            {
                var entry = FixedHeaderLength + index * IndexEntryLength;
                var id = (int)(header[entry] | header[entry + 1] << 8 | header[entry + 2] << 16);
                var begin = (header[entry + 3] << 16) | BinaryPrimitives.ReadUInt16LittleEndian(header[(entry + 5)..]);
                var finish = index + 1 < count
                    ? ((header[entry + IndexEntryLength + 3] << 16) |
                       BinaryPrimitives.ReadUInt16LittleEndian(header[(entry + IndexEntryLength + 5)..]))
                    : payload.Length;
                recordBytes[id] = payload[begin..finish].ToArray();
            }
            return (vehicleData[cursor..end], recordBytes);
        }
        throw new InvalidDataException("VehicleData has no AllConstructDecorations module.");
    }

    private static (int Length, int NextOffset) ReadSegmentedLength(byte[] source, int offset)
    {
        var total = 0;
        while (true)
        {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset));
            offset += sizeof(ushort);
            total = checked(total + word);
            if (word != ushort.MaxValue) return (total, offset);
        }
    }

    private static (int X, int Y, int Z) ReadInt32x3(ImmutableArray<byte> payload)
    {
        return (BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4)));
    }

    private static byte[] Int32x3(IEnumerable<int> values)
    {
        var array = values.ToArray();
        var bytes = new byte[12];
        for (var index = 0; index < 3; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * 4, 4), array[index]);
        return bytes;
    }

    private sealed record NativeHost(int Index, string Guid, int Rotation);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
