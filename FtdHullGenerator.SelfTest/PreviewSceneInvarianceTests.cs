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

/// <summary>
/// Pins V01's scene boundary: Grid remains the empty 3D environment around the existing
/// ship renderer, scene application leaves the physical snapshot/camera/cutaway unchanged,
/// and environment models cannot be classified as selectable ship models.
/// </summary>
internal static class PreviewSceneInvarianceTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(catalog);
        var hull = generator.Generate(HullParameters.Default with { Length = 18, Width = 9, Height = 7 });
        RunOnSta(hull, catalog);
        Console.WriteLine("Preview scene seam: Grid compatibility, physical/camera/cutaway identity, and environment exclusion passed.");
    }

    private static void RunOnSta(GeneratedHull hull, FtdBlockCatalog catalog)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyGridAndPhysicalIdentity(hull, catalog);
                VerifyEnvironmentCannotBecomeShipSelection(hull);
                VerifyInvalidSceneIsAtomic(hull);
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
    }

    private static void VerifyGridAndPhysicalIdentity(GeneratedHull hull, FtdBlockCatalog catalog)
    {
        var exportDirectory = Path.Combine(Path.GetTempPath(), $"HullForge-V01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDirectory);
        try
        {
            var control = new HullPreviewControl
            {
                Width = 960,
                Height = 640,
                Cutaway = CutawayPlane.Centreline,
                CutawayFraction = 0.4,
            };
            control.SetHull(hull, highlightEdits: false);
            control.ShowView(PreviewView.Side);

            var physicalHash = PhysicalIdentity(hull);
            var cutawayHash = PlacementIdentity(HullPreviewControl.SelectDrawnPlacements(
                hull, control.InternalArmorDrawn, control.Cutaway, control.CutawayFraction));
            var shipModel = control.ShipModel;
            var camera = RequireCamera(control);
            var cameraPosition = camera.Position;
            var lookDirection = camera.LookDirection;
            var yaw = control.ViewYaw;
            var pitch = control.ViewPitch;
            var beforeBlueprint = new BlueprintExporter().Export(hull, catalog, exportDirectory, "V01 scene identity");
            var blueprintHash = BlueprintIdentityHash(beforeBlueprint.FilePath);

            // Use a distinct immutable value to exercise the application path even though Grid
            // is intentionally V01's only scene kind.
            control.SceneSettings = new PreviewSceneSettings { Kind = PreviewSceneKind.Grid };

            Require(control.SceneSettings.Kind == PreviewSceneKind.Grid, "Grid was not retained as the active scene.");
            Require(control.EnvironmentModel is null, "Grid unexpectedly introduced 3D environment geometry.");
            Require(ReferenceEquals(control.CurrentHull, hull), "A scene change replaced the cached physical hull.");
            Require(ReferenceEquals(control.ShipModel, shipModel), "A scene change rebuilt the ship visual.");
            Require(PhysicalIdentity(hull) == physicalHash, "A scene change altered native placement/bounds identity.");
            var afterBlueprint = new BlueprintExporter().Export(hull, catalog, exportDirectory, "V01 scene identity");
            Require(BlueprintIdentityHash(afterBlueprint.FilePath) == blueprintHash,
                "A scene change altered the native blueprint identity outside the writer's volatile ForceId.");
            Require(PlacementIdentity(HullPreviewControl.SelectDrawnPlacements(
                        hull, control.InternalArmorDrawn, control.Cutaway, control.CutawayFraction)) == cutawayHash,
                "A scene change altered Grid cutaway selection.");
            Require(camera.Position == cameraPosition && camera.LookDirection == lookDirection &&
                    control.ViewYaw == yaw && control.ViewPitch == pitch,
                "A scene change moved or reframed the Grid camera.");

            var firstShipModel = FirstGeometry(control.ShipModel);
            Require(firstShipModel is not null && control.IsShipSelectableModel(firstShipModel),
                "The existing Grid ship model was excluded from ship selection.");
        }
        finally
        {
            Directory.Delete(exportDirectory, recursive: true);
        }
    }

    private static void VerifyEnvironmentCannotBecomeShipSelection(GeneratedHull hull)
    {
        var environment = EnvironmentOccluder(hull);
        var control = new HullPreviewControl(_ => environment)
        {
            Width = 800,
            Height = 600,
        };
        control.SetHull(hull, highlightEdits: false);
        control.ShowView(PreviewView.Side);
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.UpdateLayout();
        new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32).Render(control);

        var point = new Point(400, 300);
        var rawHits = HitModels(control, point);
        var environmentHit = rawHits.FirstOrDefault(hit => ReferenceEquals(hit.ModelHit, environment));
        var shipHits = rawHits.Where(hit => control.IsShipSelectableModel(hit.ModelHit)).ToArray();
        if (environmentHit is null)
            throw new InvalidOperationException("The arranged environment occluder was not hit at the test point.");
        Require(shipHits.Length > 0, "The arranged ship behind the environment was not hit at the test point.");
        Require(environmentHit.DistanceToRayOrigin < shipHits.Min(hit => hit.DistanceToRayOrigin),
            "The environment fixture did not actually occlude the ship along the test ray.");

        var selected = control.HitTestShip(point);
        Require(selected is not null && control.IsShipSelectableModel(selected),
            "Ship hit testing did not continue through the environment to the ship behind it.");
        Require(!ReferenceEquals(selected, environment),
            "Environment geometry was returned as selectable ship geometry.");
        Require(!control.IsShipSelectableModel(null), "A null hit was classified as selectable ship geometry.");
    }

    private static GeometryModel3D EnvironmentOccluder(GeneratedHull hull)
    {
        // The Side view camera lies on +X looking toward -X. This plane sits beyond the
        // starboard hull face toward the camera and spans the complete Y/Z silhouette.
        var x = hull.MaxX + 3d;
        var minimumY = hull.MinY - 4d;
        var maximumY = hull.MaxY + 5d;
        var minimumZ = hull.MinZ - 4d;
        var maximumZ = hull.MaxZ + 5d;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(x, minimumY, minimumZ),
                new(x, maximumY, minimumZ),
                new(x, maximumY, maximumZ),
                new(x, minimumY, maximumZ),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        var material = new DiffuseMaterial(Brushes.Black);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static IReadOnlyList<RayHitTestResult> HitModels(HullPreviewControl control, Point point)
    {
        var hits = new List<RayHitTestResult>();
        VisualTreeHelper.HitTest(
            control,
            null,
            result =>
            {
                if (result is RayHitTestResult ray)
                    hits.Add(ray);
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(point));
        return hits;
    }

    private static void VerifyInvalidSceneIsAtomic(GeneratedHull hull)
    {
        var control = new HullPreviewControl();
        control.SetHull(hull, highlightEdits: false);
        var originalSettings = control.SceneSettings;
        var originalShip = control.ShipModel;
        var originalHull = control.CurrentHull;

        try
        {
            control.SceneSettings = new PreviewSceneSettings { Kind = (PreviewSceneKind)int.MaxValue };
            throw new InvalidOperationException("An unknown scene kind was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        Require(ReferenceEquals(control.SceneSettings, originalSettings), "A rejected scene changed active settings.");
        Require(ReferenceEquals(control.ShipModel, originalShip), "A rejected scene rebuilt the ship visual.");
        Require(ReferenceEquals(control.CurrentHull, originalHull), "A rejected scene replaced the physical hull.");
    }

    private static PerspectiveCamera RequireCamera(HullPreviewControl control) =>
        control.Camera as PerspectiveCamera ?? throw new InvalidOperationException("Preview camera is not perspective.");

    private static Model3D? FirstGeometry(Model3D? model)
    {
        if (model is GeometryModel3D)
            return model;
        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                if (FirstGeometry(child) is { } geometry)
                    return geometry;
            }
        }
        return null;
    }

    private static string PhysicalIdentity(GeneratedHull hull) => Hash(string.Join("\n",
        $"{hull.MinX},{hull.MaxX},{hull.MinY},{hull.MaxY},{hull.MinZ},{hull.MaxZ}",
        PlacementIdentity(hull.Blocks)));

    private static string PlacementIdentity(IEnumerable<BlockPlacement> blocks) => Hash(string.Join("\n",
        blocks.Select(block =>
            $"{block.X},{block.Y},{block.Z}|{(int)block.Shape}|{block.Rotation}|{(int)block.Material}|{block.ArmorDepth}|{(int)block.Origin}")));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string BlueprintIdentityHash(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ??
            throw new InvalidOperationException("The V01 export was not a JSON object.");
        // ForceId is deliberately randomized by the established writer. Normalize that one
        // non-structural field, then require every other serialized field and array to match.
        root["Blueprint"]!.AsObject()["ForceId"] = 0;
        return Hash(root.ToJsonString());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
