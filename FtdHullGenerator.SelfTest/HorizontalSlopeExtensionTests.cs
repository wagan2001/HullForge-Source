using System.Buffers.Binary;
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

internal static class HorizontalSlopeExtensionTests
{
    public static void Run(FtdBlockCatalog catalog)
    {
        var planner = new HorizontalSlopeExtensionPlanner();
        var parameters = HullParameters.Default with { Length = 24, Width = 9, Height = 6 };
        var basis = new ShipGenerationService().Generate(
            ShipDocument.CreateNew("Horizontal extension tests", parameters), 7, catalog).Snapshot
            ?? throw new InvalidOperationException("Could not construct the resolved test snapshot.");
        VerifyGolden();
        VerifyLengthsAndMaterials();
        VerifyGuards();
        VerifyBudgets();
        Console.WriteLine("Horizontal extensions: 32 byte-exact golden records, 5–40 m/four-material matrix, " +
            "translated mirrors, native identity, rejection, cancellation and D02 budgets passed.");

        ShipGenerationSnapshot Snapshot(params BlockPlacement[] blocks)
        {
            var cells = blocks.SelectMany(b => b.OccupiedCells).ToArray();
            return basis with { Hull = basis.Hull with { Blocks = blocks,
                MinX = cells.Min(c => c.X), MaxX = cells.Max(c => c.X),
                MinY = cells.Min(c => c.Y), MaxY = cells.Max(c => c.Y),
                MinZ = cells.Min(c => c.Z), MaxZ = cells.Max(c => c.Z) } };
        }

        void VerifyGolden()
        {
            var root = FindRoot();
            var directory = Path.Combine(root, "FtdHullGenerator.SelfTest/Fixtures/HandmadeDecorations");
            var bytes = File.ReadAllBytes(Path.Combine(directory, "handmade_hfill_decorated.blueprint"));
            Require(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() ==
                "9aade945a0ab872b89baa9c673a506d3f51b4d99e1d4cc296ddf630232f3d94e", "Golden source changed.");
            using var mapping = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "handmade-refinement-mapping.json")));
            var records = mapping.RootElement.GetProperty("fixtures").EnumerateArray()
                .Single(f => f.GetProperty("fixture").GetString() == "handmade_hfill_decorated.blueprint")
                .GetProperty("records").EnumerateArray().ToArray();
            var requests = records.Select(r =>
            {
                var host = r.GetProperty("native_host");
                var p = host.GetProperty("position").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                return new SlopeExtensionRequest(Anchor(p[0], p[1], p[2], host.GetProperty("rotation").GetInt32()),
                    r.GetProperty("requested_visual_length_metres").GetInt32());
            }).ToArray();
            var snapshot = Snapshot(requests.Select(r => r.Anchor).ToArray());
            var plan = planner.Build(snapshot, requests);
            Require(plan.IsValid && plan.Extensions.Length == 32, "Golden planning failed.");
            for (var i = 0; i < records.Length; i++)
            {
                var extension = plan.Extensions.Single(e => e.Anchor.Placement == requests[i].Anchor);
                Require(Raw(extension.Record).SequenceEqual(Convert.FromHexString(records[i].GetProperty("raw_record_hex").GetString()!)),
                    $"Golden record {i} lost raw fields, omissions, signed zero or offset residuals.");
            }
            Require(plan.Extensions.Select(e => e.Record.DecorationId).SequenceEqual(Enumerable.Range(0, 32)), "Generated IDs are not unique and deterministic.");
            var reversed = planner.Build(snapshot with { Hull = snapshot.Hull with { Blocks = snapshot.Hull.Blocks.Reverse().ToArray() } }, requests.Reverse());
            Require(DecorationModuleWriter.Write(new(plan.Extensions.Select(e => e.Record)))
                .SequenceEqual(DecorationModuleWriter.Write(new(reversed.Extensions.Select(e => e.Record)))), "Input order changed output.");
            Require(plan.Source!.NativePlacements.SequenceEqual(snapshot.Hull.Blocks) &&
                plan.Source.Revision == 7 && plan.Source.CatalogFingerprint == snapshot.Hull.ResolvedCatalogFingerprint,
                "Source native identity was not frozen.");
            var translatedRequests = requests.Select(r => r with { Anchor = r.Anchor with {
                X = r.Anchor.X + 37, Y = r.Anchor.Y - 19, Z = r.Anchor.Z + 83 } }).ToArray();
            var translated = planner.Build(Snapshot(translatedRequests.Select(r => r.Anchor).ToArray()), translatedRequests);
            Require(translated.IsValid, "Translated fixture failed.");
            foreach (var extension in plan.Extensions)
            {
                var other = translated.Extensions.Single(e => e.Anchor.Placement.X == extension.Anchor.Placement.X + 37 &&
                    e.Anchor.Placement.Y == extension.Anchor.Placement.Y - 19 && e.Anchor.Placement.Z == extension.Anchor.Placement.Z + 83);
                Require(extension.Record.Chunks.Take(4).Zip(other.Record.Chunks.Take(4)).All(p => p.First.Payload.SequenceEqual(p.Second.Payload)),
                    "Translation changed a tether-relative transform.");
                Require(Math.Abs(other.VisualBounds.MinX - extension.VisualBounds.MinX - 37) < .0001 &&
                    Math.Abs(other.VisualBounds.MinZ - extension.VisualBounds.MinZ - 83) < .0001, "Visual bounds did not translate.");
                Require(translated.Extensions.Any(e => e.Anchor.Placement.X == 74 - other.Anchor.Placement.X &&
                    e.Anchor.Placement.Y == other.Anchor.Placement.Y && e.Anchor.Placement.Z == other.Anchor.Placement.Z),
                    "Nonzero mirror plane lost a partner.");
            }
            var original = snapshot.Hull.Blocks.ToArray();
            snapshot.Hull.Blocks.AsSpanIfArray()[0] = original[0] with { ArmorDepth = 3 };
            Require(plan.Source.NativePlacements.SequenceEqual(original), "Source identity aliases mutable native input.");
        }

        void VerifyLengthsAndMaterials()
        {
            foreach (var material in new[] { MaterialKind.Wood, MaterialKind.Metal, MaterialKind.LightweightAlloy, MaterialKind.HeavyArmor })
            foreach (var rotation in new[] { 16, 17, 18, 19 })
            foreach (var length in Enumerable.Range(5, 36))
            {
                var anchor = Anchor(37, -11, 83, rotation) with { Material = material };
                var snapshot = Snapshot(anchor);
                var before = snapshot.Hull;
                var plan = planner.Build(snapshot, [new(anchor, length)]);
                Require(plan.IsValid && plan.Extensions.Length == 1, $"{material}/{rotation}/{length} failed.");
                var e = plan.Extensions[0];
                var expectedMesh = length <= 10 ? BlockShape.Slope1 : length <= 20 ? BlockShape.Slope2 : length <= 30 ? BlockShape.Slope3 : BlockShape.Slope4;
                Require(e.MeshPartGuid == catalog.Resolve(material, expectedMesh).Guid && e.Anchor.PartGuid == catalog.Resolve(material, BlockShape.Slope4).Guid,
                    "Donor or host ignored actual anchor material.");
                Require(ReferenceEquals(before, snapshot.Hull) && snapshot.Hull.Blocks.SequenceEqual(plan.Source!.NativePlacements), "Planner mutated native geometry.");
                Require(Math.Abs(e.SourceMeshLengthMetres * e.Scale.Z - length) < .00001 &&
                    Math.Abs(Math.Abs(e.Position.Z) - (e.Scale.Z - 1) / 2) < .00001, "Length or offset violated the rule.");
                DecorationModuleWriter.Write(new([e.Record]));
            }
        }

        void VerifyGuards()
        {
            var a = Anchor(1, 4, 0, 16);
            var snapshot = Snapshot(a);
            void Reject(ShipGenerationSnapshot s, IEnumerable<SlopeExtensionRequest> r, string code)
            {
                var result = planner.Build(s, r);
                Require(!result.IsValid && result.Extensions.IsEmpty && result.Diagnostics.Any(d => d.Code == code), $"Expected atomic {code} rejection.");
            }
            foreach (var length in new[] { 0, -1, 41, int.MaxValue }) Reject(snapshot, [new(a, length)], "DEC103");
            foreach (var shape in new[] { BlockShape.Slope1, BlockShape.Slope2, BlockShape.Slope3, BlockShape.Slope4 })
            {
                var control = a with { Shape = shape };
                var result = planner.Build(Snapshot(control), [new(control, control.CellLength)]);
                Require(result.IsValid && result.Extensions.IsEmpty, "Native 1–4 m control received an extension.");
            }
            Reject(snapshot, [new(a with { ArmorDepth = 1 }, 5)], "DEC101");
            Reject(snapshot, [new(a with { X = 99 }, 5)], "DEC101");
            foreach (var bad in new[] { a with { Shape = BlockShape.Cube }, a with { Origin = BlockOrigin.Shell }, a with { ArmorDepth = 1 } })
                Reject(Snapshot(bad), [new(bad, 5)], "DEC101");
            foreach (var rotation in Enumerable.Range(0, 24).Except([16, 17, 18, 19]))
            {
                var bad = a with { Rotation = rotation };
                Reject(Snapshot(bad), [new(bad, 5)], "DEC102");
            }
            Reject(snapshot, [new(a, 5), new(a, 6)], "DEC101");
            Reject(Snapshot(a, a), [new(a, 5)], "DEC101");
            Reject(Snapshot(a, a with { Shape = BlockShape.Cube, Z = 1 }), [new(a, 5)], "DEC101");
            var unsupported = a with { Material = MaterialKind.Stone };
            Reject(Snapshot(unsupported), [new(unsupported, 5)], "DEC104");
            Reject(snapshot with { Revision = 8 }, [new(a, 5)], "DEC105");
            Reject(snapshot with { Hull = snapshot.Hull with { ResolvedCatalogFingerprint = "stale" } }, [new(a, 5)], "DEC105");
            Reject(snapshot with { Hull = snapshot.Hull with { SourceDocumentId = "stale" } }, [new(a, 5)], "DEC105");
            Reject(snapshot with { Hull = snapshot.Hull with { MinZ = 1 } }, [new(a, 5)], "DEC101");
            Require(planner.Build(snapshot with { Hull = snapshot.Hull with { CatalogFallbackCount = 1 } }, [new(a, 5)]).IsValid,
                "An unrelated fallback count incorrectly rejected an exact resolved slope anchor.");
            var fallbackRoot = Path.Combine(Path.GetTempPath(), "HullForge-D04-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(fallbackRoot, "From_The_Depths_Data", "StreamingAssets"));
            FtdBlockCatalog fallback;
            try { fallback = FtdBlockCatalog.Load(fallbackRoot); }
            finally { Directory.Delete(fallbackRoot, true); }
            Reject(snapshot with { Catalog = fallback, Hull = snapshot.Hull with {
                ResolvedCatalogVersion = fallback.GameVersion, ResolvedCatalogFingerprint = ShipCatalogFingerprint.Compute(fallback) } }, [new(a, 5)], "DEC104");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { planner.Build(snapshot, [new(a, 5)], cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            using var duringEnumeration = new CancellationTokenSource();
            IEnumerable<SlopeExtensionRequest> CancelDuringEnumeration()
            {
                yield return new(a, 5);
                duringEnumeration.Cancel();
                yield return new(a, 6);
            }
            try { planner.Build(snapshot, CancelDuringEnumeration(), duringEnumeration.Token); throw new InvalidOperationException("Mid-enumeration cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }

        void VerifyBudgets()
        {
            var anchors = Enumerable.Range(0, 4369).Select(i => Anchor(i * 2, 0, 0, 16)).ToArray();
            var result = planner.Build(Snapshot(anchors), anchors.Select(a => new SlopeExtensionRequest(a, 5)));
            Require(!result.IsValid && result.Extensions.IsEmpty && result.Diagnostics.Any(d => d.Code == "DEC107"), "D02 exact segmented multiple was accepted.");
            var a = anchors[0];
            result = planner.Build(Snapshot(a), Enumerable.Repeat(new SlopeExtensionRequest(a, 5), HorizontalSlopeExtensionPlanner.MaximumRequests + 1));
            Require(!result.IsValid && result.Diagnostics.Any(d => d.Code == "DEC107"), "Unbounded requests were accepted.");
        }
    }

    private static BlockPlacement Anchor(int x, int y, int z, int rotation) =>
        new(BlockShape.Slope4, MaterialKind.Metal, x, y, z, rotation) { Origin = BlockOrigin.Smoothing };

    private static byte[] Raw(DecorationRecord record)
    {
        using var bytes = new MemoryStream();
        foreach (var chunk in record.Chunks)
        {
            bytes.WriteByte((byte)chunk.Key); bytes.WriteByte((byte)(chunk.Key >> 8));
            bytes.WriteByte((byte)chunk.Payload.Length); bytes.Write(chunk.Payload.AsSpan());
        }
        return bytes.ToArray();
    }

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "FtdHullGenerator.sln"))) return d.FullName;
        throw new InvalidOperationException("Repository fixture root not found.");
    }

    private static Span<BlockPlacement> AsSpanIfArray(this IReadOnlyList<BlockPlacement> values) => (BlockPlacement[])values;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
