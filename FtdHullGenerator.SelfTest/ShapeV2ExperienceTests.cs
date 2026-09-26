using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.UI;
using static SlopeFillTestSupport;

/// <summary>
/// SHP01: the frozen 2.0 slider-first Shape V2 experience. Animal presets are absent
/// from the normal editor, the named body forms are initializers rather than modes,
/// every normal control stays free afterward, and the control envelope is materially
/// wider while the generator/validator still decides real validity.
/// </summary>
internal static class ShapeV2ExperienceTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog, bool full)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);

        VerifyAnimalPresetSurfaceIsGone();
        VerifyBodyFormPresets();
        VerifyBroadenedEnvelope();
        VerifyWidenedRangeChangesGeometry(generator);
        VerifyExtremeCombinations(generator, full);
        VerifyInvalidCombinationsAreDiagnosed(generator);
        VerifyEditorInteraction();

        Console.WriteLine(full
            ? "Shape V2 experience: no animal preset surface, 7 named body-form initializers + Custom, widened ±1.5 envelope, extreme combinations, diagnostics, and live editor independence passed."
            : "Shape V2 experience: no animal preset surface, 7 named body-form initializers + Custom, widened ±1.5 envelope, representative extremes, diagnostics, and live editor independence passed.");
    }

    /// <summary>
    /// The animal roster is internal compatibility data only. The normal editor must
    /// carry no preset grid, no hint text and no control bound to the roster.
    /// </summary>
    private static void VerifyAnimalPresetSurfaceIsGone()
    {
        Require(typeof(MainWindow).GetNestedType("ShapePresetChoice", BindingFlags.NonPublic) is null,
            "The animal preset-grid choice type is still compiled into the editor.");

        var path = Path.Combine(FindRepositoryRoot(), "FtdHullGenerator", "MainWindow.xaml");
        var xaml = XDocument.Load(path);
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        Require(!xaml.Descendants().Any(element => element.Name.LocalName == "ShapePresetGrid"),
            "The normal editor still contains the animal preset grid.");
        Require(!xaml.Descendants().Any(element => element.Name.LocalName == "ShapePresetHintText"),
            "The normal editor still contains the animal preset hint text.");
        Require(!xaml.Descendants().Any(element =>
                (string?)element.Attribute("SelectionChanged") == "ShapePresetChanged"),
            "The normal editor still wires an animal preset selection handler.");
        Require(xaml.Descendants().Any(element =>
                element.Name.LocalName == "ComboBox" &&
                (string?)element.Attribute(xamlNamespace + "Name") == "BodyStyleBox"),
            "The body-form preset selector is missing from the editor.");
    }

    /// <summary>Every named body form is a distinct, complete starting bundle.</summary>
    private static void VerifyBodyFormPresets()
    {
        var named = Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom).ToArray();
        Require(named.Length == 7, $"Shape V2 exposes {named.Length} named body forms instead of 7.");
        Require(Enum.GetValues<BodyStyle>().Contains(BodyStyle.Custom),
            "The body-form selector has no Custom state for a slider edit to land on.");

        foreach (var style in named)
        {
            var bundle = BodyShapeSettings.ForStyle(style);
            Require(bundle.Style == style, $"The {style} body form reports the wrong style.");
            Require(bundle.FlatBottom is >= HullShapeSettings.MinimumFlatBottom and <= HullShapeSettings.MaximumFlatBottom,
                $"The {style} body form leaves the flat-bottom control out of range.");
        }

        Require(BodyShapeSettings.ForStyle(BodyStyle.FlatWide).FlatBottom == 1,
            "The Flat / Wide body form no longer initializes the flat bottom.");
        foreach (var style in named.Where(style => style != BodyStyle.FlatWide))
            Require(BodyShapeSettings.ForStyle(style).FlatBottom == 0,
                $"{style} unexpectedly initializes the flat bottom; it must stay combinable with a tapered keel.");

        // Flat bottom is a body value, not a mode: the evaluator takes it as an
        // independent input alongside fullness, side shape and chine.
        var flat = RegionalSectionEvaluator.Evaluate(0.2, 0.35, 0.10, -0.65, flatBottom: 1);
        var tapered = RegionalSectionEvaluator.Evaluate(0.2, 0.35, 0.10, -0.65, flatBottom: 0);
        Require(flat > tapered, "The flat-bottom control no longer widens the lowest section rows.");
    }

    /// <summary>The control envelope is finite but materially wider than the legacy ±0.9.</summary>
    private static void VerifyBroadenedEnvelope()
    {
        Require(HullShapeSettings.MinimumControl == -1.5 && HullShapeSettings.MaximumControl == 1.5,
            $"The Shape V2 control envelope is {HullShapeSettings.MinimumControl}..{HullShapeSettings.MaximumControl} instead of the intended -1.5..1.5.");
        Require(HullShapeSettings.MaximumControl >= 1.5,
            "The Shape V2 control envelope did not widen materially beyond the legacy 0.9 travel.");

        var atMaximum = HullShapeSettings.Default with
        {
            Bow = new BowShapeSettings(1.5, 1.5, 80),
            Body = new BodyShapeSettings(BodyStyle.Custom, 1.5, 1.5, 1.5, FlatBottom: 1),
            Stern = new SternShapeSettings(1.5, 1.5, 80),
            Profile = new HullProfileSettings(2, 2, 2, 2),
        };
        Require(atMaximum.Validate(12).Count == 0,
            $"The widened envelope rejected its own maximum: {string.Join("; ", atMaximum.Validate(12))}");

        var atMinimum = HullShapeSettings.Default with
        {
            Bow = new BowShapeSettings(-1.5, -1.5, 5),
            Body = new BodyShapeSettings(BodyStyle.Custom, -1.5, -1.5, -1.5, FlatBottom: 0),
            Stern = new SternShapeSettings(-1.5, -1.5, 5),
            Profile = HullProfileSettings.Flat,
        };
        Require(atMinimum.Validate(12).Count == 0,
            $"The widened envelope rejected its own minimum: {string.Join("; ", atMinimum.Validate(12))}");

        foreach (var (label, shape) in new[]
                 {
                     ("above maximum", atMaximum with { Bow = atMaximum.Bow with { Fullness = 1.6 } }),
                     ("below minimum", atMinimum with { Body = atMinimum.Body with { Chine = -1.6 } }),
                 })
        {
            var errors = shape.Validate(12);
            Require(errors.Count > 0 && errors.Any(error =>
                    error.Contains("finite number between", StringComparison.OrdinalIgnoreCase)),
                $"A control {label} of the envelope was accepted instead of receiving a clear diagnostic.");
        }

        // The former ±0.9 range must evaluate exactly as it always did: the widening
        // only extends the travel, it does not rescale the analytic curves.
        var legacySoft = Math.Pow(Math.Sin(Math.PI * 0.5 / 2), Math.Pow(2.4, -0.9));
        Require(Math.Abs(RegionalSectionEvaluator.Evaluate(0.5, 0.9, 0, -0.9) - legacySoft) < 1e-12,
            "The former 0.9 evaluation changed when the control envelope was widened.");
        Require(RegionalSectionEvaluator.Evaluate(0.3, 1.5, 0, -0.9) >
                RegionalSectionEvaluator.Evaluate(0.3, 0.9, 0, -0.9) + 1e-9,
            "The widened fullness travel is still being clamped at the old 0.9 bound.");
        Require(RegionalSectionEvaluator.Evaluate(0.3, -1.5, 0, -0.9) <
                RegionalSectionEvaluator.Evaluate(0.3, -0.9, 0, -0.9) - 1e-9,
            "The widened negative fullness travel is still being clamped at the old 0.9 bound.");
    }

    /// <summary>Extreme valid settings produce visibly more dramatic, still-valid hulls.</summary>
    private static void VerifyWidenedRangeChangesGeometry(HullGenerator generator)
    {
        var basis = HullParameters.Default with
        {
            Length = 80,
            Width = 21,
            Height = 12,
            Beamify = false,
            Smoothing = SmoothingMethod.None,
            Shape = HullShapeSettings.Default with
            {
                Bow = new BowShapeSettings(0, 0, 20),
                Stern = new SternShapeSettings(0, 0, 20),
                Profile = HullProfileSettings.Flat,
            },
        };
        // A soft-chine section without the flat-bottom saturation makes the extra
        // fullness travel directly visible: the same hull gets a wider lower body.
        var oldEnvelope = basis with
        {
            Shape = basis.EffectiveShape with
            {
                Body = new BodyShapeSettings(BodyStyle.Custom, 0.9, 0, -0.9),
            },
        };
        var newEnvelope = basis with
        {
            Shape = basis.EffectiveShape with
            {
                Body = new BodyShapeSettings(BodyStyle.Custom, 1.5, 0, -0.9),
            },
        };

        var oldHull = generator.Generate(oldEnvelope);
        var newHull = generator.Generate(newEnvelope);
        Require(HullGeometryValidator.Validate(oldHull).Count == 0 &&
                HullGeometryValidator.Validate(newHull).Count == 0,
            "A widened-envelope comparison hull failed geometry validation.");

        var oldCells = Cells(oldHull.Blocks);
        var newCells = Cells(newHull.Blocks);
        Require(!oldCells.SetEquals(newCells),
            "The widened fullness travel produced identical geometry to the old 0.9 envelope.");
        Require(newCells.Count > oldCells.Count,
            "The widened fullness travel did not produce a fuller, more dramatic lower body.");

        static int StationBreadthSum(GeneratedHull hull)
        {
            var station = Cells(hull.Blocks).Where(cell => cell.Z == 0).ToArray();
            return station.GroupBy(cell => cell.Y)
                .Sum(row => row.Max(cell => cell.X) - row.Min(cell => cell.X) + 1);
        }

        Require(StationBreadthSum(newHull) > StationBreadthSum(oldHull),
            "The widened envelope unexpectedly produced a narrower midship section.");

        // The formerly coupled controls stay independent: a flat-bottom hull with an
        // extreme bow, stern, profile and bulb is one ordinary generation.
        var coupled = newEnvelope with
        {
            BowStyle = BowStyle.Raked,
            SternStyle = SternStyle.Canoe,
            HasBulb = true,
            Bulb = new BulbSettings(10, 40, 0, 0),
            Shape = newEnvelope.EffectiveShape with
            {
                Bow = new BowShapeSettings(1.5, 1.5, 70),
                Body = newEnvelope.EffectiveShape.Body with { SideShape = 1.5, Chine = 1.5, FlatBottom = 1 },
                Stern = new SternShapeSettings(-1.5, -1.5, 45),
                Profile = new HullProfileSettings(3, 2, 2, 1),
            },
        };
        Require(coupled.Validate().Count == 0, "An extreme but valid combined hull failed parameter validation.");
        var coupledHull = generator.Generate(coupled);
        Require(HullGeometryValidator.Validate(coupledHull).Count == 0,
            "An extreme flat-bottom hull combined with bow, stern, profile and bulb controls failed geometry validation.");
    }

    /// <summary>
    /// Representative extreme valid combinations stay deterministic, symmetric and
    /// connected, and the validator still rejects genuinely impossible parameters.
    /// </summary>
    private static void VerifyExtremeCombinations(HullGenerator generator, bool full)
    {
        var values = new[] { -1.5, -0.9, 0.0, 0.9, 1.5 };
        var combos = new List<HullShapeSettings>();
        foreach (var fullness in values)
        foreach (var side in values)
        {
            combos.Add(HullShapeSettings.Default with
            {
                Bow = new BowShapeSettings(fullness, side, fullness < 0 ? 70 : 20),
                Body = new BodyShapeSettings(BodyStyle.Custom, fullness, side, side, FlatBottom: fullness > 0 ? 1 : 0),
                Stern = new SternShapeSettings(side, fullness, side < 0 ? 70 : 20),
                Profile = HullProfileSettings.Flat,
            });
        }

        if (!full)
            combos = combos.Where((_, index) => index % 4 == 0).ToList();

        foreach (var shape in combos)
        {
            var parameters = HullParameters.Default with
            {
                Length = 64,
                Width = 19,
                Height = 12,
                Beamify = true,
                Shape = shape,
            };
            Require(parameters.Validate().Count == 0,
                $"An extreme combination failed parameter validation: {string.Join("; ", parameters.Validate())}");

            var first = generator.Generate(parameters);
            var second = generator.Generate(parameters);
            Require(HullGeometryValidator.Validate(first).Count == 0,
                $"An extreme combination failed geometry validation: {string.Join("; ", HullGeometryValidator.Validate(first))}");
            Require(Cells(first.Blocks).SetEquals(Cells(second.Blocks)),
                "An extreme combination generated different occupied cells on a second run.");
            Require(first.OccupiedLength == parameters.Length && first.OccupiedWidth == parameters.Width &&
                    first.OccupiedHeight == parameters.OverallHeight,
                "An extreme combination did not fill its declared extents.");
        }
    }

    /// <summary>Genuinely impossible geometry still fails with a truthful diagnostic.</summary>
    private static void VerifyInvalidCombinationsAreDiagnosed(HullGenerator generator)
    {
        var outOfRange = HullParameters.Default with
        {
            Shape = HullShapeSettings.Default with
            {
                Body = HullShapeSettings.Default.Body with { Fullness = 1.6 },
            },
        };
        Require(outOfRange.Validate().Any(error => error.Contains("Body fullness", StringComparison.OrdinalIgnoreCase)),
            "A control beyond the finite envelope did not receive a clear validation diagnostic.");

        var impossible = HullParameters.Default with
        {
            Length = 40,
            Width = 13,
            Height = 8,
            HullArmor = new ArmorLayout(Enumerable.Repeat(MaterialKind.Metal, 30)),
        };
        try
        {
            generator.Generate(impossible);
            throw new InvalidOperationException("An armor stack that consumes the whole cavity was accepted.");
        }
        catch (HullGenerationException exception)
        {
            Require(exception.Errors.Count > 0,
                "An impossible hull failed without a truthful blocking diagnostic.");
        }
    }

    /// <summary>
    /// The live editor: a body form initializes only its body values, every unrelated
    /// control stays free, and a slider edit becomes Custom without rewriting anything.
    /// </summary>
    private static void VerifyEditorInteraction()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                try
                {
                    RunEditorInteractionChecks(window);
                }
                finally
                {
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
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("The live Shape V2 editor probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The live Shape V2 editor probe failed.", failure);
    }

    private static void RunEditorInteractionChecks(MainWindow window)
    {
        PumpDispatcher();

        var expectedLabels = new[]
        {
            "Rounded", "V-Hull", "Deep V-Hull", "U-Hull", "Flat / Wide", "Hard Chine", "Tumblehome", "Custom",
        };
        var labels = window.BodyFormBoxForTests.Items.Cast<object>().Select(item => item.ToString()!).ToArray();
        Require(labels.SequenceEqual(expectedLabels),
            $"The body-form selector labels are {string.Join(", ", labels)} instead of the frozen names.");

        // No control anywhere in the editor surfaces an internal animal name.
        var animalNames = HullShapePreset.All.Select(preset => preset.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var itemsControl in Descendants(window).OfType<ItemsControl>())
        {
            foreach (var item in itemsControl.Items)
            {
                var text = item?.ToString();
                Require(text is null || !animalNames.Contains(text),
                    $"An animal preset name '{text}' is still visible in the normal editor.");
            }
        }

        // Every normal Shape V2 control is open and broadened.
        foreach (var slider in new[]
                 {
                     window.BowFullnessSliderForTests, window.BowFlareSliderForTests,
                     window.BodyFullnessSliderForTests, window.BodySideShapeSliderForTests,
                     window.BodyChineSliderForTests,
                     window.SternFullnessSliderForTests, window.SternSideShapeSliderForTests,
                 })
        {
            Require(slider.Minimum == HullShapeSettings.MinimumControl &&
                    slider.Maximum == HullShapeSettings.MaximumControl,
                $"{slider.Label} still travels {slider.Minimum}..{slider.Maximum} instead of the widened envelope.");
        }

        // Give every unrelated control a distinctive value before the presets run.
        window.BowStyleBoxForTests.SelectedIndex = window.BowStyleBoxForTests.Items.Count - 1;
        window.SternStyleBoxForTests.SelectedIndex = window.SternStyleBoxForTests.Items.Count - 1;
        window.BowFullnessSliderForTests.Value = 0.42;
        window.BowFlareSliderForTests.Value = -0.33;
        window.SternFullnessSliderForTests.Value = -0.21;
        window.SternSideShapeSliderForTests.Value = 0.28;
        window.EntranceLengthInputForTests.SetDisplayedValue(37);
        window.RunLengthInputForTests.SetDisplayedValue(41);
        window.BowDeckRiseInputForTests.SetDisplayedValue(2);
        window.SternDeckRiseInputForTests.SetDisplayedValue(1);
        window.BowKeelRiseInputForTests.SetDisplayedValue(1);
        window.SternKeelRiseInputForTests.SetDisplayedValue(0);
        window.BulbCheckBoxForTests.IsChecked = true;
        PumpDispatcher();

        var bowStyle = window.BowStyleBoxForTests.SelectedItem?.ToString();
        var sternStyle = window.SternStyleBoxForTests.SelectedItem?.ToString();
        var bowFullness = window.BowFullnessSliderForTests.Value;
        var bowFlare = window.BowFlareSliderForTests.Value;
        var sternFullness = window.SternFullnessSliderForTests.Value;
        var sternSide = window.SternSideShapeSliderForTests.Value;
        Require(window.EntranceLengthInputForTests.TryGetValue(out var entrance), "The entrance control lost its value.");
        Require(window.RunLengthInputForTests.TryGetValue(out var run), "The run control lost its value.");
        Require(window.BowDeckRiseInputForTests.TryGetValue(out var bowDeckRise), "The bow deck rise control lost its value.");
        Require(window.SternDeckRiseInputForTests.TryGetValue(out var sternDeckRise), "The stern deck rise control lost its value.");

        void RequireUnrelatedUnchanged(string context)
        {
            Require(window.BowStyleBoxForTests.SelectedItem?.ToString() == bowStyle &&
                    window.SternStyleBoxForTests.SelectedItem?.ToString() == sternStyle,
                $"{context} changed the bow or stern style.");
            Require(window.BowFullnessSliderForTests.Value == bowFullness &&
                    window.BowFlareSliderForTests.Value == bowFlare &&
                    window.SternFullnessSliderForTests.Value == sternFullness &&
                    window.SternSideShapeSliderForTests.Value == sternSide,
                $"{context} rewrote an unrelated bow or stern slider.");
            Require(window.EntranceLengthInputForTests.TryGetValue(out var entranceNow) && entranceNow == entrance &&
                    window.RunLengthInputForTests.TryGetValue(out var runNow) && runNow == run &&
                    window.BowDeckRiseInputForTests.TryGetValue(out var bowRiseNow) && bowRiseNow == bowDeckRise &&
                    window.SternDeckRiseInputForTests.TryGetValue(out var sternRiseNow) && sternRiseNow == sternDeckRise,
                $"{context} rewrote an unrelated entrance, run or profile control.");
            Require(window.BulbCheckBoxForTests.IsChecked == true, $"{context} switched the bulb off.");
            foreach (var control in new FrameworkElement[]
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
                     })
            {
                Require(control.IsEnabled, $"{context} disabled {control.Name}.");
            }
        }

        foreach (var style in Enum.GetValues<BodyStyle>().Where(style => style != BodyStyle.Custom))
        {
            window.SelectBodyFormForTests(style);
            PumpDispatcher();

            var bundle = BodyShapeSettings.ForStyle(style);
            Require(window.SelectedBodyFormForTests == style,
                $"Selecting the {style} body form did not select it.");
            Require(window.BodyFullnessSliderForTests.Value == bundle.Fullness &&
                    window.BodySideShapeSliderForTests.Value == bundle.SideShape &&
                    window.BodyChineSliderForTests.Value == bundle.Chine &&
                    window.BodyFlatBottomSliderForTests.Value == bundle.FlatBottom,
                $"The {style} body form did not initialize its own body values.");
            RequireUnrelatedUnchanged($"Selecting the {style} body form");
        }

        // A body slider edit becomes Custom and leaves the other body values alone.
        window.SelectBodyFormForTests(BodyStyle.FlatWide);
        PumpDispatcher();
        var flatSide = window.BodySideShapeSliderForTests.Value;
        var flatChine = window.BodyChineSliderForTests.Value;
        window.BodyFullnessSliderForTests.Value = HullShapeSettings.MaximumControl;
        PumpDispatcher();
        Require(window.SelectedBodyFormForTests == BodyStyle.Custom,
            "Editing a body slider did not transition the body form to Custom.");
        Require(window.BodySideShapeSliderForTests.Value == flatSide &&
                window.BodyChineSliderForTests.Value == flatChine &&
                window.BodyFlatBottomSliderForTests.Value == 1,
            "A body slider edit rewrote the other body values.");
        RequireUnrelatedUnchanged("A body slider edit");

        // An unrelated control edit must not disturb the selected body form.
        window.SelectBodyFormForTests(BodyStyle.U);
        PumpDispatcher();
        window.BowFullnessSliderForTests.Value = HullShapeSettings.MinimumControl;
        PumpDispatcher();
        Require(window.SelectedBodyFormForTests == BodyStyle.U,
            "Editing an unrelated bow slider cleared the selected body form.");
        Require(window.BodyFullnessSliderForTests.Value == BodyShapeSettings.ForStyle(BodyStyle.U).Fullness,
            "Editing an unrelated bow slider rewrote the body values.");

        // The Flat / Wide form is a starting point, not a mode: after it is applied the
        // user can still edit every control and combine it with an unrelated change.
        window.SelectBodyFormForTests(BodyStyle.FlatWide);
        PumpDispatcher();
        window.SternSideShapeSliderForTests.Value = HullShapeSettings.MaximumControl;
        window.BowFlareSliderForTests.Value = HullShapeSettings.MinimumControl;
        PumpDispatcher();
        Require(window.SelectedBodyFormForTests == BodyStyle.FlatWide &&
                window.BodyFlatBottomSliderForTests.Value == 1,
            "Editing unrelated controls after Flat / Wide changed the body form or dropped the flat bottom.");
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }
}
