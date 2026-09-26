using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Builds bounded, non-interactive WPF scene furniture. Every vertex is derived only from
/// immutable settings and visual bounds; no wall clock, random process state, or physical
/// placement is consulted.
/// </summary>
internal static class PreviewSceneFactory
{
    private const int StandardWaterSteps = 22;
    private const double WaterAmplitude = 0.11;

    /// <summary>
    /// The presentation-only water surface opacity. Water reads as water rather than a
    /// translucent glass sheet, while the submerged hull remains faintly visible.
    /// </summary>
    internal const double OceanWaterOpacity = 0.88;

    /// <summary>The latitude bands used for the presentation-only Ocean sky gradient.</summary>
    internal const int OceanSkyBandCount = 16;

    /// <summary>The sky-dome radius multiplier over the water plane's half-extent.</summary>
    internal const double OceanSkyRadiusFactor = 2.4;

    public static Model3D? Create(PreviewSceneSettings settings, PreviewSceneContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var group = new Model3DGroup();
        var opacity = context.Occlusion switch
        {
            PreviewSceneOcclusion.Faded => 0.16,
            PreviewSceneOcclusion.Hidden => 0,
            _ => 1,
        };

        if (settings.Kind is PreviewSceneKind.Ocean or PreviewSceneKind.Dock && opacity > 0)
        {
            if (settings.Kind == PreviewSceneKind.Ocean)
            {
                AddOceanSky(group, settings, context, opacity);
                AddOceanLighting(group, context);
            }

            group.Children.Add(CreateWater(settings, context, opacity));
            if (settings.Kind == PreviewSceneKind.Dock)
                AddDock(group, settings, context, opacity);
        }

        if (settings.ShowGridOverlay)
            AddGrid(group, settings, context);

        if (group.Children.Count == 0)
            return null;
        group.Freeze();
        return group;
    }

    /// <summary>
    /// Half-extent of the presentation water plane around the hull. Large enough that the
    /// far edge sits at or beyond the horizon for small through large hulls, so the
    /// waterline reads as open sea rather than a floating raft.
    /// </summary>
    private static double WaterMargin(PreviewSceneBounds bounds) =>
        Math.Clamp(Math.Max(bounds.Width, bounds.Length) * 2.2, 60, 900);

    private static GeometryModel3D CreateWater(
        PreviewSceneSettings settings,
        PreviewSceneContext context,
        double opacity)
    {
        var bounds = context.ShipBounds;
        var margin = WaterMargin(bounds);
        var minX = bounds.MinX - margin;
        var maxX = bounds.MaxX + margin;
        var minZ = bounds.MinZ - margin;
        var maxZ = bounds.MaxZ + margin;
        var steps = settings.LowDetail || settings.ReducedMotion ? 1 : StandardWaterSteps;
        var mesh = new MeshGeometry3D();
        var phase = settings.SceneSeed * 0.0009765625 + settings.SceneTimeSeconds * 0.18;
        if (!settings.Deterministic)
        {
            // This is sampled only when immutable settings are explicitly applied. There
            // is no idle animation clock, so opting out of fixed evidence changes one
            // static water phase without creating continuous GPU work.
            phase += Environment.TickCount64 % 1_000_003 * 0.00006103515625;
        }

        for (var zStep = 0; zStep <= steps; zStep++)
        {
            var tz = zStep / (double)steps;
            var z = minZ + (maxZ - minZ) * tz;
            for (var xStep = 0; xStep <= steps; xStep++)
            {
                var tx = xStep / (double)steps;
                var x = minX + (maxX - minX) * tx;
                var wave = steps == 1
                    ? 0
                    : WaterAmplitude * (Math.Sin(x * 0.19 + phase) + Math.Cos(z * 0.13 - phase * 0.7)) * 0.5;
                mesh.Positions.Add(new Point3D(x, settings.Waterline + wave, z));
                mesh.TextureCoordinates.Add(new Point(tx, tz));
            }
        }

        for (var zStep = 0; zStep < steps; zStep++)
        {
            for (var xStep = 0; xStep < steps; xStep++)
            {
                var row = steps + 1;
                var a = zStep * row + xStep;
                var b = a + 1;
                var c = a + row + 1;
                var d = a + row;
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(c);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(d);
                mesh.TriangleIndices.Add(c);
            }
        }

        mesh.Freeze();
        var water = context.LightTheme ? Color.FromRgb(52, 132, 163) : Color.FromRgb(22, 92, 122);
        var brush = new SolidColorBrush(water) { Opacity = OceanWaterOpacity * opacity };
        brush.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(brush));
        var glint = new SolidColorBrush(context.LightTheme ? Color.FromRgb(214, 240, 246) : Color.FromRgb(150, 214, 232))
        {
            Opacity = 0.5 * opacity,
        };
        glint.Freeze();
        material.Children.Add(new SpecularMaterial(glint, settings.LowDetail ? 8 : 34));
        // A faint self-illumination keeps deep water readable instead of collapsing to
        // near-black in the dark workbench; it is presentation only.
        var lift = new SolidColorBrush(context.LightTheme ? Color.FromRgb(22, 56, 70) : Color.FromRgb(8, 34, 48))
        {
            Opacity = 0.35 * opacity,
        };
        lift.Freeze();
        material.Children.Add(new EmissiveMaterial(lift));
        material.Freeze();
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    /// <summary>
    /// Builds the presentation-only Ocean sky and horizon as one large emissive dome with
    /// a vertical gradient brush: water colour below the equator so the finite water
    /// plane's far edge cannot show a seam, a hazy horizon at the waterline, and a sky
    /// gradient above it. The dome is opaque and emissive, so it behaves as a backdrop
    /// and never relies on the hull lights or on transparent-object ordering.
    /// </summary>
    private static void AddOceanSky(
        Model3DGroup group,
        PreviewSceneSettings settings,
        PreviewSceneContext context,
        double opacity)
    {
        var bounds = context.ShipBounds;
        var span = Math.Max(bounds.Width, Math.Max(bounds.Height, bounds.Length));
        var radius = Math.Max(WaterMargin(bounds) * OceanSkyRadiusFactor, span * 6);
        var center = new Point3D(bounds.Center.X, settings.Waterline, bounds.Center.Z);
        // The below-horizon dome deliberately matches the water base colour, so where the
        // finite water plane ends the backdrop continues it without a visible edge.
        var water = context.LightTheme ? Color.FromRgb(52, 132, 163) : Color.FromRgb(22, 92, 122);
        var horizon = context.LightTheme ? Color.FromRgb(214, 232, 244) : Color.FromRgb(150, 190, 214);
        var zenith = context.LightTheme ? Color.FromRgb(86, 152, 210) : Color.FromRgb(30, 66, 118);

        const int longitudeSteps = 48;
        var latitudeSteps = settings.LowDetail || settings.ReducedMotion ? 8 : OceanSkyBandCount;
        var mesh = new MeshGeometry3D();
        for (var latitudeStep = 0; latitudeStep <= latitudeSteps; latitudeStep++)
        {
            var latitude = latitudeStep / (double)latitudeSteps;
            for (var segment = 0; segment <= longitudeSteps; segment++)
            {
                var longitude = segment / (double)longitudeSteps * Math.PI * 2;
                mesh.Positions.Add(DomePoint(center, radius, latitude, longitude));
                mesh.TextureCoordinates.Add(new Point(segment / (double)longitudeSteps, latitude));
            }
        }

        for (var latitudeStep = 0; latitudeStep < latitudeSteps; latitudeStep++)
        {
            for (var segment = 0; segment < longitudeSteps; segment++)
            {
                var a = latitudeStep * (longitudeSteps + 1) + segment;
                var b = a + 1;
                var c = a + longitudeSteps + 1;
                var d = c + 1;
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(c);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(c);
                mesh.TriangleIndices.Add(d);
            }
        }

        mesh.Freeze();
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Opacity = opacity,
        };
        brush.GradientStops.Add(new GradientStop(water, 0));
        brush.GradientStops.Add(new GradientStop(water, 0.46));
        brush.GradientStops.Add(new GradientStop(horizon, 0.5));
        brush.GradientStops.Add(new GradientStop(Blend(horizon, zenith, 0.45), 0.62));
        brush.GradientStops.Add(new GradientStop(zenith, 1));
        brush.Freeze();
        var material = new EmissiveMaterial(brush);
        material.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        group.Children.Add(model);
    }

    /// <summary>
    /// Adds presentation-only daylight over the fixed hull lights so Ocean reads as open
    /// air. The lights live in the environment branch and are removed with it when Grid
    /// is selected; they never touch hull geometry, bounds, statistics or export.
    /// </summary>
    private static void AddOceanLighting(Model3DGroup group, PreviewSceneContext context)
    {
        var sun = new DirectionalLight(
            context.LightTheme ? Color.FromRgb(214, 205, 184) : Color.FromRgb(150, 140, 116),
            new Vector3D(-0.35, -0.82, -0.45));
        var skyFill = new AmbientLight(
            context.LightTheme ? Color.FromRgb(46, 52, 60) : Color.FromRgb(26, 34, 48));
        group.Children.Add(sun);
        group.Children.Add(skyFill);
    }

    private static Point3D DomePoint(Point3D center, double radius, double latitudeFraction, double longitude)
    {
        var latitude = (latitudeFraction - 0.5) * Math.PI;
        var cosLatitude = Math.Cos(latitude);
        return new Point3D(
            center.X + radius * cosLatitude * Math.Cos(longitude),
            center.Y + radius * Math.Sin(latitude),
            center.Z + radius * cosLatitude * Math.Sin(longitude));
    }

    private static Color Blend(Color from, Color to, double fraction)
    {
        var t = Math.Clamp(fraction, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static void AddDock(
        Model3DGroup group,
        PreviewSceneSettings settings,
        PreviewSceneContext context,
        double opacity)
    {
        var bounds = context.ShipBounds;
        var clearance = Math.Clamp(bounds.Width * 0.08 + 2.5, 3.5, 14);
        var quayWidth = Math.Clamp(bounds.Width * 0.12 + 3, 4, 18);
        var lengthMargin = Math.Clamp(bounds.Length * 0.08 + 4, 6, 35);
        var top = settings.Waterline + 0.55;
        var thickness = settings.LowDetail ? 0.7 : 1.1;
        var minZ = bounds.MinZ - lengthMargin;
        var length = bounds.Length + lengthMargin * 2;
        var dockColor = context.LightTheme ? Color.FromRgb(115, 116, 112) : Color.FromRgb(65, 69, 72);
        var dockMaterial = Material(dockColor, 0.92 * opacity);

        group.Children.Add(Box(
            bounds.MinX - clearance - quayWidth,
            top - thickness,
            minZ,
            quayWidth,
            thickness,
            length,
            dockMaterial));
        group.Children.Add(Box(
            bounds.MaxX + clearance,
            top - thickness,
            minZ,
            quayWidth,
            thickness,
            length,
            dockMaterial));

        if (settings.LowDetail)
            return;

        var postMaterial = Material(
            context.LightTheme ? Color.FromRgb(83, 78, 70) : Color.FromRgb(43, 45, 48),
            opacity);
        var postHeight = Math.Clamp(bounds.Height * 0.12 + 1.2, 1.5, 5);
        var postSize = Math.Clamp(bounds.Width * 0.025, 0.45, 1.3);
        foreach (var x in new[]
                 {
                     bounds.MinX - clearance - postSize,
                     bounds.MaxX + clearance,
                 })
        {
            foreach (var z in new[] { bounds.MinZ, bounds.MaxZ })
            {
                group.Children.Add(Box(
                    x,
                    top,
                    z - postSize / 2,
                    postSize,
                    postHeight,
                    postSize,
                    postMaterial));
            }
        }
    }

    private static void AddGrid(Model3DGroup group, PreviewSceneSettings settings, PreviewSceneContext context)
    {
        var bounds = context.ShipBounds;
        var margin = Math.Clamp(Math.Max(bounds.Width, bounds.Length) * 0.12, 4, 40);
        var minX = Math.Floor(bounds.MinX - margin);
        var maxX = Math.Ceiling(bounds.MaxX + margin);
        var minZ = Math.Floor(bounds.MinZ - margin);
        var maxZ = Math.Ceiling(bounds.MaxZ + margin);
        var span = Math.Max(maxX - minX, maxZ - minZ);
        var step = settings.LowDetail ? 10 : span <= 120 ? 1 : span <= 300 ? 2 : 5;
        var y = settings.Kind == PreviewSceneKind.Grid ? bounds.MinY - 0.65 : settings.Waterline + 0.04;
        var minor = Material(context.LightTheme ? Color.FromRgb(45, 85, 112) : Color.FromRgb(90, 151, 184), 0.38);
        var major = Material(context.LightTheme ? Color.FromRgb(0, 92, 145) : Color.FromRgb(125, 194, 226), 0.68);
        var thickness = Math.Max(0.025, span / 9000);

        for (var x = Math.Ceiling(minX / step) * step; x <= maxX; x += step)
        {
            var material = IsMajor(x) ? major : minor;
            group.Children.Add(Box(x - thickness / 2, y, minZ, thickness, thickness, maxZ - minZ, material));
        }
        for (var z = Math.Ceiling(minZ / step) * step; z <= maxZ; z += step)
        {
            var material = IsMajor(z) ? major : minor;
            group.Children.Add(Box(minX, y, z - thickness / 2, maxX - minX, thickness, thickness, material));
        }

        bool IsMajor(double coordinate) => Math.Abs(coordinate % (step * 5)) < 1e-8;
    }

    private static Material Material(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static GeometryModel3D Box(
        double x,
        double y,
        double z,
        double width,
        double height,
        double length,
        Material material)
    {
        var p = new Point3DCollection
        {
            new(x, y, z), new(x + width, y, z), new(x + width, y + height, z), new(x, y + height, z),
            new(x, y, z + length), new(x + width, y, z + length), new(x + width, y + height, z + length), new(x, y + height, z + length),
        };
        var i = new Int32Collection
        {
            0,2,1, 0,3,2,
            4,5,6, 4,6,7,
            0,1,5, 0,5,4,
            3,7,6, 3,6,2,
            0,4,7, 0,7,3,
            1,2,6, 1,6,5,
        };
        var mesh = new MeshGeometry3D { Positions = p, TriangleIndices = i };
        mesh.Freeze();
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }
}
