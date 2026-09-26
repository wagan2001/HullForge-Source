using FtdHullGenerator.Domain.Decorations;

namespace FtdHullGenerator.Domain;

/// <summary>
/// The physical/decorative split for the smoothing selection. A decorative choice keeps its own
/// append-only persisted identity but generates exactly the corresponding proven native method;
/// the automatic visual extension is then derived from that resolved native plan. Nothing here
/// changes occupancy, collision, armor or the native anchor.
/// </summary>
public static class SmoothingMethodMapping
{
    /// <summary>The physical pass a selection generates. Decorative choices map to their native base.</summary>
    public static SmoothingMethod NativeBase(SmoothingMethod method) => method switch
    {
        SmoothingMethod.DecoVertical => SmoothingMethod.VerticalSlopeFill,
        SmoothingMethod.DecoHorizontal => SmoothingMethod.HorizontalSlopeFill,
        _ => method,
    };

    /// <summary>The automatic decoration kind for a selection, or null for a physical method.</summary>
    public static SlopeExtensionKind? DecorationKind(SmoothingMethod method) => method switch
    {
        SmoothingMethod.DecoVertical => SlopeExtensionKind.Vertical,
        SmoothingMethod.DecoHorizontal => SlopeExtensionKind.Horizontal,
        _ => null,
    };

    public static bool IsDecorative(SmoothingMethod method) => DecorationKind(method) is not null;
}
