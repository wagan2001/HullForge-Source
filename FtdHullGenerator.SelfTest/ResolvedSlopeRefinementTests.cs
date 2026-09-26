using System.Collections.Immutable;
using System.Text.Json;
using System.Windows.Media.Media3D;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Refinement;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.Serialization.Decorations;
using FtdHullGenerator.Serialization.Projects;
using FtdHullGenerator.UI;
using FtdHullGenerator.UI.Editor;
using FtdHullGenerator.UI.Refinement;

internal static class ResolvedSlopeRefinementTests
{
    public static void Run(FtdBlockCatalog catalog)
    {
        var generatedDocument = ShipDocument.CreateNew("Generated explicit selection", HullParameters.Default with
            { Length=80, Width=21, Height=12, Smoothing=SmoothingMethod.VerticalSlopeFill });
        var generatedNative = new ShipGenerationService().Generate(generatedDocument, 1, catalog).Snapshot!;
        var generatedAnchor = generatedNative.Hull.Blocks.First(b => b.Shape==BlockShape.Slope4 && b.Origin==BlockOrigin.Smoothing && b.Rotation is 4 or 6 or 12 or 14);
        var generatedIntent = generatedDocument with { Smoothing = generatedDocument.Smoothing with {
            ExplicitRefinement=new(NativeSlopeExtensionRule.RecipeId,1,[new(generatedAnchor,11)]) } };
        var generatedSkin = new ShipGenerationService().Generate(generatedIntent,2,catalog);
        Require(generatedSkin.IsValid && generatedSkin.Snapshot!.Refinement!.Extensions.Length==1 && generatedSkin.Snapshot.Hull.Blocks.SequenceEqual(generatedNative.Hull.Blocks), "Actual generation changed native placements or lost explicit intent.");
        var basis = new ShipGenerationService().Generate(ShipDocument.CreateNew("Refinement integration",
            HullParameters.Default with { Length = 24, Width = 9, Height = 6 }), 9, catalog).Snapshot!;
        var blocks = (from material in new[] { MaterialKind.Wood, MaterialKind.Metal, MaterialKind.LightweightAlloy, MaterialKind.HeavyArmor }
                      from rotation in new[] { 4,6,12,14,16,17,18,19 }
                      from length in new[] { 21,31,40 }
                      select (material, rotation, length)).Select((item, index) =>
            (Anchor: new BlockPlacement(BlockShape.Slope4,item.material,(index % 8)*12,10,(index/8)*48,item.rotation) { Origin = BlockOrigin.Smoothing }, Length:item.length)).ToArray();
        var requests = blocks.Select(b => new SlopeExtensionRequest(b.Anchor,b.Length)).ToImmutableArray();
        foreach (var invalid in new[] { "", "abc", "5.5", "41", "-1", "9999999999999999999999" })
            Require(!SlopeRefinementDialog.TryBuildSelection(new[] { (blocks[0].Anchor, true, invalid) }, out _, out _), "Invalid editor length accepted.");
        Require(!SlopeRefinementDialog.TryBuildSelection(new[] { (blocks[0].Anchor, false, "21") }, out _, out _), "Stale selected anchor silently retained/discarded.");
        Require(SlopeRefinementDialog.TryBuildSelection(new[] { (blocks[0].Anchor, false, "4") }, out var cleared, out _) && cleared is null, "Explicit stale removal failed.");
        Require(SlopeRefinementDialog.TryBuildSelection(new[] { (blocks[0].Anchor, true, "40") }, out var accepted, out _) && accepted!.Requests[0].VisualLengthMetres==40, "Valid editor boundary rejected.");
        var native = WithBlocks(basis, blocks.Select(b => b.Anchor).ToArray());
        var document = native.Document with { Smoothing = native.Document.Smoothing with { ExplicitRefinement = new(NativeSlopeExtensionRule.RecipeId,1,requests) } };
        var result = ResolvedSlopeRefinement.Attach(native with { Document = document });
        Require(result.IsValid, string.Join("; ",result.Diagnostics));
        var snapshot = result.Snapshot!;
        var skin = snapshot.Refinement!;
        Require(skin.Extensions.Length == 96 && snapshot.Hull.Blocks.SequenceEqual(native.Hull.Blocks), "Combined planner altered native geometry.");
        Require(skin.Module.Records.Select(r => r.DecorationId).SequenceEqual(Enumerable.Range(0,96)), "Combined IDs are not unique/stable.");
        var reverse = ResolvedSlopeRefinement.Attach(native with { Document = document with { Smoothing = document.Smoothing with { ExplicitRefinement = document.Smoothing.ExplicitRefinement! with { Requests = requests.Reverse().ToImmutableArray() } } } }).Snapshot!;
        Require(DecorationModuleWriter.Write(skin.Module).SequenceEqual(DecorationModuleWriter.Write(reverse.Refinement!.Module)), "Request ordering changed payload.");
        Reject(() => skin.ValidateFor(snapshot with { Revision = 10 }));
        Reject(() => skin.ValidateFor(snapshot with { Hull = snapshot.Hull with { Blocks = blocks.Skip(1).Select(b => b.Anchor).ToArray() } }));
        Reject(() => skin.ValidateFor(snapshot with { Document = document with { Name = "changed" } }));
        var normalExposure = new FeatureExposurePolicy(false);
        var experimentalExposure = new FeatureExposurePolicy(true);
        // Manual/legacy explicit refinement is deferred beyond 2.0, so neither exposure
        // state may preview or export it; only Internal Structures is experimental.
        Require(!SlopeRefinementFeatureAccess.CanPreviewOrExport(document,normalExposure,out var normalGateReason) &&
                !SlopeRefinementFeatureAccess.CanPreviewOrExport(document,experimentalExposure,out var experimentalGateReason) &&
                normalGateReason is not null && experimentalGateReason is not null,
            "Manual/legacy explicit refinement is still exposed as a normal 2.0 feature.");
        var json = ProjectDocumentSerializer.Serialize(document);
        Require(json.Succeeded, string.Join("; ",json.Diagnostics));
        Require(!json.Json!.Contains("occupiedCells") && !json.Json.Contains("keepsFittedShape"), "Derived geometry leaked into project.");
        var loaded = ProjectDocumentSerializer.Deserialize(json.Json);
        Require(loaded.Succeeded && loaded.Document!.Smoothing.ExplicitRefinement!.Requests.SequenceEqual(requests), "Explicit intent did not round-trip.");
        Require(!ProjectDocumentSerializer.Deserialize(json.Json.Replace("\"recipeVersion\": 1", "\"recipeVersion\": 99")).Succeeded, "Unknown recipe accepted.");
        var session = new EditorSession(native.Document);
        using(var edit = session.BeginTransaction()) { edit.Update(_ => document); edit.Cancel(); }
        Require(session.Document.Smoothing.ExplicitRefinement is null, "Cancel leaked selection.");
        using(var edit = session.BeginTransaction()) { edit.Update(_ => document); edit.Apply(); }
        Require(session.Undo() && session.Document.Smoothing.ExplicitRefinement is null && session.Redo() && session.Document.Smoothing.ExplicitRefinement is not null, "Undo/redo lost selection.");
        using(var cancel = new CancellationTokenSource()) { cancel.Cancel(); try { ResolvedSlopeRefinement.Attach(native with { Document = document },cancel.Token); throw new Exception("Cancellation ignored."); } catch(OperationCanceledException) {} }
        var meshGroup = HullPreviewControl.BuildRefinementModel(skin);
        RequireRefinementModelEquivalent(meshGroup, skin);
        var distinctMaterials = skin.Extensions.Select(e => e.Anchor.Placement.Material).Distinct().Count();
        Require(meshGroup.Children.Count <= distinctMaterials && meshGroup.Children.Count < skin.Extensions.Length,
            "Refinement still draws one child per extension instead of one batch per material.");
        foreach (var model in meshGroup.Children.OfType<GeometryModel3D>())
            Require(ReferenceEquals(model.Material, model.BackMaterial),
                "A refinement batch lost its two-sided back material.");
        Require(meshGroup.IsFrozen, "The refinement model is no longer frozen.");

        // The old SetResolvedShip called SetHull (a full native rebuild with no refinement
        // attached) and then rebuilt the whole native model a second time with the refinement.
        // One atomic call must build the native model exactly once.
        VerifyAtomicResolvedShipBuild(snapshot, distinctMaterials);
        var budgetBlocks = Enumerable.Range(0,4369).Select(i => new BlockPlacement(BlockShape.Slope4,MaterialKind.Metal,i*5,10,0,i%2==0?12:16) { Origin=BlockOrigin.Smoothing }).ToArray();
        var budgetNative=WithBlocks(basis,budgetBlocks);
        var budgetDocument=basis.Document with { Smoothing=basis.Document.Smoothing with { ExplicitRefinement=new(NativeSlopeExtensionRule.RecipeId,1,budgetBlocks.Select(b=>new SlopeExtensionRequest(b,5)).ToImmutableArray()) } };
        var budget=ResolvedSlopeRefinement.Attach(budgetNative with {Document=budgetDocument});
        Require(!budget.IsValid && budget.Diagnostics.Any(d=>d.Code==SlopeExtensionDiagnosticCodes.PayloadLimit), "Combined exact segmented boundary accepted.");
        var root=Path.Combine(Path.GetTempPath(),"HullForgeRefinement-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exporter=new BlueprintExporter();
            var normal=exporter.Export(native,native.Document,9,Path.Combine(root,"native"),"comparison");
            var decorated=exporter.ExportRefinedForControlledOfflineEvidence(snapshot,document,9,Path.Combine(root,"refined"),"comparison");
            using var plain=JsonDocument.Parse(File.ReadAllText(normal.FilePath));
            using var extended=JsonDocument.Parse(File.ReadAllText(decorated.FilePath));
            var plainRoot=plain.RootElement.GetProperty("Blueprint"); var extRoot=extended.RootElement.GetProperty("Blueprint");
            foreach(var property in plainRoot.EnumerateObject().Where(p=>p.Name is not ("VehicleData" or "ForceId")))
                Require(property.Value.GetRawText()==extRoot.GetProperty(property.Name).GetRawText(),"Native export property changed: "+property.Name);
            var payload=Convert.FromBase64String(extRoot.GetProperty("VehicleData").GetString()!);
            Require(payload.SequenceEqual(DecorationModuleWriter.Write(skin.Module)),"Export did not use shared payload.");
            var disabledSnapshot=ResolvedSlopeRefinement.Attach(snapshot with { Document=native.Document }).Snapshot!;
            Require(disabledSnapshot.Refinement is null, "Disabled intent retained stale decorations.");
            var disabled=exporter.Export(disabledSnapshot,native.Document,9,Path.Combine(root,"disabled"),"comparison");
            var nativeJson=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(normal.FilePath))!;
            var disabledJson=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(disabled.FilePath))!;
            // Native export assigns a fresh ForceId on every write, including two writes without refinement.
            nativeJson["Blueprint"]!["ForceId"]=0; disabledJson["Blueprint"]!["ForceId"]=0;
            Require(nativeJson.ToJsonString()==disabledJson.ToJsonString(),"Disabled output differs beyond native per-export ForceId.");
            Reject(()=>exporter.ExportRefinedForControlledOfflineEvidence(snapshot,document,10,Path.Combine(root,"stale"),"stale"));
            Reject(()=>exporter.ExportRefinedForControlledOfflineEvidence(snapshot,document with { Name="edited" },9,Path.Combine(root,"stale-document"),"stale"));
            Require(!Directory.Exists(Path.Combine(root,"stale-document")),"Changed document export touched filesystem.");
            Require(!Directory.Exists(Path.Combine(root,"stale")),"Stale export touched filesystem.");
            var opaque=new DecorationModule(new[]{new DecorationRecord(700,Guid.Empty,new[]{new DecorationChunk(222,new byte[]{3,9,7})})});
            // Literal generic peer module: set 301, zero reserved, one-byte header and two-byte payload.
            var peer = Convert.FromHexString("2D01000000000000010000000200AABBCC");
            var existing = peer.Concat(DecorationModuleWriter.Write(opaque)).Concat(peer).ToArray();
            var preserved=exporter.ExportRefinedForControlledOfflineEvidence(snapshot,document,9,Path.Combine(root,"preserved"),"preserved",existing);
            using var preservation=JsonDocument.Parse(File.ReadAllText(preserved.FilePath));
            var preservedBytes=Convert.FromBase64String(preservation.RootElement.GetProperty("Blueprint").GetProperty("VehicleData").GetString()!);
            Require(preservedBytes.AsSpan(0,peer.Length).SequenceEqual(peer) && preservedBytes.AsSpan(preservedBytes.Length-peer.Length).SequenceEqual(peer), "Unrelated VehicleData peers changed.");
            var module=VehicleDataDecorationCodec.Read(preservedBytes)!;
            Require(module.Records[0].DecorationId==700 && module.Records[0].Chunks[0].Payload.SequenceEqual(new byte[]{3,9,7}) && module.Records[1].DecorationId==701,"Opaque record or deterministic ID allocation changed.");
            var largeOpaque = new DecorationModule(new[] { new DecorationRecord(0,Guid.Empty,
                Enumerable.Range(0,2530).Select(_=>new DecorationChunk(222,new byte[255]))) });
            try
            {
                exporter.ExportRefinedForControlledOfflineEvidence(snapshot,document,9,Path.Combine(root,"overflow"),"overflow",DecorationModuleWriter.Write(largeOpaque));
                throw new Exception("Combined existing/generated payload overflow accepted.");
            }
            catch(InvalidDataException) { }
            Require(!Directory.Exists(Path.Combine(root,"overflow")),"Payload rejection touched filesystem.");
            // Deferred manual refinement must not reach the normal export gate in either
            // exposure state, and must not touch the filesystem when it is rejected. The
            // resolved preview payload itself is proven by the controlled offline-evidence
            // export above.
            Reject(()=>exporter.Export(snapshot,document,9,Path.Combine(root,"deferred-off"),"blocked", exposure: normalExposure));
            Reject(()=>exporter.Export(snapshot,document,9,Path.Combine(root,"deferred-on"),"blocked", exposure: experimentalExposure));
            Require(!Directory.Exists(Path.Combine(root,"deferred-off")) && !Directory.Exists(Path.Combine(root,"deferred-on")),
                "Deferred manual-refinement export touched filesystem.");
            var reviewDirectory=Environment.GetEnvironmentVariable("HULL_FORGE_DECORATION_REVIEW_DIR");
            if(!string.IsNullOrWhiteSpace(reviewDirectory))
            {
                Directory.CreateDirectory(reviewDirectory);
                VerifyDialogRendering(blocks.Take(3).Select(b=>b.Anchor).ToArray(), document.Smoothing.ExplicitRefinement!, reviewDirectory);
                File.WriteAllText(Path.Combine(reviewDirectory,"manifest.json"),JsonSerializer.Serialize(new {
                    SourceCommit=Environment.GetEnvironmentVariable("HULL_FORGE_REVIEW_SOURCE_SHA") ?? "development",
                    GameValidation="Pending load and re-save; no generated-output game receipt exists.",
                    Anchors=skin.Extensions.Select(e=>new { X=e.Anchor.Placement.X,Y=e.Anchor.Placement.Y,Z=e.Anchor.Placement.Z,
                        Material=e.Anchor.Placement.Material.ToString(),Rotation=e.Anchor.Placement.Rotation,
                        Length=e.RequestedLengthMetres,MeshGuid=e.MeshPartGuid,DonorMetres=e.SourceMeshLengthMetres,RecordId=e.Record.DecorationId })
                },new JsonSerializerOptions {WriteIndented=true}));
                File.Copy(normal.FilePath,Path.Combine(reviewDirectory,"native-comparison.blueprint"),true);
                File.Copy(decorated.FilePath,Path.Combine(reviewDirectory,"mixed-orientations-21-31-40.blueprint"),true);
                File.WriteAllText(Path.Combine(reviewDirectory,"README.txt"),"Generated offline candidate: 96 anchors; V4/6/12/14 and H16/17/18/19; lengths21/31/40; Wood,Metal,LightweightAlloy,HeavyArmor. Native comparison is identical except VehicleData and the existing per-export random ForceId. NOT loaded or re-saved in game. Inspect appearance, tethers, materials and host visibility; source handmade fixtures remain untouched.");
            }
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("Resolved refinement: shared transform/payload, mixed 96-case review matrix, persistence, cancel/undo, stale rejection, combined limits, opaque preservation and native parity passed.");
    }
    /// <summary>
    /// Runs the real preview control on an STA thread: one SetResolvedShip must build the
    /// native model once (the old path built it twice), and the refinement must stay batched.
    /// </summary>
    private static void VerifyAtomicResolvedShipBuild(ShipGenerationSnapshot snapshot, int distinctMaterials)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var counted = new HullPreviewControl();
                var rebuildWatch = System.Diagnostics.Stopwatch.StartNew();
                counted.SetResolvedShip(snapshot, highlightEdits: false);
                rebuildWatch.Stop();
                Require(counted.NativeBuildCountForTests == 1,
                    $"SetResolvedShip built the native model {counted.NativeBuildCountForTests} times; the old duplicate path built it twice.");
                var countedShip = (Model3DGroup)counted.ShipModel!;
                var countedRefinement = (Model3DGroup)countedShip.Children[^1];
                Require(countedRefinement.Children.Count <= distinctMaterials,
                    $"Refinement preview produced {countedRefinement.Children.Count} children for {snapshot.Refinement!.Extensions.Length} extensions.");
                counted.SetResolvedShip(snapshot, highlightEdits: false);
                Require(counted.NativeBuildCountForTests == 2,
                    "A second SetResolvedShip did not add exactly one native build.");
                counted.SetHull(snapshot.Hull, highlightEdits: false);
                Require(counted.NativeBuildCountForTests == 3,
                    "SetHull did not build the native model exactly once.");
                Console.WriteLine($"Refinement preview: {snapshot.Refinement!.Extensions.Length} extensions -> " +
                    $"{countedRefinement.Children.Count} batched child(ren) / {distinctMaterials} material(s); " +
                    $"one atomic SetResolvedShip in {rebuildWatch.Elapsed.TotalMilliseconds:0.##} ms.");
                // The control is abandoned on this thread; detach its process-wide theme
                // subscription so a later palette swap on another thread cannot reach it.
                counted.DetachThemeSubscriptionForTests();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("The atomic resolved-ship preview probe failed.", failure);
    }
    /// <summary>
    /// Proves a batched refinement model still carries every extension's exact transformed
    /// envelope vertices, with none added or omitted, so material batching is visually
    /// equivalent to the one-model-per-extension rendering it replaced.
    /// </summary>
    internal static void RequireRefinementModelEquivalent(Model3DGroup group, ResolvedSlopeRefinement refinement)
    {
        var drawn = group.Children.OfType<GeometryModel3D>()
            .SelectMany(model => ((MeshGeometry3D)model.Geometry).Positions.Cast<Point3D>())
            .ToList();
        var expected = new List<Point3D>();
        foreach (var extension in refinement.Extensions)
        {
            var shape = extension.SourceMeshLengthMetres switch
            {
                1 => BlockShape.Slope1,
                2 => BlockShape.Slope2,
                3 => BlockShape.Slope3,
                _ => BlockShape.Slope4,
            };
            foreach (var face in BlockEnvelope.Faces(shape))
                foreach (var point in face.Points)
                {
                    var transformed = extension.TransformMeshPoint(new((float)point.X, (float)point.Y, (float)point.Z));
                    expected.Add(new Point3D(transformed.X + .5, transformed.Y + .5, transformed.Z + .5));
                }
        }

        Require(drawn.Count == expected.Count,
            $"Batched refinement drew {drawn.Count} vertices for {expected.Count} expected extension vertices.");
        var remaining = new List<Point3D>(drawn);
        foreach (var point in expected)
            Require(remaining.Remove(point),
                "Batched refinement lost an extension vertex or moved it away from the exported transform.");
    }

    private static void VerifyDialogRendering(BlockPlacement[] available, ExplicitSlopeRefinement intent, string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                var dialog=new SlopeRefinementDialog(available,intent with {Requests=intent.Requests.Take(4).ToImmutableArray()},new FeatureExposurePolicy(true));
                var content=(System.Windows.FrameworkElement)dialog.Content;
                content.Measure(new System.Windows.Size(680,440)); content.Arrange(new System.Windows.Rect(0,0,680,440)); content.UpdateLayout();
                var panel=(System.Windows.Controls.DockPanel)content;
                var grid=panel.Children.OfType<System.Windows.Controls.DataGrid>().Single();
                Require(grid.Items.Count==4,"Dialog discarded the stale saved host.");
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(680,440,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(directory,"explicit-editor.png")); encoder.Save(file);
                dialog.Close();
            }
            catch(Exception error) { failure=error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)),"Dialog render timed out.");
        if(failure is not null) throw new InvalidOperationException("Dialog render failed.",failure);
    }
    private static ShipGenerationSnapshot WithBlocks(ShipGenerationSnapshot basis,BlockPlacement[] blocks)
    {
        var cells=blocks.SelectMany(b=>b.OccupiedCells).ToArray();
        return basis with {Hull=basis.Hull with {Blocks=blocks,MinX=cells.Min(c=>c.X),MaxX=cells.Max(c=>c.X),MinY=cells.Min(c=>c.Y),MaxY=cells.Max(c=>c.Y),MinZ=cells.Min(c=>c.Z),MaxZ=cells.Max(c=>c.Z)}};
    }
    private static void Require(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try {action();} catch(InvalidOperationException) {return;} throw new Exception("Invalid snapshot accepted."); }
}
