using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Serialization;
using FtdHullGenerator.UI;
using FtdHullGenerator.UI.Scenes;

/// <summary>
/// Pins the frozen 2.0 product surface: Internal Structures is the only experimental
/// feature, the deferred project/superstructure/manual-deco/Dock workflows are absent from
/// the normal interface while their implementation survives, Deco V/H and barbettes are
/// normal, and Ocean presentation cannot change generated data or export output.
/// </summary>
internal static class FrozenProductSurfaceTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);
        VerifyExposureMatrix();
        VerifyEditorXamlSurface();
        VerifySceneSelectorSurface();

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyLiveEditorSurface();
                VerifyOceanPresentationAndInvariance(generator, catalog);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException("The frozen product-surface WPF probe did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("The frozen product-surface WPF probe failed.", failure);

        Console.WriteLine(
            "Frozen product surface: one experimental feature, absent persistence/superstructure/manual-deco/Dock surface, " +
            "normal Deco V/H and barbettes, Ocean presentation, and Grid/Ocean physical/export invariance passed.");
    }

    private static void VerifyExposureMatrix()
    {
        var off = new FeatureExposurePolicy(experimentalFeaturesEnabled: false);
        var on = new FeatureExposurePolicy(experimentalFeaturesEnabled: true);

        Require(off.IsAvailable(ProductFeature.ExpandedHullOptions),
            "Expanded hull options are no longer normal 2.0 functionality.");

        foreach (var deferred in new[]
                 {
                     ProductFeature.Superstructures,
                     ProductFeature.ExplicitSlopeRefinement,
                     ProductFeature.InvertedTriangleFill,
                 })
        {
            Require(FeatureExposurePolicy.MaturityOf(deferred) == FeatureMaturity.Unavailable &&
                    !off.IsAvailable(deferred) && !on.IsAvailable(deferred),
                $"{deferred} is still exposed; the Experimental Features toggle must not resurrect a deferred post-2.0 feature.");
        }

        Require(!off.IsAvailable(ProductFeature.InternalStructures) && on.IsAvailable(ProductFeature.InternalStructures),
            "Internal Structures is no longer the experimental feature behind the single toggle.");

        var experimental = Enum.GetValues<ProductFeature>()
            .Where(feature => FeatureExposurePolicy.MaturityOf(feature) == FeatureMaturity.Experimental)
            .ToArray();
        Require(experimental.SequenceEqual(new[] { ProductFeature.InternalStructures }),
            "Internal Structures is not the only feature behind Experimental Features.");
    }

    private static void VerifyEditorXamlSurface()
    {
        var path = Path.Combine(FindRepositoryRoot(), "FtdHullGenerator", "MainWindow.xaml");
        var xaml = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var names = xaml.Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var absent in new[]
                 {
                     // Project persistence UX.
                     "SaveProjectButton",
                     // Superstructure/pagoda controls.
                     "SuperstructureEnableCheckBox",
                     "SuperstructureEditor",
                     "SuperstructureStyleBox",
                     "SuperstructureLevelsInput",
                     "SuperstructureForeAftInput",
                     "SuperstructureMaterialBox",
                     "SuperstructureSmoothingBox",
                     // Old explicit/manual decorative slope-extension control.
                     "RefinementButton",
                 })
        {
            Require(!names.Contains(absent),
                $"The normal editor still declares the deferred '{absent}' control.");
        }

        Require(!xaml.Descendants().Any(element =>
                (string?)element.Attribute("Header") == "Superstructure (Experimental)"),
            "The normal editor still offers a superstructure/pagoda pane.");

        Require(!names.Any(name => name.Contains("Decoration", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("Refinement", StringComparison.OrdinalIgnoreCase)),
            "The normal editor still declares a manual decorative-extension/refinement control.");

        var buttonLabels = xaml.Descendants(presentation + "Button")
            .Select(element => (string?)element.Attribute("Content"))
            .Where(content => content is not null)
            .ToArray();
        foreach (var label in new[] { "New", "Open", "Save", "Save As" })
        {
            Require(!buttonLabels.Contains(label),
                $"The normal editor still offers the project '{label}' button.");
        }

        foreach (var present in new[]
                 {
                     "ExperimentalFeaturesToggle",
                     "SmoothingBox",
                     "ExportButton",
                     "DockedWorkspaceHost",
                     "Preview",
                 })
        {
            Require(names.Contains(present), $"The frozen 2.0 editor is missing the '{present}' surface.");
        }

        Require(!xaml.Descendants().Any(element => element.Name.LocalName is "ShapePresetGrid" or "ShapePresetHintText"),
            "The normal editor still contains the animal preset grid or hint.");
    }

    private static void VerifySceneSelectorSurface()
    {
        Require(PreviewSceneSelectorAdorner.NormalScenes.SequenceEqual(
                new[] { PreviewSceneKind.Grid, PreviewSceneKind.Ocean }),
            "The normal scene selector no longer offers exactly Grid and Ocean.");
        Require(!PreviewSceneSelectorAdorner.NormalScenes.Contains(PreviewSceneKind.Dock),
            "Dock is still offered in the normal scene selector.");
        Require(Enum.IsDefined(PreviewSceneKind.Dock),
            "Dock was deleted from the dormant scene vocabulary instead of only being hidden from the product surface.");

        // A persisted preference from an older build must not start the normal editor in a
        // dormant scene, while a normal persisted scene survives unchanged.
        Require(PreviewSceneSettings.ForProduct(new PreviewSceneSettings { Kind = PreviewSceneKind.Dock }).Kind ==
                    PreviewSceneKind.Grid,
            "A persisted dormant Dock scene did not fail closed to Grid on the normal product surface.");
        var ocean = new PreviewSceneSettings { Kind = PreviewSceneKind.Ocean, Waterline = 2.5, SceneSeed = 41 };
        Require(ReferenceEquals(PreviewSceneSettings.ForProduct(ocean), ocean),
            "A normal persisted Ocean scene was rewritten by the product normalization.");
    }

    private static void VerifyLiveEditorSurface()
    {
        var window = new MainWindow();
        try
        {
            var experimental = window.ExperimentalFeaturesEnabledForTests;
            Require((window.InternalStructureEditorForTests is not null) == experimental,
                "MainWindow internal-structure exposure did not follow the one Experimental Features toggle.");

            // Barbettes and the drag ruler are normal functionality: the arrangement is
            // resolved for the editor regardless of the experimental toggle.
            Require(window.WorkspaceForTests.ArrangementSolution is not null,
                "The barbette arrangement was not resolved in the normal editor surface.");

            // Deco Vertical/Horizontal are normal, parameter-free smoothing choices: they
            // are listed and the central exposure policy never gates them, with or without
            // the Experimental Features toggle.
            var smoothingLabels = window.SmoothingBox.Items.Cast<object>()
                .Select(item => item.ToString())
                .ToArray();
            Require(smoothingLabels.Contains("Deco Vertical") && smoothingLabels.Contains("Deco Horizontal"),
                "The normal smoothing list lost the parameter-free Deco Vertical/Horizontal choices.");
            foreach (var method in new[] { SmoothingMethod.DecoVertical, SmoothingMethod.DecoHorizontal })
            {
                Require(HullEditorSettings.ValidateFeatureAvailability(
                            HullParameters.Default with { Smoothing = method },
                            new FeatureExposurePolicy(false)) is null &&
                        HullEditorSettings.ValidateFeatureAvailability(
                            HullParameters.Default with { Smoothing = method },
                            new FeatureExposurePolicy(true)) is null,
                    $"{method} is still gated by Experimental Features.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyOceanPresentationAndInvariance(HullGenerator generator, FtdBlockCatalog catalog)
    {
        var exportDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-UI01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDirectory);
        try
        {
            var hull = generator.Generate(HullParameters.Default with { Length = 72, Width = 35, Height = 12 });
            var control = new HullPreviewControl { Width = 960, Height = 640 };
            control.SetHull(hull, highlightEdits: false);
            control.ShowView(PreviewView.Isometric);
            ArrangeAndRender(control, 960, 640);

            Require(control.EnvironmentModel is null, "Grid unexpectedly introduced 3D environment geometry.");
            var shipModel = control.ShipModel;
            var physical = PhysicalIdentity(hull);
            var camera = (PerspectiveCamera)control.Camera;
            var cameraState = (camera.Position, camera.LookDirection, control.ViewYaw, control.ViewPitch);
            var before = new BlueprintExporter().Export(hull, catalog, exportDirectory, "UI01 invariance");
            var blueprint = BlueprintIdentity(before.FilePath);

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
            var environment = control.EnvironmentModel ??
                throw new InvalidOperationException("Ocean did not create one frozen presentation-only environment.");
            Require(environment.IsFrozen,
                "Ocean presentation environment was not frozen.");

            Require(Materials(environment).OfType<DiffuseMaterial>().Any(material =>
                    material.Brush is SolidColorBrush { Opacity: var opacity } &&
                    Math.Abs(opacity - PreviewSceneFactory.OceanWaterOpacity) < 1e-9),
                "Ocean water is not using the frozen, appropriately opaque presentation finish.");
            Require(CountSkyDomes(environment) >= 1,
                "Ocean has no sky/horizon backdrop.");
            Require(CountLights(environment) >= 2,
                "Ocean did not add its own presentation lighting.");
            Require(environment.Bounds.SizeX > hull.OccupiedWidth &&
                    environment.Bounds.SizeZ > hull.OccupiedLength &&
                    environment.Bounds.SizeY > hull.OccupiedHeight,
                "Ocean backdrop did not scale beyond the hull.");

            var identity = ModelIdentity(environment);
            control.SceneSettings = ocean;
            Require(ModelIdentity(control.EnvironmentModel) == identity,
                "Identical deterministic Ocean settings changed presentation geometry.");

            Require(ReferenceEquals(control.CurrentHull, hull) && ReferenceEquals(control.ShipModel, shipModel) &&
                    PhysicalIdentity(hull) == physical &&
                    (camera.Position, camera.LookDirection, control.ViewYaw, control.ViewPitch) == cameraState,
                "Ocean replaced, regenerated, reframed or mutated the physical ship.");
            var afterOcean = new BlueprintExporter().Export(hull, catalog, exportDirectory, "UI01 invariance");
            Require(BlueprintIdentity(afterOcean.FilePath) == blueprint,
                "Ocean presentation state changed native blueprint output.");

            Require(FirstGeometry(environment) is { } sky && !control.IsShipSelectableModel(sky),
                "Ocean backdrop acquired ship-selection identity.");

            control.SceneSettings = PreviewSceneSettings.Grid;
            Require(control.EnvironmentModel is null && ReferenceEquals(control.ShipModel, shipModel),
                "Grid was not one scene change away or rebuilt the ship renderer.");
            var afterGrid = new BlueprintExporter().Export(hull, catalog, exportDirectory, "UI01 invariance");
            Require(BlueprintIdentity(afterGrid.FilePath) == blueprint,
                "Returning to Grid changed native blueprint output.");
        }
        finally
        {
            if (Directory.Exists(exportDirectory))
                Directory.Delete(exportDirectory, recursive: true);
        }
    }

    private static void ArrangeAndRender(HullPreviewControl control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32).Render(control);
    }

    private static IEnumerable<Material> Materials(Model3D? model)
    {
        switch (model)
        {
            case GeometryModel3D geometry:
                foreach (var material in Flatten(geometry.Material))
                    yield return material;
                break;
            case Model3DGroup group:
                foreach (var child in group.Children)
                    foreach (var material in Materials(child))
                        yield return material;
                break;
        }
    }

    private static IEnumerable<Material> Flatten(Material? material)
    {
        if (material is MaterialGroup group)
        {
            foreach (var child in group.Children)
                foreach (var nested in Flatten(child))
                    yield return nested;
        }
        else if (material is not null)
        {
            yield return material;
        }
    }

    private static int CountSkyDomes(Model3D? model) => model switch
    {
        null => 0,
        GeometryModel3D geometry => Flatten(geometry.Material)
            .OfType<EmissiveMaterial>()
            .Count(material => material.Brush is LinearGradientBrush) + (Flatten(geometry.BackMaterial)
                .OfType<EmissiveMaterial>()
                .Any(material => material.Brush is LinearGradientBrush) ? 1 : 0),
        Model3DGroup group => group.Children.Sum(CountSkyDomes),
        _ => 0,
    };

    private static int CountLights(Model3D? model) => model switch
    {
        null => 0,
        Light => 1,
        Model3DGroup group => group.Children.Sum(CountLights),
        _ => 0,
    };

    private static Model3D? FirstGeometry(Model3D? model)
    {
        if (model is GeometryModel3D)
            return model;
        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
                if (FirstGeometry(child) is { } geometry)
                    return geometry;
        }
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
                    foreach (var child in group.Children)
                        Visit(child);
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
                   throw new InvalidOperationException("The UI01 export was not a JSON object.");
        // ForceId is deliberately randomized by the established writer; normalize that one
        // non-structural field, then require every other serialized field and array to match.
        root["Blueprint"]!.AsObject()["ForceId"] = 0;
        return Hash(root.ToJsonString());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
