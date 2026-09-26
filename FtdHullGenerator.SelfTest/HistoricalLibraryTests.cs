using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Historical;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using static SlopeFillTestSupport;

/// <summary>
/// HIS01: the frozen 2.0 historical starting catalog. The catalog is broad and sound, nation is
/// presentation metadata only, and every entry applies through the normal editor state path with
/// the full slider set staying free afterward. The catalog is no longer a UI surface; the
/// compatibility apply path is exercised through the editor probe seam.
/// </summary>
internal static class HistoricalLibraryTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        VerifyCatalogIntegrity();
        VerifyBroadGroupedCatalog();
        VerifyNationIsMetadataOnly(generator);
        VerifyRepresentativePipeline(generator, catalog, full);
        VerifyEditorApplication();

        Console.WriteLine(full
            ? $"Historical catalog: {HistoricalPresetCatalog.All.Count} entries across " +
              $"{HistoricalNations.All.Count} nations, integrity, nation-as-metadata, every preset generated, " +
              "small/medium/large export, and editor application/independence passed."
            : $"Historical catalog: {HistoricalPresetCatalog.All.Count} entries across " +
              $"{HistoricalNations.All.Count} nations, integrity, nation-as-metadata, representative " +
              "generation/export, and editor application/independence passed.");
    }

    /// <summary>Stable unique ids, valid dimensions/Shape V2 values, honest metadata.</summary>
    private static void VerifyCatalogIntegrity()
    {
        var errors = HistoricalPresetCatalog.Validate();
        Require(errors.Count == 0,
            "The historical catalog failed its own integrity contract: " + string.Join("; ", errors));

        var ids = HistoricalPresetCatalog.All.Select(preset => preset.Id).ToArray();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
            "The historical catalog contains duplicate stable ids.");
        Require(ids.All(id => id.Length > 0 && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')),
            "A historical preset id is not a stable lowercase token.");

        foreach (var preset in HistoricalPresetCatalog.All)
        {
            Require(preset.Nation is not null, $"Historical preset '{preset.Id}' has no nation metadata.");
            Require(!string.IsNullOrWhiteSpace(preset.Name) && !string.IsNullOrWhiteSpace(preset.Designation),
                $"Historical preset '{preset.Id}' has no display name or designation.");
            Require(!string.IsNullOrWhiteSpace(preset.Era) && !string.IsNullOrWhiteSpace(preset.BuildStatus),
                $"Historical preset '{preset.Id}' has no era or build status.");
            Require(!string.IsNullOrWhiteSpace(preset.PublishedDimensions) &&
                    !string.IsNullOrWhiteSpace(preset.SourceNote),
                $"Historical preset '{preset.Id}' has no published dimensions or source note.");

            // The editable envelope is an honest approximation: odd single-centreline width,
            // supported dimensions and Shape V2 values the validator accepts for that height.
            Require(preset.Width % 2 == 1, $"Historical preset '{preset.Id}' uses an even width.");
            Require(preset.Length >= HullParameters.MinimumLength &&
                    preset.Width >= HullParameters.MinimumWidth &&
                    preset.Height >= HullParameters.MinimumHeight,
                $"Historical preset '{preset.Id}' declares an unsupported dimension.");
            Require(preset.Shape.Validate(preset.Height).Count == 0,
                $"Historical preset '{preset.Id}' has out-of-range Shape V2 values: " +
                string.Join("; ", preset.Shape.Validate(preset.Height)));

            var parameters = preset.ToParameters(HullParameters.Default);
            Require(parameters.Validate().Count == 0,
                $"Historical preset '{preset.Id}' produced invalid parameters: " +
                string.Join("; ", parameters.Validate()));
        }
    }

    /// <summary>The library is broad, grouped by nation, and covers small through large hulls.</summary>
    private static void VerifyBroadGroupedCatalog()
    {
        Require(HistoricalPresetCatalog.All.Count >= 30,
            $"The historical library holds only {HistoricalPresetCatalog.All.Count} entries; it is a demonstration stub.");

        var byNation = HistoricalPresetCatalog.All.GroupBy(preset => preset.Nation).ToArray();
        Require(byNation.Length == HistoricalNations.All.Count,
            $"The historical library groups into {byNation.Length} nations instead of {HistoricalNations.All.Count}.");
        foreach (var nation in HistoricalNations.All)
        {
            var entries = byNation.FirstOrDefault(group => group.Key == nation);
            Require(entries is not null && entries.Count() >= 3,
                $"The {nation.DisplayName} group has fewer than three entries.");
            Require(!string.IsNullOrWhiteSpace(nation.FlagGlyph) && !string.IsNullOrWhiteSpace(nation.AccentColor),
                $"The {nation.DisplayName} group has no compact flag presentation.");
        }

        Require(HistoricalNations.All.Select(nation => nation.AccentColor).Distinct().Count() == HistoricalNations.All.Count,
            "Two nations share a flag colour.");
        Require(HistoricalNations.All.Select(nation => nation.DisplayName).Distinct().Count() == HistoricalNations.All.Count,
            "Two nations share a display name.");

        // Size coverage: patrol craft through capital ships, with a middle band.
        Require(HistoricalPresetCatalog.All.Min(preset => preset.Length) <= 25 &&
                HistoricalPresetCatalog.All.Max(preset => preset.Length) >= 200,
            "The historical library does not span small craft through capital ships.");
        Require(HistoricalPresetCatalog.All.Count(preset => preset.Length is >= 60 and <= 160) >= 5,
            "The historical library has no useful medium-hull band.");

        // No animal name is reused for a historical hull, so nothing can silently stand in
        // for a real class under an animal label.
        var animalNames = HullShapePreset.All.Select(preset => preset.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in HistoricalPresetCatalog.All)
        {
            Require(!animalNames.Contains(preset.Name) && !animalNames.Contains(preset.Designation),
                $"Historical preset '{preset.Id}' reuses an animal preset name.");
        }

        // Omissions have concrete reasons: the roster records no completed French or Italian
        // battlecruiser, so those facets are absent rather than invented.
        Require(!HistoricalPresetCatalog.All.Any(preset =>
                preset.Nation == HistoricalNations.France && preset.Designation.Contains("Battlecruiser", StringComparison.OrdinalIgnoreCase)),
            "The library invented a French battlecruiser the roster records as not applicable.");
        Require(!HistoricalPresetCatalog.All.Any(preset =>
                preset.Nation == HistoricalNations.Italy && preset.Designation.Contains("Battlecruiser", StringComparison.OrdinalIgnoreCase)),
            "The library invented an Italian battlecruiser the roster records as not applicable.");
    }

    /// <summary>Nation is visual metadata: identical values generate identical hulls.</summary>
    private static void VerifyNationIsMetadataOnly(HullGenerator generator)
    {
        var template = HistoricalPresetCatalog.Find("us-fletcher-dd")
            ?? throw new InvalidOperationException("The Fletcher entry is missing from the historical library.");
        var twin = template with { Id = "test-twin", Nation = HistoricalNations.Italy };
        Require(template.Length == twin.Length && template.Shape == twin.Shape,
            "The nation-as-metadata twin changed a dimension or Shape V2 value.");

        var american = generator.Generate(template.ToParameters(HullParameters.Default));
        var italian = generator.Generate(twin.ToParameters(HullParameters.Default));

        Require(Cells(american.Blocks).SetEquals(Cells(italian.Blocks)),
            "Changing only the nation metadata changed the generated occupied cells.");
        Require(american.Blocks.Select(block => (block.Shape, block.Position, block.Rotation, block.Material)).ToHashSet()
                .SetEquals(italian.Blocks.Select(block => (block.Shape, block.Position, block.Rotation, block.Material))),
            "Changing only the nation metadata changed a placement, rotation or material.");
        Require(american.OccupiedLength == italian.OccupiedLength &&
                american.OccupiedWidth == italian.OccupiedWidth &&
                american.OccupiedHeight == italian.OccupiedHeight,
            "Changing only the nation metadata changed the hull bounds.");

        // Applying a preset leaves armor, construction and smoothing exactly where they were.
        var basis = HullParameters.Default with
        {
            Smoothing = SmoothingMethod.HorizontalSlopeFill,
            Beamify = false,
            HullArmor = ArmorLayout.Single(MaterialKind.Wood),
        };
        var applied = template.ToParameters(basis);
        Require(applied.Smoothing == basis.Smoothing && applied.Beamify == basis.Beamify &&
                applied.HullArmor.Equals(basis.HullArmor) && Equals(applied.DeckArmor, basis.DeckArmor) &&
                applied.Superstructure == basis.Superstructure,
            "Applying a historical preset rewrote armor, construction, smoothing or superstructure.");
    }

    /// <summary>Representative small/medium/large entries pass the ordinary pipeline.</summary>
    private static void VerifyRepresentativePipeline(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        var small = HistoricalPresetCatalog.All.OrderBy(preset => preset.Length).First();
        var large = HistoricalPresetCatalog.All.OrderByDescending(preset => preset.Length).First();
        var medium = HistoricalPresetCatalog.All.OrderBy(preset => Math.Abs(preset.Length - 120)).First();
        Require(small.Length < medium.Length && medium.Length < large.Length,
            "The representative small/medium/large selection is not ordered.");

        foreach (var preset in new[] { small, medium, large })
        {
            var parameters = preset.ToParameters(HullEditorSettings.Default);
            Require(parameters.Validate().Count == 0,
                $"Representative preset '{preset.Id}' failed parameter validation.");
            var hull = generator.Generate(parameters);
            Require(HullGeometryValidator.Validate(hull).Count == 0,
                $"Representative preset '{preset.Id}' failed geometry validation: " +
                string.Join("; ", HullGeometryValidator.Validate(hull)));
            Require(hull.OccupiedLength == preset.Length && hull.OccupiedWidth == preset.Width,
                $"Representative preset '{preset.Id}' did not fill its declared footprint.");

            VerifyExport(hull, catalog, preset.Id);
        }

        // The full profile proves every entry, not just the representative three.
        if (!full)
            return;

        foreach (var preset in HistoricalPresetCatalog.All)
        {
            var hull = generator.Generate(preset.ToParameters(HullParameters.Default));
            Require(HullGeometryValidator.Validate(hull).Count == 0,
                $"Historical preset '{preset.Id}' failed geometry validation: " +
                string.Join("; ", HullGeometryValidator.Validate(hull)));
        }
    }

    private static void VerifyExport(GeneratedHull hull, FtdBlockCatalog catalog, string id)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"HullForgeHIS01-{Guid.NewGuid():N}");
        try
        {
            var export = new BlueprintExporter().Export(hull, catalog, directory, $"HIS01 {id}");
            Require(File.Exists(export.FilePath), $"Historical preset '{id}' did not write a blueprint.");
            using var document = JsonDocument.Parse(File.ReadAllText(export.FilePath));
            var craft = document.RootElement.GetProperty("Blueprint");
            Require(craft.GetProperty("MinCords").GetString() == $"{hull.MinX},{hull.MinY},{hull.MinZ}" &&
                    craft.GetProperty("MaxCords").GetString() == $"{hull.MaxX},{hull.MaxY},{hull.MaxZ}",
                $"Historical preset '{id}' exported bounds that do not match the generated hull.");
            Require(export.OccupiedCellCount == Cells(hull.Blocks).Count,
                $"Historical preset '{id}' exported a different occupied cell count than it generated.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The catalog apply path: an entry applies through the normal editor commit path and every
    /// control stays editable. The historical library itself is no longer a UI surface.
    /// </summary>
    private static void VerifyEditorApplication()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                try
                {
                    RunEditorApplicationChecks(window);
                }
                finally
                {
                    // Leave the probe document clean so Close does not open an unsaved-changes
                    // dialog on a thread with no message pump.
                    window.EditorSessionForTests.New(
                        ShipDocument.CreateNew("HIS01 probe clean", HullEditorSettings.Default));
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45)))
            throw new InvalidOperationException("The historical editor-application probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The historical editor-application probe failed.", failure);
    }

    private static void RunEditorApplicationChecks(MainWindow window)
    {
        PumpDispatcher();
        window.MarkLoadedForTests();

        var editor = window.EditorSessionForTests;
        // A headless window has not run its Loaded pass, so the dimension rows have no text
        // yet. The committed document is the honest baseline for the unrelated settings.
        var before = editor.Document.Hull;

        var preset = HistoricalPresetCatalog.Find("us-fletcher-dd")
            ?? throw new InvalidOperationException("The Fletcher entry is missing from the historical catalog.");
        var revisionBefore = editor.Revision;
        window.SelectHistoricalPresetForTests(preset.Id);
        PumpDispatcher();

        // The selection went through the normal editor state path: one committed revision.
        Require(editor.Revision == revisionBefore + 1,
            $"Applying a historical preset committed {editor.Revision - revisionBefore} revisions instead of one.");
        var applied = window.ReadParametersForTests(out var message);
        Require(applied is not null, $"The editor could not read the applied preset: {message}");
        var appliedParameters = applied!;
        Require(editor.Document.Hull.Length == preset.Length &&
                editor.Document.Hull.Width == preset.Width &&
                editor.Document.Hull.Height == preset.Height,
            "Applying a historical preset did not commit its dimensions to the editor document " +
            $"(committed {editor.Document.Hull.Length}x{editor.Document.Hull.Width}x{editor.Document.Hull.Height}, " +
            $"expected {preset.Length}x{preset.Width}x{preset.Height}, controls " +
            $"{appliedParameters.Length}x{appliedParameters.Width}x{appliedParameters.Height}).");
        Require(editor.Document.Hull.EffectiveShape == preset.Shape,
            "Applying a historical preset did not commit its Shape V2 values to the editor document.");

        Require(appliedParameters.Length == preset.Length && appliedParameters.Width == preset.Width &&
                appliedParameters.Height == preset.Height &&
                appliedParameters.BowStyle == preset.BowStyle && appliedParameters.SternStyle == preset.SternStyle &&
                appliedParameters.EffectiveShape == preset.Shape,
            "The editor controls do not show the applied preset's dimensions and Shape V2 values.");
        Require(appliedParameters.HullArmor.Equals(before.HullArmor) &&
                Equals(appliedParameters.DeckArmor, before.DeckArmor) &&
                appliedParameters.Smoothing == before.Smoothing && appliedParameters.Beamify == before.Beamify,
            "Applying a historical preset rewrote the unrelated armor, smoothing or construction state " +
            $"(hull armor {appliedParameters.HullArmor} vs {before.HullArmor}, deck {appliedParameters.DeckArmor} vs " +
            $"{before.DeckArmor}, smoothing {appliedParameters.Smoothing} vs {before.Smoothing}, " +
            $"beamify {appliedParameters.Beamify} vs {before.Beamify}).");

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
            Require(control.IsEnabled, $"Applying a historical preset disabled {control.Name}.");

        // The whole normal Shape V2 set is freely editable immediately afterward.
        window.BowFullnessSliderForTests.Value = 0.42;
        window.BowFlareSliderForTests.Value = -0.33;
        window.SternFullnessSliderForTests.Value = -0.21;
        window.SternSideShapeSliderForTests.Value = 0.28;
        window.EntranceLengthInputForTests.SetDisplayedValue(37);
        window.RunLengthInputForTests.SetDisplayedValue(41);
        window.BowDeckRiseInputForTests.SetDisplayedValue(1);
        window.SternDeckRiseInputForTests.SetDisplayedValue(1);
        window.BowKeelRiseInputForTests.SetDisplayedValue(1);
        window.SternKeelRiseInputForTests.SetDisplayedValue(0);
        window.BowStyleBoxForTests.SelectedIndex = window.BowStyleBoxForTests.Items.Count - 1;
        window.SternStyleBoxForTests.SelectedIndex = window.SternStyleBoxForTests.Items.Count - 1;
        window.BulbCheckBoxForTests.IsChecked = true;
        PumpDispatcher();

        // A body slider edit takes the body form to Custom without any historical restriction.
        window.BodyFullnessSliderForTests.Value = 0.9;
        window.BodySideShapeSliderForTests.Value = 0.4;
        window.BodyChineSliderForTests.Value = 0.2;
        PumpDispatcher();
        Require(window.SelectedBodyFormForTests == BodyStyle.Custom,
            "Editing a body slider after a historical preset did not transition the body form to Custom.");
        Require(window.BulbCheckBoxForTests.IsChecked == true && window.BulbCheckBoxForTests.IsEnabled,
            "A historical preset left the bulb control restricted.");

        var edited = window.ReadParametersForTests(out var editedMessage);
        Require(edited is not null, $"The edited historical hull became unreadable: {editedMessage}");
        var editedParameters = edited!;
        Require(editedParameters.BowStyle == BowStyle.Clipper,
            "The bow-style edit after a historical preset did not stick.");
        Require(Math.Abs(editedParameters.EffectiveShape.Bow.Fullness - 0.42) < 1e-9 &&
                Math.Abs(editedParameters.EffectiveShape.Bow.Flare + 0.33) < 1e-9 &&
                editedParameters.EffectiveShape.Bow.EntranceLengthPercent == 37 &&
                editedParameters.EffectiveShape.Body.Style == BodyStyle.Custom &&
                Math.Abs(editedParameters.EffectiveShape.Body.Fullness - 0.9) < 1e-9,
            "The full Shape V2 slider set was not freely editable after a historical preset.");

        // A second entry still applies: nothing locked the editor to the first nation/class.
        var second = HistoricalPresetCatalog.Find("de-bismarck-bb")
            ?? throw new InvalidOperationException("The Bismarck entry is missing from the historical catalog.");
        var secondRevision = editor.Revision;
        window.SelectHistoricalPresetForTests(second.Id);
        PumpDispatcher();
        Require(editor.Revision == secondRevision + 1 && editor.Document.Hull.Length == second.Length &&
                editor.Document.Hull.EffectiveShape == second.Shape,
            "A second historical preset did not apply through the normal editor state path.");
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
