using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Sketchbook;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.UI.Sketchbook;
using static SlopeFillTestSupport;

/// <summary>
/// The user-facing Hull Presets product surface: the single editor button, the modeless window
/// opened on launch, the 16 minimal catalog-driven cards, the native thumbnail and section-inset
/// renderer and the one-transaction apply wiring. Every check is written to fail against a
/// count-only, hardcoded, prose-carrying or duplicated-catalog UI.
/// </summary>
internal static class SketchbookProductSurfaceTests
{
    private static readonly string[] FastThumbnailIds =
    [
        "ans-01-channel-knife",
        "ans-03-shoal-water-gunboat",
        "ans-15-tumblehome-citadel",
        "ans-16-coastal-siege-dreadnought",
    ];

    /// <summary>Every presentation member the minimal card contract removed from the UI.</summary>
    private static readonly string[] RemovedProseMembers =
    [
        "Role",
        "Provenance",
        "Traits",
        "RecommendedSmoothingNote",
        "ThumbnailLabel",
        "SectionInsetLabel",
    ];

    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        VerifyXamlSurface();
        VerifySectionEvidence(generator);

        RunSta("hull presets launch window", TimeSpan.FromSeconds(60), () =>
        {
            EnsureApplication();
            VerifyLaunchWindow();
        });

        RunSta("hull presets browser coverage", TimeSpan.FromSeconds(60), () =>
        {
            EnsureApplication();
            VerifyBrowserCoverage();
        });

        RunSta("hull presets native thumbnails", TimeSpan.FromSeconds(full ? 300 : 90), () =>
        {
            EnsureApplication();
            VerifyNativeThumbnails(catalog, full);
        });

        RunSta("hull presets apply semantics", TimeSpan.FromSeconds(180), () =>
        {
            EnsureApplication();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            VerifyApplySemantics(catalog);
        });

        Console.WriteLine(full
            ? "Hull Presets product surface: XAML separation, launch-open window lifecycle, " +
              "16-card catalog coverage, family grouping, minimal no-copy cards, native thumbnails " +
              "for all 16 entries, section insets, fixture-matched section evidence and " +
              "one-transaction apply semantics passed."
            : "Hull Presets product surface: XAML separation, launch-open window lifecycle, " +
              "16-card catalog coverage, family grouping, minimal no-copy cards, representative " +
              "native thumbnails, section insets, fixture-matched section evidence and " +
              "one-transaction apply semantics passed.");
    }

    // ---------------------------------------------------------------------------------------
    // 1. XAML surface, window separation and catalog cleanliness.
    // ---------------------------------------------------------------------------------------

    private static void VerifyXamlSurface()
    {
        var root = FindRepositoryRoot();
        var mainWindowPath = Path.Combine(root, "FtdHullGenerator", "MainWindow.xaml");
        var windowPath = Path.Combine(root, "FtdHullGenerator", "UI", "Sketchbook", "HullPresetsWindow.xaml");
        var browserPath = Path.Combine(root, "FtdHullGenerator", "UI", "Sketchbook", "SketchbookBrowser.xaml");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // The editor hosts one plain button. The browser lives in the modeless Hull Presets
        // window, never inline in the editor column.
        var mainXaml = XDocument.Load(mainWindowPath);
        var mainElements = mainXaml.Descendants().ToArray();
        var buttonIndex = Array.FindIndex(mainElements, element =>
            element.Name.LocalName == "Button" && (string?)element.Attribute(x + "Name") == "HullPresetsButton");
        Require(buttonIndex >= 0, "The normal editor does not host the Hull Presets button.");
        Require((string?)mainElements[buttonIndex].Attribute("Click") == "OpenHullPresetsClicked",
            "The Hull Presets button is not wired to open the presets window.");
        Require(!mainElements.Any(element => element.Name.LocalName == "SketchbookBrowser"),
            "The editor still hosts the sketchbook browser inline instead of in the Hull Presets window.");

        var window = XDocument.Load(windowPath);
        var windowElements = window.Descendants().ToArray();
        Require(windowElements.Any(element => element.Name.LocalName == "SketchbookBrowser"),
            "The Hull Presets window does not host the sketchbook browser.");
        Require((string?)window.Root?.Attribute("Title") == "Hull Presets",
            $"The presets window is not titled 'Hull Presets': {window.Root?.Attribute("Title")}.");

        var dimensionsIndex = Array.FindIndex(mainElements, element =>
            element.Name.LocalName == "Expander" && (string?)element.Attribute("Header") == "Hull dimensions");
        Require(dimensionsIndex >= 0, "The hull-dimensions pane is missing from the editor.");
        Require(buttonIndex < dimensionsIndex,
            "The Hull Presets button does not appear ahead of the manual dimensions.");

        // The legacy historical library is no longer a UI surface anywhere in the editor.
        Require(!mainElements.Any(element => element.Name.LocalName == "HistoricalLibraryBrowser"),
            "The editor still hosts the legacy historical library browser.");
        Require(!mainElements.Any(element =>
                (string?)element.Attribute("Header") == "Legacy Historical Starts"),
            "The editor still hosts the Legacy Historical Starts pane.");

        // No catalog data may be hardcoded in any of the three sketchbook XAML files.
        foreach (var path in new[] { mainWindowPath, windowPath, browserPath })
        {
            var markup = File.ReadAllText(path);
            var fileName = Path.GetFileName(path);
            foreach (var entry in AlternateNavalSketchbookCatalog.All)
            {
                Require(!markup.Contains(entry.Name, StringComparison.Ordinal),
                    $"{fileName} hardcodes the entry name '{entry.Name}'.");
                Require(!markup.Contains(entry.Id, StringComparison.Ordinal),
                    $"{fileName} hardcodes the entry id '{entry.Id}'.");
                Require(!markup.Contains(entry.Provenance, StringComparison.Ordinal),
                    $"{fileName} hardcodes the provenance of '{entry.Id}'.");
            }
        }

        // The card markup itself carries no family, dimension summary or trait string. Short
        // traits like 'hard chine' are legitimate body-form wording elsewhere in the editor, so
        // this stricter check is scoped to the sketchbook markup that renders the cards.
        foreach (var path in new[] { windowPath, browserPath })
        {
            var markup = File.ReadAllText(path);
            var fileName = Path.GetFileName(path);
            foreach (var entry in AlternateNavalSketchbookCatalog.All)
            {
                Require(!markup.Contains(entry.Family, StringComparison.Ordinal) &&
                        !markup.Contains(entry.Family.Replace("&", "&amp;", StringComparison.Ordinal), StringComparison.Ordinal),
                    $"{fileName} hardcodes the family '{entry.Family}'.");
                Require(!markup.Contains(entry.EnvelopeSummary, StringComparison.Ordinal) &&
                        !markup.Contains(entry.MinimumSummary, StringComparison.Ordinal),
                    $"{fileName} hardcodes a dimension summary of '{entry.Id}'.");
                foreach (var trait in entry.Traits)
                    Require(!markup.Contains(trait, StringComparison.Ordinal),
                        $"{fileName} hardcodes the trait '{trait}'.");
            }
        }

        // The minimal card contract: no removed prose is declared or bound anywhere in the
        // sketchbook markup, and the accessible name still describes the surface.
        var browserMarkup = File.ReadAllText(browserPath);
        var windowMarkup = File.ReadAllText(windowPath);
        foreach (var removed in RemovedProseMembers)
        {
            Require(!browserMarkup.Contains(removed, StringComparison.Ordinal),
                $"SketchbookBrowser.xaml still references the removed '{removed}' copy.");
            Require(!windowMarkup.Contains(removed, StringComparison.Ordinal),
                $"HullPresetsWindow.xaml still references the removed '{removed}' copy.");
        }

        Require(browserMarkup.Contains("AutomationProperties.Name=\"Hull presets\"", StringComparison.Ordinal),
            "The Hull Presets browse surface no longer carries its accessible name.");
    }

    // ---------------------------------------------------------------------------------------
    // 2. Launch window lifecycle: opens on launch, hides on close, reopens from the button.
    // ---------------------------------------------------------------------------------------

    private static void VerifyLaunchWindow()
    {
        var window = new MainWindow();
        HullPresetsWindow? presets = null;
        try
        {
            window.RunLaunchHandshakeForTests();
            PumpDispatcher();

            presets = window.HullPresetsWindowForTests;
            Require(presets.IsVisible, "The Hull Presets window did not open on launch.");
            Require(presets.Title == "Hull Presets",
                $"The launch window title is '{presets.Title}' instead of 'Hull Presets'.");
            Require(!presets.ShowInTaskbar, "The Hull Presets window is a taskbar window.");
            Require(ReferenceEquals(presets.Browser, window.SketchbookForTests),
                "The launch window does not host the editor's browser control.");

            // Closing hides the window; the button brings the same hidden instance back.
            presets.Close();
            PumpDispatcher();
            Require(!presets.IsVisible, "Closing the Hull Presets window left it visible.");
            window.OpenHullPresetsForTests();
            PumpDispatcher();
            Require(presets.IsVisible && ReferenceEquals(presets, window.HullPresetsWindowForTests),
                "The Hull Presets button did not reopen the hidden window.");
        }
        finally
        {
            window.Close();
            PumpDispatcher();
            if (presets is { IsLoaded: true })
                presets.CloseForShutdown();
        }
    }

    // ---------------------------------------------------------------------------------------
    // 3. Sixteen-card coverage, grouping and the minimal no-prose card contract.
    // ---------------------------------------------------------------------------------------

    private static void VerifyBrowserCoverage()
    {
        var browser = new SketchbookBrowser();
        Require(browser.Cards.Count == 16,
            $"The browser exposes {browser.Cards.Count} cards instead of 16.");
        Require(browser.Cards.Select(card => card.Entry.Id)
                .SequenceEqual(AlternateNavalSketchbookCatalog.All.Select(entry => entry.Id)),
            "The browser does not present the cards in catalog order.");

        for (var index = 0; index < browser.Cards.Count; index++)
        {
            var card = browser.Cards[index];
            var entry = AlternateNavalSketchbookCatalog.All[index];
            Require(ReferenceEquals(card.Entry, entry),
                $"Card {index} does not hold catalog entry '{entry.Id}' by reference.");

            Require(!string.IsNullOrWhiteSpace(card.Family),
                $"Card '{entry.Id}' has no presentation family.");
            Require(card.SuggestedSummary == entry.EnvelopeSummary && card.MinimumSummary == entry.MinimumSummary,
                $"Card '{entry.Id}' size summaries do not come from the catalog.");
        }

        // The minimal card contract: every prose member is gone from the view model, and the
        // browser no longer declares its removed header/disclaimer copy.
        foreach (var removed in RemovedProseMembers)
            Require(!HasInstanceProperty(typeof(SketchbookCardViewModel), removed),
                $"SketchbookCardViewModel still exposes the removed '{removed}' member.");
        Require(typeof(SketchbookBrowser).GetField("Disclaimer", BindingFlags.Public | BindingFlags.Static) is null,
            "SketchbookBrowser still declares the removed fixed disclaimer.");
        Require(!HasInstanceProperty(typeof(SketchbookBrowser), "ProvenanceNoteForTests") &&
                !HasInstanceProperty(typeof(SketchbookBrowser), "DisclaimerForTests"),
            "SketchbookBrowser still exposes the removed header copy to the surface.");

        Require(browser.Cards.Single(card => card.Ordinal == 15).ShowsSectionInset &&
                browser.Cards.Single(card => card.Ordinal == 3).ShowsSectionInset &&
                browser.Cards.Single(card => card.Ordinal == 16).ShowsSectionInset,
            "The lower-body-dependent identities (03, 15, 16) do not request a section inset.");
        Require(!browser.Cards.Single(card => card.Ordinal == 8).ShowsSectionInset,
            "A card that does not depend on its lower body requested a section inset.");

        var view = browser.CardViewForTests as ListCollectionView;
        Require(view?.Groups is not null, "The card list is not grouped for the browser.");
        var groups = view!.Groups!.Cast<CollectionViewGroup>().ToArray();
        Require(groups.Length == 4, $"The cards group into {groups.Length} families instead of 4.");
        var counts = groups.ToDictionary(group => (string)group.Name!, group => group.ItemCount, StringComparer.Ordinal);
        Require(counts.TryGetValue("Torpedo & patrol craft", out var patrol) && patrol == 4 &&
                counts.TryGetValue("Destroyers & leaders", out var leaders) && leaders == 2 &&
                counts.TryGetValue("Cruisers", out var cruisers) && cruisers == 5 &&
                counts.TryGetValue("Capital ships", out var capital) && capital == 5,
            "The family grouping is not 4/2/5/5 across the four families.");
    }

    // ---------------------------------------------------------------------------------------
    // 4. Native thumbnails and section insets.
    // ---------------------------------------------------------------------------------------

    private static void VerifyNativeThumbnails(FtdBlockCatalog catalog, bool full)
    {
        var ids = full
            ? AlternateNavalSketchbookCatalog.All.Select(entry => entry.Id).ToArray()
            : FastThumbnailIds;

        var window = new MainWindow();
        try
        {
            window.MarkLoadedForTests();
            window.SetCatalogForTests(catalog);
            var populated = window.RenderSketchbookThumbnailsForTests(ids);
            Require(populated == ids.Length,
                $"Only {populated} of {ids.Length} requested Hull Presets thumbnails were populated.");

            var cards = window.SketchbookForTests.Cards;
            foreach (var id in ids)
            {
                var card = cards.Single(candidate => candidate.Entry.Id == id);
                Require(card.Thumbnail is not null,
                    $"Card '{id}' has no native thumbnail. Error: {card.ThumbnailError ?? "none"}");
                Require(card.ThumbnailError is null, $"Card '{id}' reported a thumbnail error: {card.ThumbnailError}");
                Require(card.Thumbnail!.PixelWidth == SketchbookThumbnailRenderer.DefaultWidth &&
                        card.Thumbnail.PixelHeight == SketchbookThumbnailRenderer.DefaultHeight,
                    $"Card '{id}' thumbnail is {card.Thumbnail.PixelWidth}x{card.Thumbnail.PixelHeight}.");
                Require(!card.ShowsSectionInset || card.SectionInset is not null,
                    $"Card '{id}' requests a section inset but has none. Error: {card.ThumbnailError ?? "none"}");
            }

            var card15 = cards.Single(card => card.Entry.Id == "ans-15-tumblehome-citadel");
            Require(card15.SectionInset is not null,
                $"The tumblehome card has no section inset. Error: {card15.ThumbnailError ?? "none"}");
            Require(card15.SectionInset!.PixelWidth == SketchbookThumbnailRenderer.DefaultWidth &&
                    card15.SectionInset.PixelHeight == SketchbookThumbnailRenderer.DefaultHeight,
                "The tumblehome section inset is not the expected pixel size.");
            Require(BitmapHash(card15.SectionInset) != BitmapHash(card15.Thumbnail!),
                "The tumblehome section inset is pixel-identical to its isometric thumbnail.");

            // The shipped inset must be measured on the same neutral catalog basis the committed
            // fixture used, so the section the user sees is the section the audit verified.
            var entry15 = AlternateNavalSketchbookCatalog.Find("ans-15-tumblehome-citadel")!;
            var rendererHull15 = SketchbookThumbnailRenderer.GenerateHull(entry15, catalog, out var resolved15);
            Require(resolved15, "The tumblehome thumbnail hull was not catalog-resolved.");
            var rendererEvidence15 = SketchbookSectionEvidenceReader.Measure(rendererHull15);
            using (var fixture15Document = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sketchbook-native-audit.json"))))
            {
                var fixture15 = SectionRow(fixture15Document.RootElement, "15");
                Require(rendererEvidence15.StationZ == fixture15.GetProperty("stationZ").GetInt32() &&
                        rendererEvidence15.ContourCount == fixture15.GetProperty("contourCount").GetInt32() &&
                        rendererEvidence15.ClosedContourCount == fixture15.GetProperty("closedContourCount").GetInt32() &&
                        rendererEvidence15.MaxHalfBreadth == fixture15.GetProperty("maxHalfBreadth").GetDouble() &&
                        rendererEvidence15.MaxHalfBreadthY == fixture15.GetProperty("maxHalfBreadthY").GetInt32() &&
                        rendererEvidence15.TopOccupiedY == fixture15.GetProperty("topOccupiedY").GetInt32() &&
                        rendererEvidence15.TumblehomeDelta == fixture15.GetProperty("tumblehomeDelta").GetDouble() &&
                        rendererEvidence15.HasClosedContour == fixture15.GetProperty("anyClosedContour").GetBoolean(),
                    "The shipped tumblehome section inset is not measured on the committed fixture basis.");
            }

            var card01 = cards.Single(card => card.Entry.Id == "ans-01-channel-knife");
            var card03 = cards.Single(card => card.Entry.Id == "ans-03-shoal-water-gunboat");
            Require(BitmapHash(card01.Thumbnail!) != BitmapHash(card03.Thumbnail!),
                "Two different sketchbook entries produced identical thumbnail content.");

            // One resolved-snapshot path: the renderer's generated hull must be the exact
            // catalog-resolved snapshot hull the editor would export for the same entry.
            var entry01 = AlternateNavalSketchbookCatalog.Find("ans-01-channel-knife")!;
            var rendererHull = SketchbookThumbnailRenderer.GenerateHull(entry01, catalog, out var resolved);
            Require(resolved, "GenerateHull with an installed catalog did not report a catalog-resolved hull.");
            var basis = ShipDocument.CreateNew(entry01.Name, HullParameters.Default);
            var document = SketchbookShapeInitializer.Apply(
                entry01, SketchbookSizePolicy.SuggestedDimensions, basis).Document;
            var snapshot = new ShipGenerationService().Generate(document, 1, catalog).Snapshot;
            Require(snapshot is not null, "ShipGenerationService returned no snapshot for entry 01.");
            Require(Cells(rendererHull).SetEquals(Cells(snapshot!.Hull)),
                "The renderer hull does not occupy the same cells as the resolved snapshot hull.");

            // The provisional no-catalog renderer path is still labelled honestly at the API
            // level even though the minimal card no longer prints a caption.
            var provisional = SketchbookThumbnailRenderer.Render(
                entry01, null, SketchbookThumbnailView.Isometric);
            Require(!provisional.CatalogResolved &&
                    provisional.StatusLine.Contains("provisional, no installed catalog", StringComparison.Ordinal),
                $"The provisional thumbnail is not labelled honestly: {provisional.StatusLine}");
            SketchbookThumbnailRenderer.GenerateHull(entry01, null, out var provisionalResolved);
            Require(!provisionalResolved, "GenerateHull without a catalog reported a catalog-resolved hull.");

            // A catalog change must drop every rendered image, never leaving a stale thumbnail.
            var staleCard = cards.Single(card => card.Entry.Id == "ans-15-tumblehome-citadel");
            window.SketchbookForTests.InvalidateThumbnails();
            Require(staleCard.Thumbnail is null && staleCard.SectionInset is null,
                "A catalog invalidation left a stale Hull Presets thumbnail on the card.");
        }
        finally
        {
            window.Close();
        }

        // With no installed catalog the surface must still draw real native previews rather than
        // leaving every card pending forever.
        var provisionalWindow = new MainWindow();
        try
        {
            provisionalWindow.MarkLoadedForTests();
            var provisionalPopulated = provisionalWindow.RenderSketchbookThumbnailsForTests(["ans-01-channel-knife"]);
            Require(provisionalPopulated == 1,
                $"The no-catalog browser populated {provisionalPopulated} of 1 provisional thumbnails.");
            var provisionalCard = provisionalWindow.SketchbookForTests.Cards
                .Single(card => card.Entry.Id == "ans-01-channel-knife");
            Require(provisionalCard.Thumbnail is not null,
                $"The no-catalog browser left a card pending. Error: {provisionalCard.ThumbnailError ?? "none"}");
        }
        finally
        {
            provisionalWindow.Close();
        }
    }

    // ---------------------------------------------------------------------------------------
    // 5. Section evidence matches the committed native audit fixture.
    // ---------------------------------------------------------------------------------------

    private static void VerifySectionEvidence(HullGenerator generator)
    {
        var evidence15 = MeasureSuggested(generator, "ans-15-tumblehome-citadel");
        Require(evidence15.MaxHalfBreadth == 14 && evidence15.MaxHalfBreadthY == 10 &&
                evidence15.TopOccupiedY == 15 && evidence15.TumblehomeDelta == 4 &&
                evidence15.HasClosedContour && evidence15.IsTumblehome,
            $"Candidate 15 section evidence is {evidence15} instead of the tumblehome signature.");

        var evidence10 = MeasureSuggested(generator, "ans-10-treaty-breaker");
        Require(evidence10.TumblehomeDelta == 0 && !evidence10.IsTumblehome,
            $"Candidate 10 section evidence is {evidence10} instead of a full, non-tumblehome section.");

        // Cross-check every value against the committed fixture rather than restating constants.
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sketchbook-native-audit.json");
        Require(File.Exists(fixturePath), $"The committed native-audit fixture is missing at {fixturePath}.");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var section15 = SectionRow(fixture.RootElement, "15");
        Require(evidence15.StationZ == section15.GetProperty("stationZ").GetInt32() &&
                evidence15.ContourCount == section15.GetProperty("contourCount").GetInt32() &&
                evidence15.ClosedContourCount == section15.GetProperty("closedContourCount").GetInt32() &&
                evidence15.MaxHalfBreadth == section15.GetProperty("maxHalfBreadth").GetDouble() &&
                evidence15.MaxHalfBreadthY == section15.GetProperty("maxHalfBreadthY").GetInt32() &&
                evidence15.TopOccupiedY == section15.GetProperty("topOccupiedY").GetInt32() &&
                evidence15.TumblehomeDelta == section15.GetProperty("tumblehomeDelta").GetDouble() &&
                evidence15.HasClosedContour == section15.GetProperty("anyClosedContour").GetBoolean(),
            "Candidate 15 product section evidence does not match the committed native-audit fixture.");

        var section10 = SectionRow(fixture.RootElement, "10");
        Require(evidence10.TumblehomeDelta == section10.GetProperty("tumblehomeDelta").GetDouble() &&
                evidence10.IsTumblehome ==
                    fixture.RootElement.GetProperty("probes").GetProperty("tumblehome")
                        .GetProperty("contrast").GetProperty("isTumblehome").GetBoolean(),
            "Candidate 10 product section evidence does not match the committed native-audit fixture.");

        // The cut fraction maps the measured station onto the hull's own length.
        var hull15 = SuggestedHull(generator, "ans-15-tumblehome-citadel");
        var fraction = SketchbookSectionEvidenceReader.CutFraction(hull15, evidence15);
        Require(fraction >= 0d && fraction <= 1d &&
                Math.Abs(fraction - (evidence15.StationZ - hull15.MinZ) / (double)(hull15.MaxZ - hull15.MinZ)) < 1e-12,
            $"The section cut fraction {fraction} does not map the station onto the hull length.");
    }

    private static SketchbookSectionEvidence MeasureSuggested(HullGenerator generator, string id) =>
        SketchbookSectionEvidenceReader.Measure(SuggestedHull(generator, id));

    /// <summary>
    /// The product initializer applied to <see cref="HullParameters.Default" /> reproduces the
    /// audit's exact suggested-size inputs, so the committed fixture remains the oracle.
    /// </summary>
    private static GeneratedHull SuggestedHull(HullGenerator generator, string id)
    {
        var entry = AlternateNavalSketchbookCatalog.Find(id)
            ?? throw new InvalidOperationException($"The sketchbook entry '{id}' is missing.");
        var document = SketchbookShapeInitializer.Apply(
            entry, SketchbookSizePolicy.SuggestedDimensions,
            ShipDocument.CreateNew(entry.Name, HullParameters.Default)).Document;
        return generator.Generate(document.Hull);
    }

    private static JsonElement SectionRow(JsonElement fixture, string id)
    {
        foreach (var row in fixture.GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("id").GetString() == id &&
                row.GetProperty("size").GetString() == "suggested" &&
                row.TryGetProperty("section", out var section) &&
                section.ValueKind == JsonValueKind.Object)
            {
                return section;
            }
        }

        throw new InvalidOperationException($"The fixture has no suggested section row for candidate {id}.");
    }

    // ---------------------------------------------------------------------------------------
    // 6. One-transaction apply semantics.
    // ---------------------------------------------------------------------------------------

    private static void VerifyApplySemantics(FtdBlockCatalog catalog)
    {
        var window = new MainWindow();
        try
        {
            window.MarkLoadedForTests();
            window.SetCatalogForTests(catalog);
            var editor = window.EditorSessionForTests;
            var entry01 = AlternateNavalSketchbookCatalog.Find("ans-01-channel-knife")!;

            var beforeDocument = editor.Document;
            var before = beforeDocument.Hull;
            var revisionBefore = editor.Revision;

            window.SelectSketchbookEntryForTests(entry01.Id, SketchbookSizePolicy.SuggestedDimensions);
            PumpDispatcher();

            Require(editor.Revision == revisionBefore + 1,
                $"Applying one Hull Presets entry committed {editor.Revision - revisionBefore} revisions instead of one.");
            var appliedDocument = editor.Document;
            Require(appliedDocument.Hull.Length == entry01.SuggestedLength &&
                    appliedDocument.Hull.Width == entry01.SuggestedWidth &&
                    appliedDocument.Hull.Height == entry01.SuggestedHeight,
                "The committed Hull Presets document does not carry the suggested dimensions.");
            Require(appliedDocument.Hull.EffectiveShape == entry01.Shape &&
                    appliedDocument.Hull.BowStyle == entry01.BowStyle &&
                    appliedDocument.Hull.SternStyle == entry01.SternStyle &&
                    appliedDocument.Hull.HasBulb == entry01.HasBulb &&
                    appliedDocument.Hull.Bulb == entry01.Bulb,
                "The committed Hull Presets document does not carry the exact entry geometry.");

            var applied = window.ReadParametersForTests(out var message);
            Require(applied is not null, $"The applied Hull Presets controls became unreadable: {message}");
            Require(applied!.Length == entry01.SuggestedLength && applied.Width == entry01.SuggestedWidth &&
                    applied.Height == entry01.SuggestedHeight && applied.BowStyle == entry01.BowStyle &&
                    applied.SternStyle == entry01.SternStyle && applied.EffectiveShape == entry01.Shape &&
                    applied.HasBulb == entry01.HasBulb && applied.Bulb == entry01.Bulb,
                "The editor controls do not show the applied entry exactly.");
            Require(applied.HullArmor.Equals(before.HullArmor) &&
                    Equals(applied.DeckArmor, before.DeckArmor) &&
                    applied.Smoothing == before.Smoothing &&
                    applied.Beamify == before.Beamify,
                "Applying a Hull Presets entry rewrote unrelated armor, deck, smoothing or construction state.");

            var controls = new FrameworkElement[]
            {
                window.BowFullnessSliderForTests, window.BowFlareSliderForTests,
                window.BodyFullnessSliderForTests, window.BodySideShapeSliderForTests,
                window.BodyChineSliderForTests, window.BodyFlatBottomSliderForTests,
                window.SternFullnessSliderForTests, window.SternSideShapeSliderForTests,
                window.EntranceLengthInputForTests, window.RunLengthInputForTests,
                window.BowDeckRiseInputForTests, window.SternDeckRiseInputForTests,
                window.BowKeelRiseInputForTests, window.SternKeelRiseInputForTests,
                window.BulbCheckBoxForTests, window.BodyFormBoxForTests,
                window.BowStyleBoxForTests, window.SternStyleBoxForTests,
            };
            foreach (var control in controls)
                Require(control.IsEnabled, $"Applying a Hull Presets entry disabled {control.Name}.");

            Require(editor.Undo() && editor.Document == beforeDocument,
                "Undo did not restore the pre-apply document.");
            Require(editor.Redo() && editor.Document == appliedDocument,
                "Redo did not re-apply the Hull Presets entry.");

            // Keep the current dimensions on an even-width basis: L/W/H stay, only the four rises
            // scale from the immutable baseline.
            editor.New(ShipDocument.CreateNew("Hull Presets keep",
                HullEditorSettings.Default with { Length = 100, Width = 18, Height = 8 }));
            window.SelectSketchbookEntryForTests("ans-05-high-forecastle", SketchbookSizePolicy.KeepCurrentDimensions);
            PumpDispatcher();
            var keep = editor.Document;
            Require(keep.Hull.Length == 100 && keep.Hull.Width == 18 && keep.Hull.Height == 8,
                $"Keeping the current dimensions changed them to {keep.Hull.Length}x{keep.Hull.Width}x{keep.Hull.Height}.");
            Require(keep.Hull.EffectiveShape.Profile == new HullProfileSettings(4, 0, 1, 1),
                $"Entry 05 at H=8 scaled to {keep.Hull.EffectiveShape.Profile} instead of (4,0,1,1).");

            var keepRevision = editor.Revision;
            window.SelectSketchbookEntryForTests("ans-05-high-forecastle", SketchbookSizePolicy.KeepCurrentDimensions);
            PumpDispatcher();
            Require(editor.Revision == keepRevision,
                "A repeated Keep apply created a revision instead of being a no-op.");
            Require(editor.Document.Hull.EffectiveShape.Profile == new HullProfileSettings(4, 0, 1, 1),
                "A repeated Keep apply drifted from the immutable baseline.");

            // The unclamped out-of-range rise is committed and reported, never silently clamped.
            editor.New(ShipDocument.CreateNew("Hull Presets no clamp",
                HullEditorSettings.Default with { Length = 100, Width = 18, Height = 5 }));
            window.SelectSketchbookEntryForTests("ans-07-fleet-torpedo-cruiser", SketchbookSizePolicy.KeepCurrentDimensions);
            PumpDispatcher();
            var noClamp = editor.Document;
            Require(noClamp.Hull.EffectiveShape.Profile == new HullProfileSettings(0, 1, 4, 1),
                $"Entry 07 at H=5 committed {noClamp.Hull.EffectiveShape.Profile} instead of the unclamped (0,1,4,1).");
            Require(window.StatusIsErrorForTests,
                $"An out-of-range scaled rise was not reported as an error: {window.StatusTextForTests}");
            Require(noClamp.Validate().Any(diagnostic =>
                    diagnostic.Code == DesignDiagnosticCodes.HullParametersInvalid &&
                    diagnostic.Message.Contains("Bow keel rise", StringComparison.Ordinal)),
                "The committed out-of-range rise did not produce an honest DOC009 diagnostic.");

            // With the catalog installed, the live preview resolves the committed document into one
            // export-authoritative snapshot.
            editor.New(ShipDocument.CreateNew("Hull Presets preview", HullEditorSettings.Default));
            window.SelectSketchbookEntryForTests(entry01.Id, SketchbookSizePolicy.SuggestedDimensions);
            PumpDispatcher();
            var committed = editor.Document;
            var preview = window.GeneratePreviewForTestsAsync();
            PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(60));
            preview.GetAwaiter().GetResult();

            var snapshot = window.CurrentShipSnapshotForTests;
            Require(snapshot is not null,
                $"The Hull Presets apply did not resolve a preview snapshot. Status: {window.StatusTextForTests}");
            Require(snapshot!.Document.DocumentId == committed.DocumentId &&
                    snapshot.Document.Hull == committed.Hull &&
                    (snapshot.Document with { Datum = committed.Datum }) == committed,
                "The resolved snapshot document does not match the committed editor document apart from " +
                "the derived, non-persisted layout datum.");
            // The only difference is the derived frame datum the preview composes in memory: the
            // committed document stays at its persisted origin, exactly like the barbette path.
            Require(committed.EffectiveDatum.LayoutBowZ == DesignMeasure.Zero &&
                    window.WorkspaceForTests.LayoutFrame is { } layoutFrame &&
                    snapshot.Document.EffectiveDatum.LayoutBowZ == layoutFrame.BowDatum,
                "The resolved snapshot did not carry the derived, non-persisted layout datum.");
            Require(snapshot.Hull.OccupiedLength == entry01.SuggestedLength &&
                    snapshot.Hull.OccupiedWidth == entry01.SuggestedWidth,
                $"The resolved snapshot occupies {snapshot.Hull.OccupiedLength}x{snapshot.Hull.OccupiedWidth} " +
                $"instead of the applied {entry01.SuggestedLength}x{entry01.SuggestedWidth} envelope.");
            Require(window.ExportEnabledForTests,
                $"Export was not enabled after a catalog-resolved Hull Presets preview: {window.StatusTextForTests}");
        }
        finally
        {
            window.EditorSessionForTests.New(
                ShipDocument.CreateNew("Hull Presets probe clean", HullEditorSettings.Default));
            window.Close();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    private static bool HasInstanceProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static HashSet<(int X, int Y, int Z)> Cells(GeneratedHull hull) =>
        hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();

    private static string BitmapHash(BitmapSource image)
    {
        var stride = image.PixelWidth * ((image.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null)
            return;
        var created = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        created.InitializeComponent();
        created.Navigating += (_, eventArgs) => eventArgs.Cancel = true;
        FtdHullGenerator.UI.ThemeManager.Apply(FtdHullGenerator.UI.AppTheme.Workbench);
    }

    private static void RunSta(string label, TimeSpan timeout, Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout))
            throw new InvalidOperationException($"The {label} STA probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException($"The {label} STA probe failed.", failure);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            PumpDispatcher();
        Require(condition(), "The live Hull Presets preview did not complete within its timeout.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
