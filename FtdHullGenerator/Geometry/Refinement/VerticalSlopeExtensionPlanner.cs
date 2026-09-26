using System.Buffers.Binary;
using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Serialization.Decorations;

namespace FtdHullGenerator.Geometry.Refinement;

/// <summary>Explicit-length construction from the handmade Vertical recipe; never selects hull runs.</summary>
public sealed class VerticalSlopeExtensionPlanner
{
    // Five chunks occupy 79 bytes, plus the D02 26-byte record preamble. Bound input too,
    // including rejected/control requests, so an unbounded enumerable cannot grow diagnostics.
    public const int MaximumRequests = DecorationBinaryFormat.MaximumSegmentedPayloadBytes / 105;

    public NativeSlopeExtensionPlan Build(ShipGenerationSnapshot snapshot,
        IEnumerable<SlopeExtensionRequest> requests, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requests);
        token.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<DesignDiagnostic>();
        var extensions = ImmutableArray.CreateBuilder<NativeSlopeExtension>();
        var fingerprint = ShipCatalogFingerprint.Compute(snapshot.Catalog);
        if (snapshot.Diagnostics.IsDefault || snapshot.Diagnostics.Any(d => d.IsError) ||
            string.IsNullOrWhiteSpace(snapshot.Document.DocumentId) ||
            snapshot.Hull.SourceDocumentId != snapshot.Document.DocumentId ||
            snapshot.Hull.SourceRevision != snapshot.Revision ||
            snapshot.Hull.ResolvedCatalogVersion != snapshot.Catalog.GameVersion ||
            snapshot.Hull.ResolvedCatalogFingerprint != fingerprint)
        {
            Error(SlopeExtensionDiagnosticCodes.UnresolvedNativePlan,
                "Vertical extension requires the final matching document revision and resolved catalog.");
            return new([], diagnostics.ToImmutable());
        }
        var native = ImmutableArray.CreateBuilder<BlockPlacement>();
        var anchors = new Dictionary<(int, int, int), BlockPlacement>();
        var ambiguous = new HashSet<(int, int, int)>();
        foreach (var block in snapshot.Hull.Blocks)
        {
            token.ThrowIfCancellationRequested();
            native.Add(block);
            if (!anchors.TryAdd(block.Position, block)) ambiguous.Add(block.Position);
        }
        var source = new SlopeExtensionSourceIdentity(snapshot.Document.DocumentId, snapshot.Revision,
            fingerprint, native.ToImmutable());
        var seen = new HashSet<(int, int, int)>();
        var pending = new List<SlopeExtensionRequest>();
        var consumed = 0;
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            if (++consumed > MaximumRequests)
            {
                Error(SlopeExtensionDiagnosticCodes.PayloadLimit,
                    $"Vertical extension accepts at most {MaximumRequests} requests within the D02 payload budget.");
                return new([], diagnostics.ToImmutable(), source);
            }
            if (request is null)
            {
                Error(SlopeExtensionDiagnosticCodes.InvalidAnchor, "An extension request cannot be null.");
                continue;
            }
            pending.Add(request);
        }
        // Count native occupancy only at requested host cells. Memory is bounded by four cells
        // per request, and the final native list is traversed once rather than once per host.
        var hostOccupancy = new Dictionary<(int, int, int), int>();
        foreach (var request in pending)
        {
            token.ThrowIfCancellationRequested();
            if (request.VisualLengthMetres is < 5 or > 40 || request.Anchor.Shape != BlockShape.Slope4 ||
                request.Anchor.Rotation is not (4 or 6 or 12 or 14)) continue;
            foreach (var cell in request.Anchor.OccupiedCells) hostOccupancy.TryAdd(cell, 0);
        }
        if (hostOccupancy.Count > 0)
        foreach (var block in source.NativePlacements)
        {
            token.ThrowIfCancellationRequested();
            if (block.Rotation is < 0 or > 23)
            {
                Error(SlopeExtensionDiagnosticCodes.InvalidAnchor,
                    $"Native placement {block.Position} has invalid rotation {block.Rotation}; its occupied footprint cannot certify a host.");
                continue;
            }
            foreach (var cell in block.OccupiedCells)
                if (hostOccupancy.TryGetValue(cell, out var count)) hostOccupancy[cell] = Math.Min(2, count + 1);
        }
        foreach (var request in pending.OrderBy(r => r.Anchor.X).ThenBy(r => r.Anchor.Y)
                     .ThenBy(r => r.Anchor.Z).ThenBy(r => r.VisualLengthMetres))
        {
            token.ThrowIfCancellationRequested();
            var anchor = request.Anchor;
            if (!seen.Add(anchor.Position) || ambiguous.Contains(anchor.Position) ||
                !anchors.TryGetValue(anchor.Position, out var final) || final != anchor)
            {
                Error(SlopeExtensionDiagnosticCodes.InvalidAnchor,
                    $"Anchor {anchor.Position} is missing, duplicated, or differs from the final native placement.");
                continue;
            }
            var length = request.VisualLengthMetres;
            if (length is >= 1 and <= 4) continue; // Native controls are deliberately untouched.
            if (length is < 5 or > 40)
            {
                Error(SlopeExtensionDiagnosticCodes.UnsupportedLength, $"Visual length {length} is outside 5–40 m.");
                continue;
            }
            if (anchor.Shape != BlockShape.Slope4 || anchor.Origin != BlockOrigin.Smoothing ||
                anchor.ArmorDepth != 0 || anchor.UsePoles)
            {
                Error(SlopeExtensionDiagnosticCodes.InvalidAnchor, "Only a final native smoothing Slope4 can anchor an extension.");
                continue;
            }
            if (anchor.Rotation is not (4 or 6 or 12 or 14))
            {
                Error(SlopeExtensionDiagnosticCodes.UnsupportedOrientation,
                    $"Vertical rotation {anchor.Rotation} has no handmade extension mapping.");
                continue;
            }
            if (anchor.OccupiedCells.Any(cell => hostOccupancy[cell] != 1))
            {
                Error(SlopeExtensionDiagnosticCodes.InvalidAnchor,
                    $"Anchor {anchor.Position} does not exclusively own its native occupied footprint.");
                continue;
            }
            if (anchor.OccupiedCells.Any(cell => cell.X < snapshot.Hull.MinX || cell.X > snapshot.Hull.MaxX ||
                cell.Y < snapshot.Hull.MinY || cell.Y > snapshot.Hull.MaxY ||
                cell.Z < snapshot.Hull.MinZ || cell.Z > snapshot.Hull.MaxZ))
            {
                Error(SlopeExtensionDiagnosticCodes.InvalidAnchor, $"Anchor {anchor.Position} extends outside the final structural bounds.");
                continue;
            }
            if (anchor.Material is not (MaterialKind.Wood or MaterialKind.Metal or
                MaterialKind.LightweightAlloy or MaterialKind.HeavyArmor))
            {
                Error(SlopeExtensionDiagnosticCodes.MissingMaterialPart, $"Material {anchor.Material} has no supported extension evidence.");
                continue;
            }
            var rule = NativeSlopeExtensionRule.Calculate(length);
            var shape = rule.MeshLength switch { 1 => BlockShape.Slope1, 2 => BlockShape.Slope2,
                3 => BlockShape.Slope3, _ => BlockShape.Slope4 };
            var host = snapshot.Catalog.Resolve(anchor.Material, BlockShape.Slope4);
            var mesh = snapshot.Catalog.Resolve(anchor.Material, shape);
            if (host.IsFallback || mesh.IsFallback || host.Guid == Guid.Empty || mesh.Guid == Guid.Empty ||
                host.Shape != BlockShape.Slope4 || mesh.Shape != shape ||
                host.Material != anchor.Material || mesh.Material != anchor.Material)
            {
                Error(SlopeExtensionDiagnosticCodes.MissingMaterialPart,
                    $"The installed {anchor.Material} native host or {rule.MeshLength} m slope donor is unresolved; cube fallback is forbidden.");
                continue;
            }
            var (position, euler) = Transform(anchor.Rotation, rule.OffsetMagnitude);
            var tether = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(tether, anchor.X);
            BinaryPrimitives.WriteInt32LittleEndian(tether.AsSpan(4), anchor.Y);
            BinaryPrimitives.WriteInt32LittleEndian(tether.AsSpan(8), anchor.Z);
            var record = new DecorationRecord(extensions.Count, Guid.Empty,
            [new DecorationChunk(0, mesh.Guid.ToByteArray()),
             new DecorationChunk(1, Floats(new(1.001f, 1.001f, rule.LongitudinalScale))),
             new DecorationChunk(2, Floats(position)), new DecorationChunk(3, Floats(euler)),
             new DecorationChunk(5, tether)]);
            extensions.Add(new(new(anchor, host.Guid), SlopeExtensionKind.Vertical, length, record));
        }
        token.ThrowIfCancellationRequested();
        // D02 deliberately rejects unobserved exact continuation-word boundaries.
        if (extensions.Count > 0 && extensions.Count * 105 % ushort.MaxValue == 0)
            Error(SlopeExtensionDiagnosticCodes.PayloadLimit, "The generated payload hits an unsupported exact D02 continuation boundary; split the requested selection.");
        return new(diagnostics.Any(d => d.IsError) ? [] : extensions.ToImmutable(), diagnostics.ToImmutable(), source);

        void Error(string code, string message) => diagnostics.Add(DesignDiagnostic.Error(code, message,
            snapshot.Document.DocumentId, "VerticalSlopeExtension") with
            { SuggestedCorrection = "Use unique final native 4 m Vertical anchors with supported materials and explicit lengths 5–40, from the current resolved snapshot." });
    }

    private static (DecorationVector3 Position, DecorationVector3 Euler) Transform(int rotation, float d)
    {
        // Measured saved offset direction, before the lossy Euler serialization. Float32
        // multiplication reproduces every handmade 5–12 m residual; use the same rule through
        // 40 m. Recomputing from the raw 89.9802 Euler would change the independent offset field.
        var z = -0.9999997615814209f * d;
        var y = -0.9999998211860657f * d;
        var residual = 1.7881393432617188e-7f * d;
        return rotation switch
        {
            12 => (new(0, 0, d), new(0, 0, 180)),
            14 => (new(0, 0, z), new(0, 180, 180)),
            4 => (new(0, y, residual), new(Bits(0x42b3f5df), Bits(0xb6a8129b), 0)),
            6 => (new(0, y, residual), new(Bits(0x42b3f5df), 180, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        };
    }

    private static float Bits(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));
    private static byte[] Floats(DecorationVector3 vector)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, vector.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), vector.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), vector.Z);
        return bytes;
    }
}
