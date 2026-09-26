using System.Collections.Immutable;

namespace FtdHullGenerator.Domain.Decorations;

/// <summary>
/// One opaque chunk in a decoration-manager record. D02 preserves chunk keys, order and bytes
/// without assigning game or smoothing semantics to them.
/// </summary>
public sealed record DecorationChunk(ushort Key, ImmutableArray<byte> Payload)
{
    public DecorationChunk(ushort key, IEnumerable<byte> payload)
        : this(key, payload?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(payload)))
    {
    }
}

/// <summary>
/// Structural record framing for one decoration-manager entry. The preamble GUID and ordered
/// chunks are retained exactly; interpreting their meaning belongs to later verified work.
/// </summary>
public sealed record DecorationRecord(
    int DecorationId,
    Guid PreambleGuid,
    ImmutableArray<DecorationChunk> Chunks)
{
    public DecorationRecord(int decorationId, Guid preambleGuid, IEnumerable<DecorationChunk> chunks)
        : this(decorationId, preambleGuid,
            chunks?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(chunks)))
    {
    }
}

/// <summary>A typed, semantics-free representation of one AllConstructDecorations module.</summary>
public sealed record DecorationModule(ImmutableArray<DecorationRecord> Records)
{
    public DecorationModule(IEnumerable<DecorationRecord> records)
        : this(records?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(records)))
    {
    }

    public static DecorationModule Empty { get; } = new(ImmutableArray<DecorationRecord>.Empty);
}
