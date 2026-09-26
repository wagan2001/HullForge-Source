using System.Collections.Concurrent;
using System.Reflection;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Components;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Geometry.Layout;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.UI.Editor;

/// <summary>
/// Adversarial HF-03/HF-04/HF-05 oracles: the odd whole-metre clear-diameter domain invariant, the
/// whole-metre arrangement measurement anchor, the derived resolved layout frame identity/ruler
/// algebra, stale/cancelled preparation suppression and preparation-side editor-history isolation.
/// These cases are written to fail against the pre-fix implementation (even/fractional diameters
/// accepted, diameter choosing a temporary parity, unguarded background frames, stale revisions
/// rolling a newer frame backwards).
/// </summary>
/// <remarks>
/// This suite covers the domain/preparation backend and its product wiring seams. The live product
/// first-Add, ruler/Move/arrangement consumption and MainWindow preview wiring that consume the
/// frame are exercised by <c>Stabilization2TeamCTests</c>.
/// </remarks>
internal static class LayoutFrameTests
{
    public static void Run()
    {
        VerifyClearDiameterDomainInvariant();
        VerifyClearDiameterProgressionHelpers();
        VerifyArrangementMeasurementUsesWholeMetreAnchor();
        VerifyCompatibleMeasurementCenterRetired();
        VerifyFrameIdentityAndRulerConversions();
        VerifyFrameResolverUnsupportedHullFailsClosed();
        VerifySynchronousPreparationAndNoDerivedHistory();
        VerifyStaleJobCannotPublish();
        VerifyNewerJobSupersedesOlder();
        VerifyCancelledJobDoesNotPublish();
        VerifyStaleRevisionCannotReplaceNewerFrame();
        VerifyFirstAddFrameIdentity();

        Console.WriteLine(
            "Layout frame backend: odd whole-metre clear-diameter invariant (BAR009 on ClearDiameter, " +
            "never BAR015/BAR107), whole-metre arrangement anchor at context.CenterPlaneX, derived frame " +
            "identity/ruler algebra, stale/cancelled preparation suppression and preparation-side " +
            "editor-history isolation passed. Product first-Add/ruler/preview wiring is exercised by " +
            "Stabilization2TeamCTests.");
    }

    /// <summary>HF-04: 3/5/7/9/11 valid; 4/6/8/fractional/zero/negative invalid with BAR009, never parity.</summary>
    private static void VerifyClearDiameterDomainInvariant()
    {
        foreach (var metres in new[] { 1, 3, 5, 7, 9, 11 })
        {
            var definition = Definition(DesignMeasure.FromMetres(metres));
            Require(BarbetteDefinition.IsSupportedClearDiameter(definition.ClearDiameter),
                $"A {metres} m clear diameter must be a supported product value.");
            Require(!definition.Validate().Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.BarbetteClearDiameterInvalid),
                $"A {metres} m clear diameter must not report BAR009.");
        }

        foreach (var metres in new[] { 4, 6, 8 })
        {
            var definition = Definition(DesignMeasure.FromMetres(metres));
            Require(!BarbetteDefinition.IsSupportedClearDiameter(definition.ClearDiameter),
                $"An even {metres} m clear diameter must not be a supported product value.");
            VerifyClearDiameterDiagnostic(definition, $"an even {metres} m");
        }

        foreach (var twiceMetres in new[] { 7, 13 })
        {
            var definition = Definition(DesignMeasure.FromTwiceMetres(twiceMetres));
            VerifyClearDiameterDiagnostic(definition, $"a fractional {definition.ClearDiameter.Metres:0.#} m");
        }

        VerifyClearDiameterDiagnostic(Definition(DesignMeasure.Zero), "a zero");
        VerifyClearDiameterDiagnostic(Definition(DesignMeasure.FromMetres(-3)), "a negative");
    }

    private static void VerifyClearDiameterDiagnostic(BarbetteDefinition definition, string label)
    {
        var diagnostics = definition.Validate().ToList();
        var clearDiameter = diagnostics.Where(diagnostic =>
            diagnostic.Code == DesignDiagnosticCodes.BarbetteClearDiameterInvalid).ToArray();
        Require(clearDiameter.Length == 1,
            $"A barbette with {label} clear diameter must report exactly one BAR009; got " +
            $"{diagnostics.Count(diagnostic => diagnostic.Code == DesignDiagnosticCodes.BarbetteClearDiameterInvalid)}.");
        Require(clearDiameter[0].Field == nameof(BarbetteDefinition.ClearDiameter),
            $"The BAR009 for {label} clear diameter must name the ClearDiameter field.");
        Require(clearDiameter[0].Severity == DesignSeverity.Error,
            $"The BAR009 for {label} clear diameter must be an error.");
        Require(clearDiameter[0].Requested == definition.ClearDiameter,
            $"The BAR009 for {label} clear diameter must report the requested value.");
        Require(!diagnostics.Any(diagnostic =>
                diagnostic.Code is DesignDiagnosticCodes.BarbetteEvenWidthCenterline
                    or BarbetteDiagnosticCodes.CenterParityMismatch),
            $"A {label} clear diameter must not be reported as a centre parity problem " +
            "(BAR015/BAR107).");

        // The rasterizer boundary fails closed: no measurement and no physical intent escape.
        var measured = BarbetteGenerator.Measure(definition, DesignMeasure.Zero, DesignMeasure.Zero);
        Require(!measured.IsValid && measured.Measurement is null &&
                measured.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.BarbetteClearDiameterInvalid),
            $"A {label} clear diameter must not produce a raster measurement.");
    }

    private static void VerifyClearDiameterProgressionHelpers()
    {
        Require(BarbetteDefinition.NextSupportedClearDiameter(DesignMeasure.FromMetres(3)) ==
                DesignMeasure.FromMetres(5), "The next clear diameter above 3 m must be 5 m.");
        Require(BarbetteDefinition.NextSupportedClearDiameter(DesignMeasure.FromMetres(4)) ==
                DesignMeasure.FromMetres(5), "The next clear diameter above 4 m must be 5 m.");
        Require(BarbetteDefinition.NextSupportedClearDiameter(DesignMeasure.FromMetres(1)) ==
                DesignMeasure.FromMetres(3), "The next clear diameter above 1 m must be 3 m.");
        Require(BarbetteDefinition.NextSupportedClearDiameter(DesignMeasure.Zero) ==
                DesignMeasure.FromMetres(1), "A value below the minimum must advance to 1 m.");
        Require(BarbetteDefinition.NextSupportedClearDiameter(DesignMeasure.FromTwiceMetres(7)) ==
                DesignMeasure.FromMetres(5), "The next clear diameter above 3.5 m must be 5 m.");

        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromMetres(5)) ==
                DesignMeasure.FromMetres(3), "The previous clear diameter below 5 m must be 3 m.");
        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromMetres(4)) ==
                DesignMeasure.FromMetres(3), "The previous clear diameter below 4 m must be 3 m.");
        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromMetres(3)) ==
                DesignMeasure.FromMetres(1), "The previous clear diameter below 3 m must be 1 m.");
        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromMetres(1)) is null,
            "There is no supported clear diameter below 1 m.");
        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromTwiceMetres(13)) ==
                DesignMeasure.FromMetres(5), "The previous clear diameter below 6.5 m must be 5 m.");

        // Boundary safety: extreme DesignMeasure values must clamp to a legal value, never overflow
        // or return an out-of-range diameter.
        Require(BarbetteDefinition.IsSupportedClearDiameter(BarbetteDefinition.MaximumSupportedClearDiameter),
            "The maximum supported clear diameter must itself be a legal value.");
        Require(BarbetteDefinition.NextSupportedClearDiameter(DesignMeasure.FromTwiceMetres(int.MaxValue)) ==
                BarbetteDefinition.MaximumSupportedClearDiameter,
            "The next diameter above an extreme input must clamp to the maximum, not overflow.");
        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromTwiceMetres(int.MaxValue)) ==
                BarbetteDefinition.MaximumSupportedClearDiameter,
            "The previous diameter below an extreme input must clamp to the maximum, not overflow.");
        Require(BarbetteDefinition.PreviousSupportedClearDiameter(DesignMeasure.FromTwiceMetres(int.MinValue)) is null,
            "The previous diameter below the minimum must be null for an extreme negative input.");
        Require(BarbetteDefinition.NextSupportedClearDiameter(BarbetteDefinition.MaximumSupportedClearDiameter) ==
                BarbetteDefinition.MaximumSupportedClearDiameter,
            "Nudging above the maximum must stay at the maximum legal value.");
    }

    /// <summary>
    /// The arrangement measurement of a barbette node uses the actual evaluated centre plane and a
    /// whole-metre Z anchor. A 7 m clear diameter on an odd hull must therefore measure valid with a
    /// whole-metre centre and no parity diagnostic.
    /// </summary>
    private static void VerifyArrangementMeasurementUsesWholeMetreAnchor()
    {
        var parameters = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.None,
            Superstructure = SuperstructureSettings.Default with { Enabled = false },
        };
        var definition = BarbetteDefinition.Create("barbette", "ring",
            DesignMeasure.FromMetres(7), 3, neckClearSizeMetres: 3);
        var node = ArrangementNode.Create("ring", ArrangementNodeKind.Barbette, "barbette",
            DesignMeasure.FromMetres(4)) with { RequestedCenter = DesignMeasure.FromMetres(45.5) };
        var arrangement = new Arrangement([node], [], [], DesignMeasure.Zero, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute);
        var document = ShipDocument.CreateNew("Frame anchor", parameters, "layout-frame-anchor") with
        {
            Arrangement = arrangement,
            Datum = new LayoutDatum(DesignMeasure.FromMetres(40.5), DesignMeasure.Zero),
            Barbettes = [definition],
        };

        var result = new ShipGenerationService().Generate(document, 3, EmptyCatalog());
        Require(result.IsValid, Describe(result.Diagnostics));
        var solved = result.Snapshot!.Arrangement.Nodes.Single();
        Require(solved.WorldZCenter.IsWholeMetre,
            $"The 7 m barbette arrangement measurement must use a whole-metre Z anchor; got " +
            $"{solved.WorldZCenter.Metres:0.##} m.");
        Require(solved.WorldZCenter == DesignMeasure.FromMetres(-5),
            "The 7 m barbette at ruler 45.5 m on the 40.5 m bow datum must sit at world Z = -5 m.");
        Require(!result.Diagnostics.Any(diagnostic =>
                diagnostic.Code is BarbetteDiagnosticCodes.CenterParityMismatch
                    or DesignDiagnosticCodes.BarbetteEvenWidthCenterline
                    or DesignDiagnosticCodes.BarbetteClearDiameterInvalid),
            "A valid 7 m diameter must not report a parity or clear-diameter diagnostic. " +
            Describe(result.Diagnostics));

        // The same measurement through the public rasterizer with the real centre plane and Z = 0.
        var context = HullGenerator.CreateContext(parameters);
        var measured = BarbetteGenerator.Measure(definition, context.CenterPlaneX, DesignMeasure.Zero);
        Require(measured.IsValid && measured.Measurement is not null,
            "A 7 m diameter measured at the evaluated centre plane and Z = 0 must be valid. " +
            Describe(measured.Diagnostics));
    }

    /// <summary>HF-05: diameter must not choose a temporary measurement parity.</summary>
    private static void VerifyCompatibleMeasurementCenterRetired()
    {
        var retired = typeof(ShipGenerationService).GetMethod("CompatibleMeasurementCenter",
            BindingFlags.NonPublic | BindingFlags.Static);
        Require(retired is null,
            "CompatibleMeasurementCenter must be retired so the clear diameter cannot choose a " +
            "temporary measurement parity.");
    }

    private static void VerifyFrameIdentityAndRulerConversions()
    {
        var frame = new ResolvedLayoutFrame(
            DesignMeasure.FromMetres(40.5), DesignMeasure.FromMetres(91), DesignMeasure.Zero,
            "doc-1", 5, 2, "catalog-x");
        Require(frame.IsCurrent("doc-1", 5), "A frame must be current for its own document and revision.");
        Require(!frame.IsCurrent("doc-1", 6), "A frame must be stale for another revision.");
        Require(!frame.IsCurrent("doc-2", 5), "A frame must be stale for another document.");
        Require(!frame.IsCurrent("doc-2", 6), "A frame must be stale for another document and revision.");
        Require(!frame.Validate().HasErrors(), "A well-formed derived frame must validate.");
        Require(frame.RulerCenterToWorldZ(DesignMeasure.FromMetres(10)) == DesignMeasure.FromMetres(30.5),
            "Ruler-to-world must follow z = BowDatum - s.");
        Require(frame.WorldZToRulerCenter(DesignMeasure.FromMetres(30.5)) == DesignMeasure.FromMetres(10),
            "World-to-ruler must follow s = BowDatum - z.");

        var outOfRange = frame with
        {
            BowDatum = DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres + 2),
        };
        Require(outOfRange.Validate().HasErrors(),
            "A frame outside the design bounds must fail validation rather than be published.");
        var missingIdentity = frame with { SourceDocumentId = " " };
        Require(missingIdentity.Validate().HasErrors(),
            "A frame without a source document identity must fail validation.");
    }

    private static void VerifyFrameResolverUnsupportedHullFailsClosed()
    {
        var valid = LayoutFrameResolver.Resolve(HullParameters.Default, "doc", 7, 3, "cat");
        Require(valid.IsResolved, Describe(valid.Diagnostics));
        Require(valid.Frame!.SourceDocumentId == "doc" && valid.Frame.SourceRevision == 7 &&
                valid.Frame.JobId == 3 && valid.Frame.CatalogIdentity == "cat",
            "A resolved frame must carry its document, revision, job and catalog identity.");
        Require(valid.Frame.BowDatum == DesignMeasure.FromMetres(40.5) &&
                valid.Frame.SupportedRulerEnd == DesignMeasure.FromMetres(91) &&
                valid.Frame.CenterPlaneX == DesignMeasure.Zero,
            "The default hull frame must use its exact supported deck interval.");

        var deckless = LayoutFrameResolver.Resolve(HullParameters.Default with { DeckArmor = null },
            "doc", 7, 4, null);
        Require(!deckless.IsResolved && deckless.Frame is null &&
                deckless.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == LayoutFrameDiagnosticCodes.FrameUnavailable),
            "A hull without a structural deck interval must return no frame with a diagnostic, " +
            "never a zeroed frame.");
    }

    /// <summary>
    /// Resolving a derived frame synchronously and asynchronously must not touch editor history.
    /// </summary>
    private static void VerifySynchronousPreparationAndNoDerivedHistory()
    {
        var document = ShipDocument.CreateNew("Session", HullParameters.Default, "session-doc");
        var session = new EditorSession(document);
        var revision = session.Revision;
        var canUndo = session.CanUndo;
        var canRedo = session.CanRedo;
        var isDirty = session.IsDirty;

        using var preparation = new LayoutFramePreparation();
        var synchronous = preparation.ResolveNow(document.Hull, document.DocumentId, revision, "cat");
        Require(synchronous.IsResolved && synchronous.Frame!.SourceRevision == revision,
            "The synchronous first-Add resolve must produce a frame for the current revision. " +
            Describe(synchronous.Diagnostics));

        var asynchronous = preparation.ResolveAsync(document.Hull, document.DocumentId, revision, "cat")
            .GetAwaiter().GetResult();
        Require(asynchronous.IsResolved && asynchronous.Frame!.SourceRevision == revision,
            "The asynchronous prepare must produce a frame for the current revision. " +
            Describe(asynchronous.Diagnostics));

        Require(session.Revision == revision && session.CanUndo == canUndo &&
                session.CanRedo == canRedo && session.IsDirty == isDirty &&
                ReferenceEquals(session.Document, document),
            "Resolving a derived layout frame must not mutate the editor revision, undo/redo state, " +
            "dirty state or document.");
    }

    /// <summary>A superseded job must never publish, even after it completes.</summary>
    private static void VerifyStaleJobCannotPublish()
    {
        var gates = new ConcurrentDictionary<long, ManualResetEventSlim>();
        using var preparation = new LayoutFramePreparation(job =>
        {
            var gate = gates.GetOrAdd(job.JobId, _ => new ManualResetEventSlim(false));
            gate.Wait(job.CancellationToken);
            return new LayoutFrameResolution(FrameFor(job), []);
        });

        var request = new LayoutFrameRequest(HullParameters.Default, "doc", 4, "cat");
        var firstTask = preparation.ResolveAsync(request);
        var firstJob = preparation.CurrentJobId;
        var secondTask = preparation.ResolveAsync(request);
        var secondJob = preparation.CurrentJobId;
        Require(secondJob > firstJob, "Each preparation must receive a strictly increasing job identity.");

        gates.GetOrAdd(secondJob, _ => new ManualResetEventSlim(false)).Set();
        var secondResult = secondTask.GetAwaiter().GetResult();
        Require(secondResult.Frame is not null &&
                preparation.CurrentFor("doc", 4)?.JobId == secondJob,
            "The newest job must publish its frame.");

        gates.GetOrAdd(firstJob, _ => new ManualResetEventSlim(false)).Set();
        var firstResult = firstTask.GetAwaiter().GetResult();
        Require(firstResult.Frame is null,
            "A superseded job must not return or publish a stale frame.");
        Require(preparation.CurrentFor("doc", 4)?.JobId == secondJob,
            "A superseded job must not replace the newer published frame.");
    }

    private static void VerifyNewerJobSupersedesOlder()
    {
        var gates = new ConcurrentDictionary<long, ManualResetEventSlim>();
        using var preparation = new LayoutFramePreparation(job =>
        {
            var gate = gates.GetOrAdd(job.JobId, _ => new ManualResetEventSlim(false));
            gate.Wait(job.CancellationToken);
            return new LayoutFrameResolution(FrameFor(job), []);
        });

        var request = new LayoutFrameRequest(HullParameters.Default, "doc", 9, "cat");
        var older = preparation.ResolveAsync(request);
        var olderJob = preparation.CurrentJobId;
        var newer = preparation.ResolveAsync(request);
        var newerJob = preparation.CurrentJobId;

        gates.GetOrAdd(newerJob, _ => new ManualResetEventSlim(false)).Set();
        newer.GetAwaiter().GetResult();
        Require(preparation.CurrentFor("doc", 9)?.JobId == newerJob,
            "The newer job must become current.");
        Require(preparation.CurrentFor("doc", 8) is null,
            "A frame published for one revision must not satisfy another revision.");

        gates.GetOrAdd(olderJob, _ => new ManualResetEventSlim(false)).Set();
        var olderResult = older.GetAwaiter().GetResult();
        Require(olderResult.Frame is null && preparation.CurrentFor("doc", 9)?.JobId == newerJob,
            "Starting a newer job must supersede the older one permanently.");
    }

    private static void VerifyCancelledJobDoesNotPublish()
    {
        var started = new ManualResetEventSlim(false);
        using var preparation = new LayoutFramePreparation(job =>
        {
            started.Set();
            job.CancellationToken.WaitHandle.WaitOne();
            job.CancellationToken.ThrowIfCancellationRequested();
            return new LayoutFrameResolution(FrameFor(job), []);
        });

        using var cancellation = new CancellationTokenSource();
        var task = preparation.ResolveAsync(new LayoutFrameRequest(
            HullParameters.Default, "doc", 2, null, cancellation.Token));
        Require(started.Wait(TimeSpan.FromSeconds(10)), "The cancelled-job probe did not start.");
        cancellation.Cancel();
        var result = task.GetAwaiter().GetResult();
        Require(result.Frame is null && preparation.CurrentFor("doc", 2) is null,
            "A cancelled preparation must not publish a frame.");
    }

    /// <summary>
    /// A preparation for an older revision of the same document must never roll the published frame
    /// backwards, even though it is the newest job: stale work cannot overwrite current state.
    /// </summary>
    private static void VerifyStaleRevisionCannotReplaceNewerFrame()
    {
        using var preparation = new LayoutFramePreparation();

        var newer = preparation.ResolveNow(HullParameters.Default, "doc", revision: 5, "cat");
        Require(newer.IsResolved && preparation.CurrentFor("doc", 5)?.SourceRevision == 5,
            "A frame for the current revision must publish.");

        var stale = preparation.ResolveNow(HullParameters.Default, "doc", revision: 4, "cat");
        Require(stale.Frame is null,
            "A request for an older revision must not be returned as a resolved current frame.");
        Require(preparation.CurrentFor("doc", 5)?.SourceRevision == 5,
            "An older-revision resolve must not replace the newer published frame.");
        Require(preparation.CurrentFor("doc", 4) is null,
            "The older-revision frame must not become current.");

        var newest = preparation.ResolveNow(HullParameters.Default, "doc", revision: 6, "cat");
        Require(newest.IsResolved && preparation.CurrentFor("doc", 6)?.SourceRevision == 6,
            "A genuinely newer revision must still replace the published frame.");

        // Revisions of a different document are independent and must not be compared.
        var otherDocument = preparation.ResolveNow(HullParameters.Default, "other-doc", revision: 1, "cat");
        Require(otherDocument.IsResolved && preparation.CurrentFor("other-doc", 1)?.SourceRevision == 1,
            "A lower revision of a different document must publish normally.");
    }

    /// <summary>
    /// First-Add must bind the frame to the current revision and measured bow datum, never the
    /// default <see cref="LayoutDatum.Origin"/> reinterpreted later.
    /// </summary>
    private static void VerifyFirstAddFrameIdentity()
    {
        var document = ShipDocument.CreateNew("First add", HullParameters.Default, "first-add-doc");
        const long revision = 12;
        using var preparation = new LayoutFramePreparation();
        var resolution = preparation.ResolveNow(document.Hull, document.DocumentId, revision, "cat");
        Require(resolution.IsResolved, Describe(resolution.Diagnostics));
        var frame = resolution.Frame!;
        Require(frame.SourceRevision == revision,
            "The first-Add frame must report the current revision.");
        Require(frame.SourceDocumentId == document.DocumentId,
            "The first-Add frame must report the current document identity.");
        Require(frame.IsCurrent(document.DocumentId, revision),
            "The first-Add frame must be current for its document and revision.");
        Require(preparation.CurrentFor(document.DocumentId, revision) is not null,
            "CurrentFor must return the frame for the current revision.");
        Require(preparation.CurrentFor(document.DocumentId, revision - 1) is null,
            "CurrentFor must not return a frame for a previous revision.");
        Require(preparation.CurrentFor("other-document", revision) is null,
            "CurrentFor must not return a frame for another document.");
        Require(frame.BowDatum == DesignMeasure.FromMetres(40.5) &&
                frame.BowDatum != LayoutDatum.Origin.LayoutBowZ,
            "First-Add must use the measured forward supported deck face, not the default origin.");
    }

    private static ResolvedLayoutFrame FrameFor(LayoutFrameJob job) => new(
        DesignMeasure.FromMetres(40.5), DesignMeasure.FromMetres(91), DesignMeasure.Zero,
        job.DocumentId, job.Revision, job.JobId, job.CatalogIdentity);

    private static BarbetteDefinition Definition(DesignMeasure clearDiameter) =>
        BarbetteDefinition.Create("barbette", "ring", clearDiameter, 3, neckClearSizeMetres: 3);

    private static FtdBlockCatalog EmptyCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "HullForge-LayoutFrame-EmptyCatalog");
        Directory.CreateDirectory(Path.Combine(root, "From_The_Depths_Data", "StreamingAssets"));
        return FtdBlockCatalog.Load(root);
    }

    private static string Describe(IEnumerable<DesignDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
