namespace FtdHullGenerator.Domain;

/// <summary>
/// One metre of an armor stack. A null material reserves empty air, while structural
/// layers choose which longitudinal structural family fills the layer.
/// </summary>
public readonly record struct ArmorLayer
{
    public ArmorLayer(MaterialKind? material)
        : this(material, ArmorConstruction.Solid)
    {
    }

    /// <summary>Compatibility overload for callers that used the original pole flag.</summary>
    public ArmorLayer(MaterialKind? material, bool usePoles)
        : this(material, usePoles ? ArmorConstruction.Pole : ArmorConstruction.Solid)
    {
    }

    public ArmorLayer(MaterialKind? material, ArmorConstruction construction)
    {
        if (material is not null && !Enum.IsDefined(material.Value))
            throw new ArgumentException("An armor layer contains an unsupported material.", nameof(material));
        if (!Enum.IsDefined(construction))
            throw new ArgumentException("An armor layer contains an unsupported construction mode.", nameof(construction));

        Material = material;
        Construction = material is null ? ArmorConstruction.Solid : construction;
    }

    /// <summary>Null represents a deliberately empty one-metre air gap.</summary>
    public MaterialKind? Material { get; }

    /// <summary>Chooses the longitudinal member family when structural members are enabled.</summary>
    public ArmorConstruction Construction { get; }

    /// <summary>Compatibility projection of the original pole-only option.</summary>
    public bool UsePoles => Construction == ArmorConstruction.Pole;

    public bool IsAir => Material is null;

    public static ArmorLayer Air => new(null);

    public override string ToString() => IsAir || Construction == ArmorConstruction.Solid
        ? IsAir ? "Air" : $"{Material}"
        : $"{Material} {Construction}";
}

/// <summary>Per-layer longitudinal construction used by the armor editor and beam optimizer.</summary>
public enum ArmorConstruction
{
    Solid,
    Pole,
    BeamSlopeUp,
    BeamSlopeDown,
    BeamSlopeSpike,
}

/// <summary>
/// An ordered stack of one-metre armor layers. Layer zero is the exposed layer;
/// subsequent entries proceed inward (or downward for a deck).
/// </summary>
public sealed class ArmorLayout : IEquatable<ArmorLayout>
{
    private readonly ArmorLayer[] _layers;
    private readonly IReadOnlyList<ArmorLayer> _readOnlyLayers;

    public ArmorLayout(IEnumerable<MaterialKind> layers)
        : this(layers?.Select(material => new ArmorLayer(material)) ??
               throw new ArgumentNullException(nameof(layers)))
    {
    }

    public ArmorLayout(IEnumerable<ArmorLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        _layers = layers.ToArray();
        if (_layers.Length == 0)
            throw new ArgumentException("An armor layout must contain at least one layer.", nameof(layers));
        _readOnlyLayers = Array.AsReadOnly(_layers);
    }

    public IReadOnlyList<ArmorLayer> Layers => _readOnlyLayers;

    public int Thickness => _layers.Length;

    public MaterialKind SurfaceMaterial => _layers[0].Material ??
        throw new InvalidOperationException("The exposed armor layer must be structural.");

    /// <summary>True when empty armor separates structural layers on either side.</summary>
    public bool HasInternalAirGap => _layers.Select((layer, index) => (layer, index))
        .Any(pair => pair.layer.IsAir &&
                     _layers.Take(pair.index).Any(layer => !layer.IsAir) &&
                     _layers.Skip(pair.index + 1).Any(layer => !layer.IsAir));

    public static ArmorLayout Single(MaterialKind material) => new([new ArmorLayer(material)]);

    public bool Equals(ArmorLayout? other) =>
        ReferenceEquals(this, other) || other is not null && _layers.SequenceEqual(other._layers);

    public override bool Equals(object? obj) => obj is ArmorLayout other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var layer in _layers)
            hash.Add(layer);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join("/", _layers);
}
