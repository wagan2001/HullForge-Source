using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// A single hull-smoothing technique. A pass takes a validated hull and returns a
/// new one with additive bevel shapes inserted. It must not move or remove any
/// cell that the input hull already occupies, must keep the declared bounds, and
/// must keep port/starboard symmetry so <see cref="HullGeometryValidator" /> still
/// passes on its output.
/// </summary>
public interface ISmoothingPass
{
    /// <summary>The method this pass implements.</summary>
    SmoothingMethod Method { get; }

    /// <summary>Returns a new hull with this pass's shapes added. The input is left unchanged.</summary>
    GeneratedHull Apply(GeneratedHull hull);
}
