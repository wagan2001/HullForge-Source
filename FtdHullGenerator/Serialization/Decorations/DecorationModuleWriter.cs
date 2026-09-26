using System.Buffers.Binary;
using FtdHullGenerator.Domain.Decorations;

namespace FtdHullGenerator.Serialization.Decorations;

/// <summary>Writes one complete decorations-manager module from semantics-free record framing.</summary>
internal static class DecorationModuleWriter
{
    public static byte[] Write(DecorationModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.Records.IsDefault)
            throw new InvalidDataException("The decoration record collection is uninitialized.");
        if (module.Records.Length > DecorationBinaryFormat.MaximumDecorationCount)
        {
            throw new InvalidDataException(
                $"A decoration module cannot contain more than {DecorationBinaryFormat.MaximumDecorationCount} " +
                "records within the observed segmented-length bound.");
        }

        var preambleLength = checked(DecorationBinaryFormat.PreambleBytesPerDecoration * module.Records.Length);
        if (preambleLength > DecorationBinaryFormat.MaximumSplit24BitValue ||
            preambleLength > DecorationBinaryFormat.MaximumSegmentedPayloadBytes)
        {
            throw new InvalidDataException(
                "The decoration preamble exceeds the bounded payload or split-offset framing.");
        }

        var ids = new HashSet<int>();
        var recordsLength = 0;
        for (var index = 0; index < module.Records.Length; index++)
        {
            var record = module.Records[index] ??
                throw new InvalidDataException($"Decoration record {index} is null.");
            if (record.DecorationId is < 0 or > DecorationBinaryFormat.MaximumSplit24BitValue)
                throw new InvalidDataException($"Decoration id {record.DecorationId} does not fit unsigned 24-bit framing.");
            if (!ids.Add(record.DecorationId))
                throw new InvalidDataException($"Decoration id {record.DecorationId} is duplicated.");
            var recordLength = MeasureOpaqueChunks(record, index);
            if (recordLength == 0)
                throw new InvalidDataException($"Decoration record {index} is empty.");
            if (recordLength > DecorationBinaryFormat.MaximumSegmentedPayloadBytes -
                    preambleLength - recordsLength)
            {
                throw new InvalidDataException(
                    "The decoration records exceed the observed segmented-length bound.");
            }
            recordsLength += recordLength;
        }

        var payloadLength = preambleLength + recordsLength;
        var payload = new List<byte>(payloadLength);
        var scratch = new byte[16];
        for (var index = 0; index < module.Records.Length; index++)
        {
            Array.Clear(scratch);
            module.Records[index].PreambleGuid.TryWriteBytes(scratch);
            WriteChunk(payload, checked((ushort)(2 * index)), scratch);
            BinaryPrimitives.WriteInt32LittleEndian(scratch, module.Records[index].DecorationId);
            WriteChunk(payload, checked((ushort)(2 * index + 1)), scratch.AsSpan(0, 4));
        }

        var offsets = new int[module.Records.Length];
        for (var index = 0; index < module.Records.Length; index++)
        {
            if (payload.Count > DecorationBinaryFormat.MaximumSplit24BitValue)
                throw new InvalidDataException($"Decoration record {index} starts beyond split 24-bit framing.");
            offsets[index] = payload.Count;
            WriteOpaqueChunks(payload, module.Records[index]);
        }

        if (payload.Count != payloadLength)
            throw new InvalidDataException("The decoration payload length changed after bounded preflight.");
        if (payload.Count > 0 && payload.Count % ushort.MaxValue == 0)
        {
            throw new InvalidDataException(
                "Exact 65,535-byte payload multiples have no observed terminating length word.");
        }

        var headerLength = checked(DecorationBinaryFormat.FixedDecorationHeaderLength +
            DecorationBinaryFormat.IndexEntryLength * module.Records.Length);
        if (headerLength > DecorationBinaryFormat.MaximumHeaderBytes)
            throw new InvalidDataException("The decoration header exceeds the defensive codec limit.");

        var lengthWordCount = payload.Count / ushort.MaxValue + 1;
        if (lengthWordCount > DecorationBinaryFormat.MaximumLengthWordCount)
            throw new InvalidDataException("The decoration payload requires too many segmented length words.");
        var headerStart = checked(DecorationBinaryFormat.GenericModuleFixedPrefixLength +
            sizeof(ushort) * lengthWordCount);
        var moduleLength = checked(headerStart + headerLength + payload.Count);
        if (moduleLength > DecorationBinaryFormat.MaximumVehicleDataBytes)
            throw new InvalidDataException("The decoration module exceeds the defensive VehicleData limit.");

        var result = new byte[moduleLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result, DecorationBinaryFormat.SetId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)headerLength));
        var cursor = DecorationBinaryFormat.GenericModuleFixedPrefixLength;
        var remaining = payload.Count;
        while (remaining >= ushort.MaxValue)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(cursor), ushort.MaxValue);
            cursor += sizeof(ushort);
            remaining -= ushort.MaxValue;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(cursor), checked((ushort)remaining));

        var header = result.AsSpan(headerStart, headerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, DecorationBinaryFormat.TypeKey);
        WriteUInt24(header, 7, DecorationBinaryFormat.ManagerId);
        header[10] = checked((byte)(preambleLength >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)(preambleLength & 0xffff));
        for (var index = 0; index < module.Records.Length; index++)
        {
            var entry = DecorationBinaryFormat.FixedDecorationHeaderLength +
                index * DecorationBinaryFormat.IndexEntryLength;
            WriteUInt24(header, entry, checked((uint)module.Records[index].DecorationId));
            header[entry + 3] = checked((byte)(offsets[index] >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(header[(entry + 5)..], (ushort)(offsets[index] & 0xffff));
        }
        payload.CopyTo(result, headerStart + headerLength);
        return result;
    }

    private static int MeasureOpaqueChunks(DecorationRecord record, int recordIndex)
    {
        if (record.Chunks.IsDefault)
            throw new InvalidDataException($"Decoration record {recordIndex} has an uninitialized chunk collection.");
        var length = 0;
        for (var index = 0; index < record.Chunks.Length; index++)
        {
            var chunk = record.Chunks[index] ??
                throw new InvalidDataException($"Decoration record {recordIndex} chunk {index} is null.");
            if (chunk.Payload.IsDefault)
                throw new InvalidDataException($"Decoration record {recordIndex} chunk {index} has uninitialized bytes.");
            if (chunk.Payload.Length > byte.MaxValue)
                throw new InvalidDataException($"Decoration record {recordIndex} chunk {index} exceeds byte-length framing.");
            var chunkLength = 3 + chunk.Payload.Length;
            if (length > DecorationBinaryFormat.MaximumSegmentedPayloadBytes - chunkLength)
            {
                throw new InvalidDataException(
                    $"Decoration record {recordIndex} exceeds the observed segmented-length bound.");
            }
            length += chunkLength;
        }
        return length;
    }

    private static void WriteOpaqueChunks(List<byte> destination, DecorationRecord record)
    {
        foreach (var chunk in record.Chunks)
            WriteChunk(destination, chunk.Key, chunk.Payload.AsSpan());
    }

    private static void WriteChunk(List<byte> destination, ushort key, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > byte.MaxValue)
            throw new InvalidDataException("A decoration chunk exceeds byte-length framing.");
        destination.Add((byte)(key & 0xff));
        destination.Add((byte)(key >> 8));
        destination.Add(checked((byte)payload.Length));
        foreach (var value in payload)
            destination.Add(value);
    }

    private static void WriteUInt24(Span<byte> destination, int offset, uint value)
    {
        if (value > DecorationBinaryFormat.MaximumSplit24BitValue)
            throw new InvalidDataException("A value does not fit unsigned 24-bit framing.");
        destination[offset] = (byte)(value & 0xff);
        destination[offset + 1] = (byte)((value >> 8) & 0xff);
        destination[offset + 2] = (byte)(value >> 16);
    }
}
