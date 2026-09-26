
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Refinement;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization.Decorations;

internal static class VerticalSlopeExtensionTests
{
    public static void Run()
    {
        var catalog = FtdBlockCatalog.Load(FtdInstallationLocator.FindInstalledGame()
            ?? throw new InvalidOperationException("Installed catalog required for Vertical evidence tests."));
        var parameters = HullParameters.Default with { Length = 20, Width = 9, Height = 8,
            Superstructure = SuperstructureSettings.Default with { Enabled = false } };
        var document = ShipDocument.FromLegacyParameters(parameters, "Vertical evidence", "vertical-evidence");
        var generated = new ShipGenerationService().Generate(document, 23, catalog);
        Require(generated.IsValid, "Native snapshot generation failed.");
        var basis = generated.Snapshot!;
        VerifyGolden(basis);
        VerifyRangeAndMaterials(basis);
        VerifyRejectionsAndIdentity(basis);
        Console.WriteLine("Vertical extensions: 64 byte-exact records, all 5–40 m lengths/materials, translation/mirrors, native identity, diagnostics, cancellation and bounded payload passed.");
    }

    private static void VerifyGolden(ShipGenerationSnapshot basis)
    {
        var directory = RepositoryFile(Path.Combine("FtdHullGenerator.SelfTest", "Fixtures", "HandmadeDecorations"));
        var bytes = File.ReadAllBytes(Path.Combine(directory, "handmade_vfill_decorated.blueprint"));
        Require(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() ==
            "33109ead3630e46a9f09f732d4d7b7eb333d6b2e2cad9ae73ea2cd26baf245f7", "Vertical oracle changed.");
        using var json = JsonDocument.Parse(bytes);
        var blueprint = json.RootElement.GetProperty("Blueprint");
        var records = VehicleDataDecorationCodec.Read(Convert.FromBase64String(blueprint.GetProperty("VehicleData").GetString()!))!.Records;
        using var mapping = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "handmade-refinement-mapping.json")));
        var rows = mapping.RootElement.GetProperty("fixtures").EnumerateArray()
            .Single(f => f.GetProperty("fixture").GetString() == "handmade_vfill_decorated.blueprint")
            .GetProperty("records").EnumerateArray().ToArray();
        var requests = rows.Select(row =>
        {
            var host = row.GetProperty("native_host");
            var p = host.GetProperty("position").EnumerateArray().Select(n => n.GetInt32()).ToArray();
            return new SlopeExtensionRequest(Anchor(p[0], p[1], p[2], host.GetProperty("rotation").GetInt32()),
                row.GetProperty("requested_visual_length_metres").GetInt32());
        }).ToArray();
        var snapshot = WithBlocks(basis, requests.Select(r => r.Anchor).ToArray());
        var planner = new VerticalSlopeExtensionPlanner();
        var plan = planner.Build(snapshot, requests);
        Require(plan.IsValid && plan.Extensions.Length == 64, "Golden request planning failed: " + string.Join(";", plan.Diagnostics));
                foreach (var expected in records)
        {
            var tether = expected.Chunks.Single(c => c.Key == 5).Payload;
            var actual = plan.Extensions.Single(e => e.Record.Chunks.Single(c => c.Key == 5).Payload.SequenceEqual(tether));
            Require(EqualChunks(actual.Record, expected), $"Golden record {expected.DecorationId} changed raw payload bytes.");
        }
        var repeat = planner.Build(snapshot, requests.Reverse());
        Require(plan.Extensions.Zip(repeat.Extensions).All(pair => pair.First.Record.DecorationId == pair.Second.Record.DecorationId &&
            EqualChunks(pair.First.Record, pair.Second.Record)), "Request order/IDs are not deterministic.");

        // Translate both sides across a nonzero mirror plane, retaining each raw relative field.
        var translatedRequests = requests.Select(r => r with { Anchor = r.Anchor with
            { X = r.Anchor.X + 37, Y = r.Anchor.Y - 11, Z = r.Anchor.Z + 53 } }).ToArray();
        var translated = planner.Build(WithBlocks(basis, translatedRequests.Select(r => r.Anchor).ToArray()), translatedRequests);
        Require(translated.IsValid, "Translated golden plan failed.");
        foreach (var (original, moved) in plan.Extensions.Zip(translated.Extensions))
        {
            Require(original.Record.Chunks.Take(4).Zip(moved.Record.Chunks).All(p => p.First.Payload.SequenceEqual(p.Second.Payload)),
                "Translation changed a relative transform or material.");
            Require(moved.Anchor.Placement == original.Anchor.Placement with
                { X = original.Anchor.Placement.X + 37, Y = original.Anchor.Placement.Y - 11, Z = original.Anchor.Placement.Z + 53 },
                "Translation lost exact tether/native identity.");
            Require(Math.Abs(moved.VisualBounds.MinX - original.VisualBounds.MinX - 37) < .0001 &&
                Math.Abs(moved.VisualBounds.MinY - original.VisualBounds.MinY + 11) < .0001 &&
                Math.Abs(moved.VisualBounds.MinZ - original.VisualBounds.MinZ - 53) < .0001,
                "Visual bounds did not translate with the tether.");
        }
        foreach (var extension in translated.Extensions)
            Require(translated.Extensions.Any(other => other.Anchor.Placement.X == 74 - extension.Anchor.Placement.X &&
                other.Anchor.Placement.Y == extension.Anchor.Placement.Y && other.Anchor.Placement.Z == extension.Anchor.Placement.Z &&
                other.RequestedLengthMetres == extension.RequestedLengthMetres && other.Position == extension.Position &&
                other.EulerAngles == extension.EulerAngles), "Nonzero-plane mirrored partner changed.");
    }

    private static void VerifyRangeAndMaterials(ShipGenerationSnapshot basis)
    {
        var requests = new List<SlopeExtensionRequest>();
        foreach (var material in new[] { MaterialKind.Metal, MaterialKind.Wood, MaterialKind.LightweightAlloy, MaterialKind.HeavyArmor })
        foreach (var rotation in new[] { 4, 6, 12, 14 })
        for (var length = 5; length <= 40; length++)
            requests.Add(new(Anchor(requests.Count * 5, 20, 0, rotation) with { Material = material }, length));
        var plan = new VerticalSlopeExtensionPlanner().Build(WithBlocks(basis, requests.Select(r => r.Anchor).ToArray()), requests);
        Require(plan.IsValid && plan.Extensions.Length == 576, "Full supported explicit-length/material matrix failed.");
        foreach (var e in plan.Extensions)
        {
            var length = e.RequestedLengthMetres;
            var m = (length + 9) / 10;
            var shape = m switch { 1 => BlockShape.Slope1, 2 => BlockShape.Slope2, 3 => BlockShape.Slope3, _ => BlockShape.Slope4 };
            Require(e.SourceMeshLengthMetres == m && e.MeshPartGuid == basis.Catalog.Resolve(e.Anchor.Placement.Material, shape).Guid &&
                e.Anchor.PartGuid == basis.Catalog.Resolve(e.Anchor.Placement.Material, BlockShape.Slope4).Guid,
                "Anchor material or donor boundary changed.");
            Require(Math.Abs(e.Scale.Z * m - length) < .00001 && e.Scale.Z <= 10, "Scale does not retain requested length.");
            var d = ((float)length / m - 1) / 2;
            var expected = e.Anchor.Placement.Rotation switch
            {
                12 => new DecorationVector3(0, 0, d),
                14 => new DecorationVector3(0, 0, -.9999997615814209f * d),
                _ => new DecorationVector3(0, -.9999998211860657f * d, 1.7881393432617188e-7f * d),
            };
            Require(e.Position == expected && e.Record.Chunks.Select(c => c.Key).SequenceEqual(new ushort[] { 0, 1, 2, 3, 5 }),
                "Measured offset direction or field omissions changed outside golden lengths.");
        }
    }

    private static void VerifyRejectionsAndIdentity(ShipGenerationSnapshot basis)
    {
        var planner = new VerticalSlopeExtensionPlanner();
        var anchor = Anchor(0, 20, 0, 4);
        var mutable = new List<BlockPlacement> { anchor };
        var snapshot = WithBlocks(basis, mutable);
        var before = snapshot.Hull;
        var plan = planner.Build(snapshot, [new(anchor, 21)]);
        Require(plan.IsValid && ReferenceEquals(before, snapshot.Hull) && before.Blocks.SequenceEqual([anchor]) &&
            plan.Source!.NativePlacements.SequenceEqual([anchor]) && plan.Source.Revision == snapshot.Revision &&
            plan.Source.DocumentId == snapshot.Document.DocumentId && plan.Source.CatalogFingerprint == snapshot.Hull.ResolvedCatalogFingerprint,
            "Planner changed native blocks/armor/bounds or failed to capture final identity.");
        mutable.Clear();
        Require(plan.Source!.NativePlacements.SequenceEqual([anchor]), "Source placements were not frozen.");
        snapshot = WithBlocks(basis, [anchor]);
        Check(snapshot, [new(anchor with { ArmorDepth = 2 }, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        Check(snapshot, [new(anchor with { X = 1 }, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        Check(snapshot, [new(anchor, 5), new(anchor, 6)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        Check(WithBlocks(basis, [anchor, anchor]), [new(anchor, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        Check(snapshot with { Hull = snapshot.Hull with { MinY = anchor.Y } }, [new(anchor, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        var overlappingCube = new BlockPlacement(BlockShape.Cube, MaterialKind.Metal, 0, 19, 0, 0);
        Check(WithBlocks(basis, [anchor, overlappingCube]), [new(anchor, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        var overlappingBeam = new BlockPlacement(BlockShape.Beam4, MaterialKind.Wood, 0, 19, -2, 0);
        Check(WithBlocks(basis, [overlappingBeam, anchor]), [new(anchor, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        var separateAnchor = anchor with { X = 20 };
        Check(WithBlocks(basis, [anchor, overlappingCube, separateAnchor]), [new(separateAnchor, 12), new(anchor, 5)],
            SlopeExtensionDiagnosticCodes.InvalidAnchor);
        foreach (var rotation in new[] { -1, 24 })
            Check(WithBlocks(basis, [anchor, overlappingBeam with { Rotation = rotation }]), [new(anchor, 5)],
                SlopeExtensionDiagnosticCodes.InvalidAnchor);
        var adjacent = planner.Build(WithBlocks(basis, [anchor, overlappingCube with { X = 1 }]), [new(anchor, 5)]);
        Require(adjacent.IsValid && adjacent.Extensions.Length == 1, "Adjacent nonoverlapping native placement rejected.");
        foreach (var length in new[] { int.MinValue, 0, 41, int.MaxValue })
            Check(snapshot, [new(anchor, length)], SlopeExtensionDiagnosticCodes.UnsupportedLength);
        foreach (var rotation in Enumerable.Range(0, 24).Except([4, 6, 12, 14]).Concat([-1, 24]))
        {
            var wrong = anchor with { Rotation = rotation };
            Check(WithBlocks(basis, [wrong]), [new(wrong, 5)], SlopeExtensionDiagnosticCodes.UnsupportedOrientation);
        }
        foreach (var wrong in new[] { anchor with { Shape = BlockShape.Slope3 }, anchor with { Origin = BlockOrigin.Shell }, anchor with { ArmorDepth = 1 }, anchor with { UsePoles = true } })
            Check(WithBlocks(basis, [wrong]), [new(wrong, 5)], SlopeExtensionDiagnosticCodes.InvalidAnchor);
        foreach (var material in new[] { MaterialKind.Stone, MaterialKind.Lead, MaterialKind.Glass, MaterialKind.Rubber, (MaterialKind)99 })
        {
            var wrong = anchor with { Material = material };
            Check(WithBlocks(basis, [wrong]), [new(wrong, 5)], SlopeExtensionDiagnosticCodes.MissingMaterialPart);
        }
        foreach (var wrong in new[] { snapshot with { Revision = 24 }, snapshot with { Hull = snapshot.Hull with
            { ResolvedCatalogFingerprint = "stale" } }, snapshot with { Hull = snapshot.Hull with { SourceDocumentId = "other" } } })
            Check(wrong, [new(anchor, 5)], SlopeExtensionDiagnosticCodes.UnresolvedNativePlan);
                var emptyCatalogRoot = Path.Combine(Path.GetTempPath(), "absent-vertical-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(emptyCatalogRoot, "From_The_Depths_Data", "StreamingAssets", "Mods", "Core_Structural"));
        FtdBlockCatalog fallback;
        try { fallback = FtdBlockCatalog.Load(emptyCatalogRoot); }
        finally { Directory.Delete(emptyCatalogRoot, true); }
        var missing = snapshot with { Catalog = fallback, Hull = snapshot.Hull with { ResolvedCatalogVersion = fallback.GameVersion,
            ResolvedCatalogFingerprint = ShipCatalogFingerprint.Compute(fallback) } };
        Check(missing, [new(anchor, 5)], SlopeExtensionDiagnosticCodes.MissingMaterialPart);
        var controls = Enumerable.Range(1, 4).Select(length => new SlopeExtensionRequest(anchor with
            { X = length * 5, Shape = length switch { 1 => BlockShape.Slope1, 2 => BlockShape.Slope2, 3 => BlockShape.Slope3, _ => BlockShape.Slope4 } }, length)).ToArray();
        var nativeOnly = planner.Build(WithBlocks(basis, controls.Select(r => r.Anchor).ToArray()), controls);
        Require(nativeOnly.IsValid && nativeOnly.Extensions.IsEmpty && nativeOnly.Diagnostics.IsEmpty, "Native 1–4 m controls changed.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { planner.Build(snapshot, [], cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        using var during = new CancellationTokenSource();
        IEnumerable<SlopeExtensionRequest> CancelDuringEnumeration()
        {
            yield return new(anchor, 5);
            during.Cancel();
            yield return new(anchor with { X = 5 }, 5);
        }
        try { planner.Build(snapshot, CancelDuringEnumeration(), during.Token); throw new InvalidOperationException("Mid-enumeration cancellation ignored."); }
        catch (OperationCanceledException) { }
        var nulls = planner.Build(snapshot, Enumerable.Repeat<SlopeExtensionRequest>(null!, VerticalSlopeExtensionPlanner.MaximumRequests + 1));
        Require(!nulls.IsValid && nulls.Diagnostics.Any(d => d.Code == SlopeExtensionDiagnosticCodes.PayloadLimit), "Null input bypassed the request limit.");
        var bounded = planner.Build(snapshot, Enumerable.Repeat(new SlopeExtensionRequest(anchor, 5), VerticalSlopeExtensionPlanner.MaximumRequests + 1));
        Require(!bounded.IsValid && bounded.Diagnostics.Any(d => d.Code == SlopeExtensionDiagnosticCodes.PayloadLimit) &&
            bounded.Diagnostics.Length <= VerticalSlopeExtensionPlanner.MaximumRequests + 1, "Input/payload limit did not terminate enumeration.");
        var maximum = Enumerable.Range(0, VerticalSlopeExtensionPlanner.MaximumRequests)
            .Select(i => new SlopeExtensionRequest(anchor with { X = i * 5 }, 40)).ToArray();
        var maximumPlan = planner.Build(WithBlocks(basis, maximum.Select(r => r.Anchor).ToArray()), maximum);
        Require(maximumPlan.IsValid, "Maximum bounded payload rejected.");
                var boundaryRequests = maximum.Take(4369).ToArray();
        Check(WithBlocks(basis, boundaryRequests.Select(r => r.Anchor).ToArray()), boundaryRequests, SlopeExtensionDiagnosticCodes.PayloadLimit);
        var payload = DecorationModuleWriter.Write(new DecorationModule(maximumPlan.Extensions.Select(e => e.Record).ToImmutableArray()));
        Require(payload.Length > 0, "Maximum payload did not pass D02 writer bounds.");

        void Check(ShipGenerationSnapshot input, IEnumerable<SlopeExtensionRequest> requests, string code)
        {
            var invalid = planner.Build(input, requests);
            Require(!invalid.IsValid && invalid.Extensions.IsEmpty && invalid.Diagnostics.Any(d => d.Code == code), $"Expected {code} diagnostic missing.");
        }
    }

    private static ShipGenerationSnapshot WithBlocks(ShipGenerationSnapshot basis, IReadOnlyList<BlockPlacement> blocks) =>
                basis with { Hull = basis.Hull with { Blocks = blocks,
            MinX = blocks.Count == 0 ? 0 : blocks.Min(b => b.X) - 4, MaxX = blocks.Count == 0 ? 0 : blocks.Max(b => b.X) + 4,
            MinY = blocks.Count == 0 ? 0 : blocks.Min(b => b.Y) - 4, MaxY = blocks.Count == 0 ? 0 : blocks.Max(b => b.Y) + 4,
            MinZ = blocks.Count == 0 ? 0 : blocks.Min(b => b.Z) - 4, MaxZ = blocks.Count == 0 ? 0 : blocks.Max(b => b.Z) + 4 } };
    private static BlockPlacement Anchor(int x, int y, int z, int rotation) =>
        new(BlockShape.Slope4, MaterialKind.Metal, x, y, z, rotation) { Origin = BlockOrigin.Smoothing };
    private static bool EqualChunks(DecorationRecord a, DecorationRecord b) => a.PreambleGuid == b.PreambleGuid &&
        a.Chunks.Length == b.Chunks.Length && a.Chunks.Zip(b.Chunks).All(pair => pair.First.Key == pair.Second.Key &&
            pair.First.Payload.SequenceEqual(pair.Second.Payload));
    private static string RepositoryFile(string path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, path);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(path);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
