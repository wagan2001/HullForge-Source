using System.Buffers.Binary;
using System.Collections.Immutable;
using FtdHullGenerator.Domain.Decorations;

namespace FtdHullGenerator.Serialization.Decorations;

/// <summary>
/// Structurally decodes one complete decorations-manager module while leaving every record chunk
/// semantically opaque.
/// </summary>
internal static class DecorationModuleReader
{
    internal sealed record DecodedModule(
        DecorationModule Module,
        ImmutableArray<ushort> PayloadLengthWords,
        int ActualPayloadLength)
    {
        public bool UsesSegmentedPayloadLength => PayloadLengthWords.Length > 1;
    }

    public static DecodedModule ReadExact(ReadOnlySpan<byte> module)
    {
        if (module.Length > DecorationBinaryFormat.MaximumVehicleDataBytes)
            throw new InvalidDataException("The decoration module exceeds the defensive VehicleData limit.");
        if (module.Length < DecorationBinaryFormat.MinimumModulePrefixLength)
            throw new InvalidDataException("The decoration module prefix is truncated.");
        if (ReadUInt32(module, 0) != DecorationBinaryFormat.SetId || ReadUInt32(module, 4) != 0)
            throw new InvalidDataException("The module does not have the confirmed decorations prefix.");

        var headerLengthValue = ReadUInt32(module, 8);
        if (headerLengthValue is < DecorationBinaryFormat.FixedDecorationHeaderLength
            or > DecorationBinaryFormat.MaximumHeaderBytes)
            throw new InvalidDataException("The decoration header length is outside codec bounds.");

        var headerLength = checked((int)headerLengthValue);
        var length = ReadSegmentedLength(module, DecorationBinaryFormat.GenericModuleFixedPrefixLength);
        var payloadStart = checked(length.NextOffset + headerLength);
        if (checked(payloadStart + length.Value) != module.Length)
            throw new InvalidDataException("The segmented decoration payload length does not match the module extent.");

        var header = module.Slice(length.NextOffset, headerLength);
        if (ReadUInt32(header, 0) != DecorationBinaryFormat.TypeKey ||
            header[4] != 0 || header[5] != 0 || header[6] != 0 ||
            ReadUInt24(header, 7) != DecorationBinaryFormat.ManagerId || header[11] != 0)
        {
            throw new InvalidDataException("The decorations module identity or fixed separators are invalid.");
        }

        var indexBytes = headerLength - DecorationBinaryFormat.FixedDecorationHeaderLength;
        if (indexBytes % DecorationBinaryFormat.IndexEntryLength != 0)
            throw new InvalidDataException("The decoration header is not 14 + 7*N bytes.");
        var count = indexBytes / DecorationBinaryFormat.IndexEntryLength;
        if (count > DecorationBinaryFormat.MaximumDecorationCount)
            throw new InvalidDataException("The decoration count exceeds the representable preamble bound.");

        var preambleLength = checked(count * DecorationBinaryFormat.PreambleBytesPerDecoration);
        var firstRecordOffset = checked((header[10] << 16) | ReadUInt16(header, 12));
        if (firstRecordOffset != preambleLength)
            throw new InvalidDataException("The first record offset does not equal the 26*N preamble length.");
        if (preambleLength > length.Value)
            throw new InvalidDataException("The decoration preamble is truncated.");

        var ids = new int[count];
        var offsets = new int[count];
        var uniqueIds = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var entry = DecorationBinaryFormat.FixedDecorationHeaderLength +
                index * DecorationBinaryFormat.IndexEntryLength;
            ids[index] = checked((int)ReadUInt24(header, entry));
            if (!uniqueIds.Add(ids[index]))
                throw new InvalidDataException($"Decoration id {ids[index]} is duplicated.");
            if (header[entry + 4] != 0)
                throw new InvalidDataException("A decoration index separator is non-zero.");

            offsets[index] = checked((header[entry + 3] << 16) | ReadUInt16(header, entry + 5));
            if (index == 0 && offsets[index] != preambleLength)
                throw new InvalidDataException("The first index offset disagrees with firstRecordOffset.");
            if (index > 0 && offsets[index] <= offsets[index - 1])
                throw new InvalidDataException("Decoration record offsets are not strictly ascending.");
            if (offsets[index] >= length.Value)
                throw new InvalidDataException("A decoration record offset lies outside the payload.");
        }

        if (count == 0 && length.Value != 0)
            throw new InvalidDataException("An empty decoration manager carries unexpected payload bytes.");

        var payload = module[payloadStart..];
        var preambleGuids = ReadPreamble(payload[..preambleLength], ids);
        var records = ImmutableArray.CreateBuilder<DecorationRecord>(count);
        for (var index = 0; index < count; index++)
        {
            var end = index + 1 < count ? offsets[index + 1] : length.Value;
            records.Add(new DecorationRecord(ids[index], preambleGuids[index],
                ReadOpaqueChunks(payload[offsets[index]..end], index)));
        }

        return new DecodedModule(
            new DecorationModule(records.MoveToImmutable()), length.Words, length.Value);
    }

    internal static SegmentedLength ReadSegmentedLength(ReadOnlySpan<byte> source, int offset)
    {
        var words = ImmutableArray.CreateBuilder<ushort>();
        var total = 0;
        while (words.Count < DecorationBinaryFormat.MaximumLengthWordCount)
        {
            var word = ReadUInt16(source, offset);
            offset = checked(offset + sizeof(ushort));
            words.Add(word);
            total = checked(total + word);
            if (total > DecorationBinaryFormat.MaximumSegmentedPayloadBytes)
                throw new InvalidDataException("A segmented payload length exceeds the observed bound.");
            if (word == ushort.MaxValue)
                continue;
            if (words.Count > 1 && word == 0)
            {
                throw new InvalidDataException(
                    "An exact 65,535-byte payload multiple has no observed terminator encoding.");
            }
            return new SegmentedLength(total, offset, words.ToImmutable());
        }
        throw new InvalidDataException("A segmented payload length has too many continuation words.");
    }

    internal readonly record struct SegmentedLength(
        int Value,
        int NextOffset,
        ImmutableArray<ushort> Words);

    private static ImmutableArray<Guid> ReadPreamble(ReadOnlySpan<byte> preamble, IReadOnlyList<int> ids)
    {
        var preambleGuids = ImmutableArray.CreateBuilder<Guid>(ids.Count);
        var cursor = 0;
        for (var index = 0; index < ids.Count; index++)
        {
            ReadChunkHeader(preamble, ref cursor, checked((ushort)(2 * index)), 16);
            preambleGuids.Add(new Guid(preamble.Slice(cursor, 16)));
            cursor += 16;
            ReadChunkHeader(preamble, ref cursor, checked((ushort)(2 * index + 1)), 4);
            if (ReadInt32(preamble, cursor) != ids[index])
                throw new InvalidDataException("The decoration preamble id disagrees with the index table.");
            cursor += 4;
        }
        if (cursor != preamble.Length)
            throw new InvalidDataException("The decoration preamble did not tile exactly.");
        return preambleGuids.MoveToImmutable();
    }

    private static ImmutableArray<DecorationChunk> ReadOpaqueChunks(
        ReadOnlySpan<byte> record,
        int recordIndex)
    {
        var chunks = ImmutableArray.CreateBuilder<DecorationChunk>();
        var cursor = 0;
        while (cursor < record.Length)
        {
            if (record.Length - cursor < 3)
                throw new InvalidDataException($"Decoration record {recordIndex} has a truncated chunk header.");
            var key = ReadUInt16(record, cursor);
            var payloadLength = record[cursor + 2];
            cursor += 3;
            if (record.Length - cursor < payloadLength)
                throw new InvalidDataException($"Decoration record {recordIndex} chunk {key} is truncated.");
            chunks.Add(new DecorationChunk(key, record.Slice(cursor, payloadLength).ToArray()));
            cursor += payloadLength;
        }
        return chunks.ToImmutable();
    }

    private static void ReadChunkHeader(
        ReadOnlySpan<byte> source,
        ref int cursor,
        ushort expectedKey,
        int expectedLength)
    {
        if (source.Length - cursor < 3 || ReadUInt16(source, cursor) != expectedKey ||
            source[cursor + 2] != expectedLength || source.Length - cursor - 3 < expectedLength)
            throw new InvalidDataException("The decoration preamble is malformed.");
        cursor += 3;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || offset > source.Length - sizeof(ushort))
            throw new InvalidDataException("A uint16 field is truncated.");
        return BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || offset > source.Length - 3)
            throw new InvalidDataException("A uint24 field is truncated.");
        return (uint)(source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || offset > source.Length - sizeof(uint))
            throw new InvalidDataException("A uint32 field is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || offset > source.Length - sizeof(int))
            throw new InvalidDataException("An int32 field is truncated.");
        return BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
    }
}
