using System.Windows.Media.Media3D;

namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Presentation bounds supplied to the scene factory. They may size visual furniture but
/// are never returned to <c>GeneratedHull</c>, the editor revision, or the exporter.
/// </summary>
internal readonly record struct PreviewSceneBounds(
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double MinZ,
    double MaxZ)
{
    public static PreviewSceneBounds Empty { get; } = new(-10, 10, -2, 8, -14, 14);

    public double Width => Math.Max(1, MaxX - MinX);

    public double Height => Math.Max(1, MaxY - MinY);

    public double Length => Math.Max(1, MaxZ - MinZ);

    public Point3D Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2, (MinZ + MaxZ) / 2);
}

/// <summary>One immutable input to the procedural scene factory.</summary>
internal readonly record struct PreviewSceneContext(
    PreviewSceneBounds ShipBounds,
    PreviewSceneOcclusion Occlusion,
    bool LightTheme);

/// <summary>Observable presentation state for status UI and focused tests.</summary>
public sealed record PreviewScenePresentationState(
    PreviewSceneKind Kind,
    PreviewSceneOcclusion Occlusion,
    string StatusText);
