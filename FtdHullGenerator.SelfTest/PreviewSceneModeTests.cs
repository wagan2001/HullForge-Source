using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI;
using FtdHullGenerator.UI.Scenes;

/// <summary>V02's deterministic Ocean/Dock, inspection fallback and local-preference regressions.</summary>
internal static class PreviewSceneModeTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);
        VerifyPreferenceStore();
        VerifyFreeboardPure(generator);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifySceneModes(generator, catalog);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;

        Console.WriteLine("Preview scene modes: deterministic Ocean/Dock, low-detail, preferences, inspection fallback, hit-through and physical/export identity passed.");
    }

    private static void VerifyPreferenceStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HullForge-V02-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "scene.json");
        try
        {
            var store = new PreviewScenePreferenceStore(path);
            Require(store.LoadOrDefault() == PreviewSceneSettings.Grid && !File.Exists(path),
                "Loading a missing scene preference wrote machine-local state.");

            var expected = new PreviewSceneSettings
            {
                Kind = PreviewSceneKind.Dock,
                Waterline = 4.75,
                ShowGridOverlay = true,
                LowDetail = true,
                ReducedMotion = true,
                Deterministic = true,
                SceneSeed = 77,
                SceneTimeSeconds = 12.5,
            };
            store.Save(expected);
            Require(store.LoadOrDefault() == expected,
                "A valid machine-local scene preference did not round-trip.");

            File.WriteAllText(path, "{ not valid json");
            Require(store.LoadOrDefault() == PreviewSceneSettings.Grid,
                "A corrupt scene preference did not fail closed to Grid.");
            File.WriteAllText(path, "{\"Kind\":999}");
            Require(store.LoadOrDefault() == PreviewSceneSettings.Grid,
                "An unknown persisted scene kind did not fail closed to Grid.");

            // A legacy raw-waterline preference keeps its exact persisted world-Y value; the
            // freeboard view is derived and never rewrites the stored datum.
            File.WriteAllText(path, "{\"Kind\":1,\"Waterline\":3.25}");
            var legacy = store.LoadOrDefault();
            Require(legacy.Kind == PreviewSceneKind.Ocean && legacy.Waterline == 3.25,
                "A legacy raw-waterline preference was reinterpreted or lost instead of loading unchanged.");

            var ocean = new PreviewSceneSettings
            {
                Kind = PreviewSceneKind.Ocean,
                Waterline = PreviewSceneFreeboard.ToWaterline(11, 4.5),
                SceneSeed = 41,
            };
            store.Save(ocean);
            Require(store.LoadOrDefault() == ocean,
                "An Ocean scene preference did not round-trip its persisted waterline.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyFreeboardPure(HullGenerator generator)
    {
        // Conversion convention: freeboard = referenceDeckElevation - waterline.
        Require(PreviewSceneFreeboard.ToWaterline(11, 3) == 8,
            "Freeboard-to-waterline conversion did not use the reference deck datum.");
        Require(PreviewSceneFreeboard.FromWaterline(11, 8) == 3,
            "Waterline-to-freeboard conversion did not use the reference deck datum.");
        foreach (var (deck, freeboard) in new[] { (11d, 0d), (11d, 3d), (9d, 4.5d), (13d, -2d), (7.5d, 12.25d) })
        {
            Require(PreviewSceneFreeboard.FromWaterline(
                        deck, PreviewSceneFreeboard.ToWaterline(deck, freeboard)) == freeboard,
                "Freeboard did not round-trip through the visual waterline.");
        }

        Require(PreviewSceneFreeboard.IsAvailable(PreviewSceneKind.Ocean) &&
                !PreviewSceneFreeboard.IsAvailable(PreviewSceneKind.Grid) &&
                !PreviewSceneFreeboard.IsAvailable(PreviewSceneKind.Dock),
            "Freeboard availability is not Ocean-only.");

        var datum = new PreviewSceneDeckDatum(11, 0);
        var (min, max) = PreviewSceneFreeboard.Range(datum, 4);
        Require(min == 0 && max == 16 && max >= min + PreviewSceneFreeboard.StepMetres,
            "The base freeboard range for datum (11, 0) is not the expected 0..16 m.");
        foreach (var legacy in new[] { -7.25, 25.5 })
        {
            var (legacyMin, legacyMax) = PreviewSceneFreeboard.Range(datum, legacy);
            Require(legacy >= legacyMin && legacy <= legacyMax,
                "An out-of-range persisted waterline was not representable on the freeboard slider.");
            Require(IsWholeStep(legacyMin) && IsWholeStep(legacyMax),
                "An expanded freeboard range was not a whole step multiple.");
        }
        var (_, narrowMax) = PreviewSceneFreeboard.Range(new PreviewSceneDeckDatum(0, 0), 0);
        Require(narrowMax >= PreviewSceneFreeboard.StepMetres,
            "A degenerate freeboard range was narrower than one step.");
        Require(PreviewSceneFreeboard.Snap(3.24) == 3.0 && PreviewSceneFreeboard.Snap(3.26) == 3.5,
            "Freeboard snap did not round to the half-metre step.");

        // The authoritative datum mirrors HullBuildContext.ReferenceDeckY, never MaxY/OverallHeight.
        var datumCases = new[]
        {
            HullParameters.Default with { Length = 16, Width = 7, Height = 7 },
            HullParameters.Default with { Length = 20, Width = 9, Height = 10 },
            HullParameters.Default with { Length = 24, Width = 11, Height = 13 },
        };
        GeneratedHull? sameHeightFlat = null;
        foreach (var parameters in datumCases)
        {
            var contextDatum = HullGenerator.CreateContext(parameters).ReferenceDeckY;
            Require(contextDatum == parameters.Height - 1,
                "HullBuildContext.ReferenceDeckY no longer mirrors HullParameters.Height - 1.");
            var generated = generator.Generate(parameters);
            Require(generated.Parameters.Height - 1 == contextDatum,
                "A generated hull no longer carries the context reference deck datum.");
            if (parameters.Height == 10)
                sameHeightFlat = generated;
        }

        // Deck rise changes GeneratedHull.MaxY without moving the ship-wide reference deck.
        Require(sameHeightFlat is not null, "The datum-stability case did not generate its flat hull.");
        var raised = generator.Generate(datumCases[1] with
        {
            Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(3, 3, 0, 0) },
        });
        Require(sameHeightFlat!.MaxY != raised.MaxY,
            "Deck rise did not change GeneratedHull.MaxY; the datum-stability case is not exercising a bound change.");
        Require(sameHeightFlat.Parameters.Height - 1 == raised.Parameters.Height - 1,
            "Two hulls with the same Height produced different reference deck datums.");

        static bool IsWholeStep(double value) =>
            Math.Abs(value / PreviewSceneFreeboard.StepMetres -
                     Math.Round(value / PreviewSceneFreeboard.StepMetres)) < 1e-9;
    }

    private static void VerifySceneModes(HullGenerator generator, FtdBlockCatalog catalog)
    {
        var hull = generator.Generate(HullParameters.Default with
        {
            Length = 72,
            Width = 35,
            Height = 12,
        });
        var preferencePath = Path.Combine(Path.GetTempPath(), $"HullForge-V02-pref-{Guid.NewGuid():N}.json");
        var exportDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-V02-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDirectory);
        try
        {
            var control = new HullPreviewControl(new PreviewScenePreferenceStore(preferencePath))
            {
                Width = 960,
                Height = 640,
            };
            Require(control.SceneDeckDatum is null,
                "A scene deck datum existed before any hull was set.");
            control.SetHull(hull, highlightEdits: false);
            Require(control.SceneDeckDatum == new PreviewSceneDeckDatum(hull.Parameters.Height - 1, hull.MinY),
                "The scene deck datum did not match the authoritative HullParameters.Height - 1 reference deck.");
            control.ShowView(PreviewView.Isometric);
            ArrangeAndRender(control, 960, 640);

            var shipModel = control.ShipModel;
            var physicalIdentity = PhysicalIdentity(hull);
            var camera = (PerspectiveCamera)control.Camera;
            var cameraState = (camera.Position, camera.LookDirection, control.ViewYaw, control.ViewPitch);
            var before = new BlueprintExporter().Export(hull, catalog, exportDirectory, "V02 identity");
            var blueprintIdentity = BlueprintIdentity(before.FilePath);

            var ocean = new PreviewSceneSettings
            {
                Kind = PreviewSceneKind.Ocean,
                Waterline = 3.25,
                SceneSeed = 77,
                SceneTimeSeconds = 12.5,
                Deterministic = true,
            };
            control.SceneSettings = ocean;
            ArrangeAndRender(control, 960, 640);
            var oceanModel = control.EnvironmentModel;
            Require(oceanModel is not null && oceanModel.IsFrozen,
                "Ocean did not create one frozen presentation-only environment.");
            var oceanIdentity = ModelIdentity(oceanModel);
            control.SceneSettings = ocean;
            Require(ModelIdentity(control.EnvironmentModel) == oceanIdentity,
                "Identical deterministic scene settings changed Ocean geometry.");
            control.SceneSettings = ocean with { SceneSeed = 78 };
            Require(ModelIdentity(control.EnvironmentModel) != oceanIdentity,
                "The explicit Ocean seed did not affect detailed procedural geometry.");

            Require(ReferenceEquals(control.CurrentHull, hull) && ReferenceEquals(control.ShipModel, shipModel) &&
                    PhysicalIdentity(hull) == physicalIdentity &&
                    (camera.Position, camera.LookDirection, control.ViewYaw, control.ViewPitch) == cameraState,
                "Changing to Ocean replaced, regenerated, reframed or mutated the physical ship.");
            var afterOcean = new BlueprintExporter().Export(hull, catalog, exportDirectory, "V02 identity");
            Require(BlueprintIdentity(afterOcean.FilePath) == blueprintIdentity,
                "Ocean presentation state changed native blueprint output.");

            var environmentGeometry = FirstGeometry(control.EnvironmentModel);
            Require(environmentGeometry is not null && !control.IsShipSelectableModel(environmentGeometry),
                "Ocean furniture acquired ship-selection identity.");
            Require(control.HitTestShip(new Point(480, 320)) is { } selected &&
                    control.IsShipSelectableModel(selected),
                "Ship hit testing did not continue through the actual Ocean environment.");

            // Freeboard is a derived view over the persisted waterline: changing it moves the
            // presentation water while the physical ship, camera, cutaway and export stay fixed.
            var deckDatum = control.SceneDeckDatum ??
                throw new InvalidOperationException("No scene deck datum after SetHull.");
            var freeboardLow = ocean with
            {
                Waterline = PreviewSceneFreeboard.ToWaterline(deckDatum.ReferenceDeckElevationMetres, 3),
            };
            control.SceneSettings = freeboardLow;
            var lowFreeboardIdentity = ModelIdentity(control.EnvironmentModel);
            control.SceneSettings = freeboardLow with
            {
                Waterline = PreviewSceneFreeboard.ToWaterline(deckDatum.ReferenceDeckElevationMetres, 6),
            };
            Require(ModelIdentity(control.EnvironmentModel) != lowFreeboardIdentity,
                "Changing freeboard did not move the Ocean presentation water.");
            Require(ReferenceEquals(control.CurrentHull, hull) && ReferenceEquals(control.ShipModel, shipModel) &&
                    PhysicalIdentity(hull) == physicalIdentity &&
                    (camera.Position, camera.LookDirection, control.ViewYaw, control.ViewPitch) == cameraState,
                "Changing freeboard replaced, regenerated, reframed or mutated the physical ship.");
            var afterFreeboard = new BlueprintExporter().Export(hull, catalog, exportDirectory, "V02 identity");
            Require(BlueprintIdentity(afterFreeboard.FilePath) == blueprintIdentity,
                "Changing freeboard changed native blueprint output.");
            control.SceneSettings = PreviewSceneSettings.Grid;
            Require(control.EnvironmentModel is null,
                "Grid stopped being the empty environment while exercising freeboard.");

            control.SceneSettings = ocean with { Kind = PreviewSceneKind.Dock, ShowGridOverlay = true };
            var standardPositions = PositionCount(control.EnvironmentModel);
            Require(GeometryCount(control.EnvironmentModel) > 6,
                "Dock did not include water, quay and independent grid furniture.");
            control.SceneSettings = control.SceneSettings with { ReducedMotion = true };
            Require(PositionCount(control.EnvironmentModel) < standardPositions,
                "Reduced motion did not flatten detailed water geometry.");
            control.SceneSettings = control.SceneSettings with { ReducedMotion = false, LowDetail = true };
            Require(PositionCount(control.EnvironmentModel) < standardPositions,
                "Low detail did not reduce Dock environment geometry.");

            control.Cutaway = CutawayPlane.Centreline;
            Require(control.ScenePresentationState.Occlusion == PreviewSceneOcclusion.Hidden &&
                    control.EnvironmentModel is not null &&
                    control.ScenePresentationState.StatusText.Contains("hidden", StringComparison.OrdinalIgnoreCase),
                "Cutaway did not hide occluding water/dock furniture while retaining the grid and visible state.");
            control.Cutaway = CutawayPlane.None;
            control.ShowView(PreviewView.Underside);
            Require(control.ScenePresentationState.Occlusion == PreviewSceneOcclusion.Faded &&
                    control.ScenePresentationState.StatusText.Contains("faded", StringComparison.OrdinalIgnoreCase),
                "Underside inspection did not fade environment furniture with visible status.");

            var shipBeforeGrid = control.ShipModel;
            control.SceneSettings = PreviewSceneSettings.Grid;
            Require(control.EnvironmentModel is null && ReferenceEquals(control.ShipModel, shipBeforeGrid),
                "Grid was not one scene change away or rebuilt the ship renderer.");

            var large = generator.Generate(HullParameters.Default with
            {
                Length = 200,
                Width = 60,
                Height = 30,
            });
            var largeIdentity = PhysicalIdentity(large);
            control.SetHull(large, highlightEdits: false);
            control.ShowView(PreviewView.Isometric);
            control.SceneSettings = ocean with { Kind = PreviewSceneKind.Dock, Waterline = large.MinY - 25 };
            Require(PhysicalIdentity(large) == largeIdentity && control.EnvironmentModel is not null &&
                    control.EnvironmentModel.Bounds.SizeX > large.OccupiedWidth &&
                    control.EnvironmentModel.Bounds.SizeZ > large.OccupiedLength,
                "Large-hull presentation bounds changed native bounds or failed to frame the environment.");
            control.SceneSettings = control.SceneSettings with { Waterline = large.MaxY + 25 };
            Require(PhysicalIdentity(large) == largeIdentity &&
                    control.ScenePresentationState.StatusText.Contains("presentation only", StringComparison.OrdinalIgnoreCase),
                "An extreme manual waterline changed the ship or implied a physical calculation.");

            // The control bridge must follow the nominal ship-wide reference deck even when
            // deck rise makes GeneratedHull.MaxY diverge from HullParameters.Height - 1, so a
            // MaxY substitution cannot pass unnoticed.
            var raisedHull = generator.Generate(hull.Parameters with
            {
                Shape = HullShapeSettings.Default with { Profile = new HullProfileSettings(3, 3, 0, 0) },
            });
            Require(raisedHull.MaxY > raisedHull.Parameters.Height - 1,
                "The raised-deck datum case did not move MaxY above the nominal reference deck.");
            var raisedControl = new HullPreviewControl { Width = 320, Height = 240 };
            raisedControl.SetHull(raisedHull, highlightEdits: false);
            Require(raisedControl.SceneDeckDatum ==
                    new PreviewSceneDeckDatum(raisedHull.Parameters.Height - 1, raisedHull.MinY),
                "The scene deck datum followed GeneratedHull.MaxY instead of the nominal reference deck.");
            Require(!File.Exists(preferencePath),
                "An unhosted preview wrote a default or programmatic scene preference.");
        }
        finally
        {
            if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, recursive: true);
            if (File.Exists(preferencePath)) File.Delete(preferencePath);
        }
    }

    private static void ArrangeAndRender(HullPreviewControl control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32).Render(control);
    }

    private static int GeometryCount(Model3D? model) => model switch
    {
        null => 0,
        GeometryModel3D => 1,
        Model3DGroup group => group.Children.Sum(GeometryCount),
        _ => 0,
    };

    private static int PositionCount(Model3D? model) => model switch
    {
        null => 0,
        GeometryModel3D { Geometry: MeshGeometry3D mesh } => mesh.Positions.Count,
        Model3DGroup group => group.Children.Sum(PositionCount),
        _ => 0,
    };

    private static Model3D? FirstGeometry(Model3D? model)
    {
        if (model is GeometryModel3D) return model;
        if (model is Model3DGroup group)
            foreach (var child in group.Children)
                if (FirstGeometry(child) is { } geometry)
                    return geometry;
        return null;
    }

    private static string ModelIdentity(Model3D? model)
    {
        var lines = new List<string>();
        Visit(model);
        return Hash(string.Join("\n", lines));

        void Visit(Model3D? current)
        {
            switch (current)
            {
                case GeometryModel3D { Geometry: MeshGeometry3D mesh }:
                    lines.Add(string.Join(";", mesh.Positions.Select(point =>
                        $"{point.X:R},{point.Y:R},{point.Z:R}")));
                    lines.Add(string.Join(",", mesh.TriangleIndices));
                    break;
                case Model3DGroup group:
                    foreach (var child in group.Children) Visit(child);
                    break;
            }
        }
    }

    private static string PhysicalIdentity(GeneratedHull hull) => Hash(string.Join("\n",
        $"{hull.MinX},{hull.MaxX},{hull.MinY},{hull.MaxY},{hull.MinZ},{hull.MaxZ}",
        string.Join("\n", hull.Blocks.Select(block =>
            $"{block.X},{block.Y},{block.Z}|{(int)block.Shape}|{block.Rotation}|{(int)block.Material}|{block.ArmorDepth}|{(int)block.Origin}"))));

    private static string BlueprintIdentity(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ??
                   throw new InvalidOperationException("The V02 export was not a JSON object.");
        root["Blueprint"]!.AsObject()["ForceId"] = 0;
        return Hash(root.ToJsonString());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
