using System.Buffers.Binary;
using System.Collections.Immutable;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Serialization.Decorations;

namespace FtdHullGenerator.Geometry.Refinement;

/// <summary>Constructs explicitly requested visual extensions of final native horizontal slopes.
/// It does not select lengths or modify the native hull. Recipe 1 retains handmade float bits.</summary>
public sealed class HorizontalSlopeExtensionPlanner
{
    // Each record has 79 chunk bytes plus 26 preamble bytes in D02's segmented payload.
    public const int MaximumRequests = DecorationBinaryFormat.MaximumSegmentedPayloadBytes / 105;

    public NativeSlopeExtensionPlan Build(ShipGenerationSnapshot snapshot,
        IEnumerable<SlopeExtensionRequest> requests, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requests);
        token.ThrowIfCancellationRequested();
        var hull = snapshot.Hull;
        var fingerprint = ShipCatalogFingerprint.Compute(snapshot.Catalog);
        if (snapshot.Diagnostics.IsDefault || snapshot.Diagnostics.Any(d => d.IsError) ||
            string.IsNullOrWhiteSpace(snapshot.Document.DocumentId) ||
            hull.SourceDocumentId != snapshot.Document.DocumentId || hull.SourceRevision != snapshot.Revision ||
            hull.ResolvedCatalogVersion != snapshot.Catalog.GameVersion ||
            hull.ResolvedCatalogFingerprint != fingerprint)
            return Error(SlopeExtensionDiagnosticCodes.UnresolvedNativePlan,
                "Generate a valid final native snapshot with matching document, revision and catalog before refinement.");

        var pending = new List<SlopeExtensionRequest>();
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            if (pending.Count == MaximumRequests)
                return Error(SlopeExtensionDiagnosticCodes.PayloadLimit, "Horizontal requests exceed the D02 payload budget.");
            if (request is null)
                return Error(SlopeExtensionDiagnosticCodes.InvalidAnchor, "An extension request is null.");
            pending.Add(request);
        }

        var native = ImmutableArray.CreateBuilder<BlockPlacement>(hull.Blocks.Count);
        var membership = new Dictionary<BlockPlacement, int>();
        var positions = new Dictionary<(int, int, int), int>();
        foreach (var block in hull.Blocks)
        {
            token.ThrowIfCancellationRequested();
            native.Add(block);
            membership[block] = membership.GetValueOrDefault(block) + 1;
            positions[block.Position] = positions.GetValueOrDefault(block.Position) + 1;
        }
        var source = new SlopeExtensionSourceIdentity(snapshot.Document.DocumentId, snapshot.Revision,
            fingerprint, native.ToImmutable());
        // Only track cells requested as anchors, rather than allocating a second hull-sized
        // occupancy model. A forged or stale native list must not authorize overlapping hosts.
        var anchorCells = new Dictionary<(int, int, int), int>();
        foreach (var request in pending)
            if (request.Anchor.Shape == BlockShape.Slope4 && request.Anchor.Rotation is >= 16 and <= 19)
                foreach (var cell in request.Anchor.OccupiedCells) anchorCells.TryAdd(cell, 0);
        foreach (var block in source.NativePlacements)
        {
            token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(block.Shape) || block.Rotation is < 0 or > 23)
                return Error(SlopeExtensionDiagnosticCodes.UnresolvedNativePlan,
                    "The final native plan contains an invalid shape or rotation.");
            foreach (var cell in block.OccupiedCells)
                if (anchorCells.TryGetValue(cell, out var count)) anchorCells[cell] = count + 1;
        }
        var diagnostics = ImmutableArray.CreateBuilder<DesignDiagnostic>();
        var extensions = ImmutableArray.CreateBuilder<NativeSlopeExtension>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var request in pending.OrderBy(r => r.Anchor.X).ThenBy(r => r.Anchor.Y)
                     .ThenBy(r => r.Anchor.Z).ThenBy(r => r.VisualLengthMetres))
        {
            token.ThrowIfCancellationRequested();
            var anchor = request.Anchor;
            if (!seen.Add(anchor.Position))
            {
                Reject(SlopeExtensionDiagnosticCodes.InvalidAnchor, "Duplicate extension requests for one native anchor are ambiguous.");
                continue;
            }
            if (!membership.TryGetValue(anchor, out var count) || count != 1 || positions[anchor.Position] != 1)
            {
                Reject(SlopeExtensionDiagnosticCodes.InvalidAnchor, "The exact anchor and its metadata must occur once in the final native plan.");
                continue;
            }
            if (request.VisualLengthMetres is >= 1 and <= 4) continue;
            if (request.VisualLengthMetres is < 5 or > 40)
            {
                Reject(SlopeExtensionDiagnosticCodes.UnsupportedLength, "Visual extension lengths must be integers from 5 through 40 m.");
                continue;
            }
            if (anchor.Shape != BlockShape.Slope4 || anchor.Origin != BlockOrigin.Smoothing ||
                anchor.ArmorDepth != 0 || anchor.UsePoles)
            {
                Reject(SlopeExtensionDiagnosticCodes.InvalidAnchor, "A visual extension needs an exposed native smoothing Slope4 anchor.");
                continue;
            }
            if (anchor.Rotation is < 16 or > 19)
            {
                Reject(SlopeExtensionDiagnosticCodes.UnsupportedOrientation, "Horizontal recipe 1 supports native rotations 16, 17, 18 and 19 only.");
                continue;
            }
            if (anchor.OccupiedCells.Any(c => c.X < hull.MinX || c.X > hull.MaxX ||
                    c.Y < hull.MinY || c.Y > hull.MaxY || c.Z < hull.MinZ || c.Z > hull.MaxZ))
            {
                Reject(SlopeExtensionDiagnosticCodes.InvalidAnchor, "The native anchor footprint lies outside final structural bounds.");
                continue;
            }
            if (anchor.OccupiedCells.Any(c => anchorCells[c] != 1))
            {
                Reject(SlopeExtensionDiagnosticCodes.InvalidAnchor, "The native anchor overlaps another final placement.");
                continue;
            }
            if (anchor.Material is not (MaterialKind.Wood or MaterialKind.Metal or
                    MaterialKind.LightweightAlloy or MaterialKind.HeavyArmor))
            {
                Reject(SlopeExtensionDiagnosticCodes.MissingMaterialPart, "This material has no evidenced horizontal extension recipe.");
                continue;
            }
            var rule = NativeSlopeExtensionRule.Calculate(request.VisualLengthMetres);
            var shape = rule.MeshLength switch { 1 => BlockShape.Slope1, 2 => BlockShape.Slope2,
                3 => BlockShape.Slope3, _ => BlockShape.Slope4 };
            var host = snapshot.Catalog.Resolve(anchor.Material, BlockShape.Slope4);
            var mesh = snapshot.Catalog.Resolve(anchor.Material, shape);
            if (host.IsFallback || mesh.IsFallback || host.Shape != BlockShape.Slope4 || mesh.Shape != shape ||
                host.Material != anchor.Material || mesh.Material != anchor.Material ||
                host.Guid == Guid.Empty || mesh.Guid == Guid.Empty)
            {
                Reject(SlopeExtensionDiagnosticCodes.MissingMaterialPart, "Resolve both native host and donor slope from the installed catalog without cube fallback.");
                continue;
            }
            var negative = anchor.Rotation is 17 or 19;
            // The saved negative-Z direction carries a float32 residual. Retain that measured
            // coefficient at every length rather than normalizing it or recomputing Euler trig.
            var offset = rule.OffsetMagnitude * (negative ? -0.9999997615814209f : 1f);
            var angles = anchor.Rotation switch
            {
                16 => Floats(-0f, -0f, 270f),
                17 => Floats(-0f, 180f, 90f),
                18 => Floats(0f, 0f, 90f),
                _ => Floats(-0f, 180f, 270f),
            };
            var tether = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(tether, anchor.X);
            BinaryPrimitives.WriteInt32LittleEndian(tether.AsSpan(4), anchor.Y);
            BinaryPrimitives.WriteInt32LittleEndian(tether.AsSpan(8), anchor.Z);
            var record = new DecorationRecord(extensions.Count, Guid.Empty,
            [
                new DecorationChunk(0, mesh.Guid.ToByteArray()),
                new DecorationChunk(1, Floats(1.001f, 1.001f, rule.LongitudinalScale)),
                new DecorationChunk(2, Floats(0f, 0f, offset)),
                new DecorationChunk(3, angles),
                new DecorationChunk(5, tether),
            ]);
            extensions.Add(new NativeSlopeExtension(new(anchor, host.Guid), SlopeExtensionKind.Horizontal,
                request.VisualLengthMetres, record));

            void Reject(string code, string message) => diagnostics.Add(DesignDiagnostic.Error(code,
                $"Anchor ({anchor.X}, {anchor.Y}, {anchor.Z}): {message}", field: nameof(SlopeExtensionRequest.Anchor)));
        }
        token.ThrowIfCancellationRequested();
        if (extensions.Count > 0 && extensions.Count * 105 % ushort.MaxValue == 0)
            return Error(SlopeExtensionDiagnosticCodes.PayloadLimit,
                "The decoration payload has an exact segmented-length multiple unsupported by D02.");
        // Fail atomically: callers cannot accidentally attach a partial result with rejected requests.
        return new(diagnostics.Count == 0 ? extensions.ToImmutable() : [], diagnostics.ToImmutable(), source);
    }

    private static NativeSlopeExtensionPlan Error(string code, string message) =>
        new([], [DesignDiagnostic.Error(code, message)]);

    private static byte[] Floats(float x, float y, float z)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), z);
        return bytes;
    }
}
