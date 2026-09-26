using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Numerics;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Decorations;

/// <summary>The two evidenced native smoothing sources; not a general-purpose skin mode.</summary>
public enum SlopeExtensionKind { Vertical, Horizontal }

/// <summary>Explicit intent. Selecting a length from a contour is a separate planner operation.</summary>
public sealed record SlopeExtensionRequest(BlockPlacement Anchor, int VisualLengthMetres);

/// <summary>Full final native identity, including armor metadata and the resolved part GUID.</summary>
public sealed record ResolvedSlopeAnchor(BlockPlacement Placement, Guid PartGuid);

/// <summary>Float32 values as serialized by FtD. Signed zero and oracle residuals are retained.</summary>
public readonly record struct DecorationVector3(float X, float Y, float Z);

/// <summary>World-space visual envelope in metres; never an occupied-cell or structural bound.</summary>
public sealed record DecorationVisualBounds(
    double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ);

/// <summary>
/// One resolved extension. The record is the export payload; preview consumes these same resolved
/// fields. A native block is never replaced, hidden, reoriented or stretched by this contract.
/// </summary>
public sealed class NativeSlopeExtension
{
    public ResolvedSlopeAnchor Anchor { get; }
    public SlopeExtensionKind Kind { get; }
    public int RequestedLengthMetres { get; }
    public string RecipeId => NativeSlopeExtensionRule.RecipeId;
    public int RecipeVersion => NativeSlopeExtensionRule.RecipeVersion;
    public Guid MeshPartGuid { get; }
    public int SourceMeshLengthMetres { get; }
    public DecorationVector3 Scale { get; }
    public DecorationVector3 Position { get; }
    public DecorationVector3 EulerAngles { get; }
    public DecorationRecord Record { get; }
    public DecorationVisualBounds VisualBounds { get; }

    // Only evidenced planners construct these. Values for preview are decoded from the single
    // export record, never accepted as a second potentially inconsistent transform.
    internal NativeSlopeExtension(ResolvedSlopeAnchor anchor, SlopeExtensionKind kind, int length,
        DecorationRecord record)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(record);
        var rule = NativeSlopeExtensionRule.Calculate(length);
        if (!Enum.IsDefined(kind) || anchor.PartGuid == Guid.Empty ||
            anchor.Placement.Shape != BlockShape.Slope4 || anchor.Placement.Origin != BlockOrigin.Smoothing ||
            anchor.Placement.Rotation is < 0 or > 23 || !Enum.IsDefined(anchor.Placement.Material))
            throw new ArgumentException("An extension requires a resolved native smoothing Slope4 anchor.");
        ushort[] keys = [0, 1, 2, 3, 5];
        int[] sizes = [16, 12, 12, 12, 12];
        if (record.Chunks.IsDefault || record.Chunks.Length != keys.Length || record.PreambleGuid != Guid.Empty ||
            record.DecorationId is < 0 or > 0xFFFFFF)
            throw new ArgumentException("The extension record does not match the evidenced field contract.");
        for (var i = 0; i < keys.Length; i++)
            if (record.Chunks[i].Key != keys[i] || record.Chunks[i].Payload.IsDefault ||
                record.Chunks[i].Payload.Length != sizes[i])
                throw new ArgumentException("The extension must preserve the evidenced fields and omissions.");
        MeshPartGuid = new Guid(record.Chunks[0].Payload.AsSpan());
        Scale = ReadVector(1);
        Position = ReadVector(2);
        EulerAngles = ReadVector(3);
        var tether = record.Chunks[4].Payload.AsSpan();
        if (MeshPartGuid == Guid.Empty ||
            BitConverter.SingleToInt32Bits(Scale.X) != BitConverter.SingleToInt32Bits(1.001f) ||
            BitConverter.SingleToInt32Bits(Scale.Y) != BitConverter.SingleToInt32Bits(1.001f) ||
            BitConverter.SingleToInt32Bits(Scale.Z) != BitConverter.SingleToInt32Bits(rule.LongitudinalScale) ||
            BinaryPrimitives.ReadInt32LittleEndian(tether) != anchor.Placement.X ||
            BinaryPrimitives.ReadInt32LittleEndian(tether[4..]) != anchor.Placement.Y ||
            BinaryPrimitives.ReadInt32LittleEndian(tether[8..]) != anchor.Placement.Z)
            throw new ArgumentException("The extension record disagrees with its length or native anchor.");
        Anchor = anchor;
        Kind = kind;
        RequestedLengthMetres = length;
        SourceMeshLengthMetres = rule.MeshLength;
        Record = record;
        VisualBounds = ComputeBounds();

        DecorationVector3 ReadVector(int index)
        {
            var bytes = record.Chunks[index].Payload.AsSpan();
            var result = new DecorationVector3(BinaryPrimitives.ReadSingleLittleEndian(bytes),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
            if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
                throw new ArgumentException("Decoration transforms must contain finite float32 values.");
            return result;
        }
    }

    /// <summary>Applies scale, then Euler Z/X/Y, then the tether-relative offset, in metres.</summary>
    public Vector3 TransformMeshPoint(Vector3 point)
    {
        var angles = EulerAngles;
        var rotation = Quaternion.CreateFromYawPitchRoll(angles.Y * MathF.PI / 180f,
            angles.X * MathF.PI / 180f, angles.Z * MathF.PI / 180f);
        var transformed = Vector3.Transform(point * new Vector3(Scale.X, Scale.Y, Scale.Z), rotation);
        return transformed + new Vector3(Position.X + Anchor.Placement.X,
            Position.Y + Anchor.Placement.Y, Position.Z + Anchor.Placement.Z);
    }

    private DecorationVisualBounds ComputeBounds()
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        // Conservative transformed mesh-box envelope, independent of native occupied cells.
        foreach (var x in new[] { -.5f, .5f })
        foreach (var y in new[] { -.5f, .5f })
        foreach (var z in new[] { -.5f, SourceMeshLengthMetres - .5f })
        {
            var point = TransformMeshPoint(new Vector3(x, y, z));
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return new(min.X, max.X, min.Y, max.Y, min.Z, max.Z);
    }
}

/// <summary>A frozen identity of the revision, catalog and exact native placements used by a planner.</summary>
public sealed record SlopeExtensionSourceIdentity(string DocumentId, long Revision,
    string CatalogFingerprint, ImmutableArray<BlockPlacement> NativePlacements);

/// <summary>
/// Immutable planner output tied to the final resolved native plan, before D05 attaches it to an
/// editor revision. Errors remain explicit; no partial result with errors is export-authoritative.
/// </summary>
public sealed record NativeSlopeExtensionPlan(
    ImmutableArray<NativeSlopeExtension> Extensions,
    ImmutableArray<DesignDiagnostic> Diagnostics,
    SlopeExtensionSourceIdentity? Source = null)
{
    public bool IsValid => !Extensions.IsDefault && !Diagnostics.IsDefault &&
        !Diagnostics.Any(diagnostic => diagnostic.IsError) &&
        (Extensions.IsEmpty || Source is { NativePlacements.IsDefault: false } &&
            !string.IsNullOrWhiteSpace(Source.DocumentId) && !string.IsNullOrWhiteSpace(Source.CatalogFingerprint));

    public static NativeSlopeExtensionPlan Empty { get; } = new([], []);
}

/// <summary>
/// User-authorized 2026-09-13 construction arithmetic. This does not select a hull run, infer a
/// rotation, or turn the former persisted refinement limit into a supported recipe.
/// </summary>
public static class NativeSlopeExtensionRule
{
    public const string RecipeId = "handmade-native-4m-extension";
    public const int RecipeVersion = 1;
    public const int MinimumLengthMetres = 5;
    public const int MaximumLengthMetres = 40;

    public static (int MeshLength, float LongitudinalScale, float OffsetMagnitude) Calculate(int length)
    {
        if (length is < MinimumLengthMetres or > MaximumLengthMetres)
            throw new ArgumentOutOfRangeException(nameof(length), length,
                "A native 4 m slope can be visually extended to an integer length from 5 through 40 m.");
        var meshLength = (length + 9) / 10;
        var scale = (float)length / meshLength;
        return (meshLength, scale, (scale - 1f) / 2f);
    }
}

public static class SlopeExtensionDiagnosticCodes
{
    public const string InvalidAnchor = "DEC101";
    public const string UnsupportedOrientation = "DEC102";
    public const string UnsupportedLength = "DEC103";
    public const string MissingMaterialPart = "DEC104";
    public const string UnresolvedNativePlan = "DEC105";
    public const string SelectionUnavailable = "DEC106";
    public const string PayloadLimit = "DEC107";
    public const string UnsupportedRecipe = "DEC108";
}
