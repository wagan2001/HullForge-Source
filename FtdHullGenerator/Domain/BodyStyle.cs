namespace FtdHullGenerator.Domain;

/// <summary>
/// A user-facing family of midship sections. Each named style supplies a useful
/// starting bundle; <see cref="Custom" /> identifies a bundle the operator edited.
/// </summary>
public enum BodyStyle
{
    Rounded = 0,
    V,
    DeepV,
    U,
    FlatWide,
    HardChine,
    Tumblehome,
    Custom,
}
