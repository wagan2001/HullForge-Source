namespace FtdHullGenerator.Serialization.Decorations;

/// <summary>Confirmed constants and defensive implementation bounds for the decoration codec.</summary>
internal static class DecorationBinaryFormat
{
    public const uint SetId = 291231;
    public const uint TypeKey = 9999;
    public const uint ManagerId = 100000;

    public const int GenericModuleFixedPrefixLength = 12;
    public const int MinimumModulePrefixLength = 14;
    public const int FixedDecorationHeaderLength = 14;
    public const int IndexEntryLength = 7;
    public const int PreambleBytesPerDecoration = 26;

    // Defensive read bounds. Confirmed records are far smaller; these prevent corrupt length
    // fields from driving unbounded allocations while remaining well above representable output.
    public const int MaximumVehicleDataBytes = 16 * 1024 * 1024;
    public const int MaximumHeaderBytes = 1024 * 1024;
    public const int MaximumVehicleDataModules = 256;

    // Current first-party evidence uses at most nine 0xFFFF continuation words plus one remainder,
    // with split high-byte/low-u16 offsets. Exact multiples of 0xFFFF remain unobserved.
    public const int MaximumLengthWordCount = 10;
    public const int MaximumSegmentedPayloadBytes = ushort.MaxValue * MaximumLengthWordCount - 1;
    public const int MaximumSplit24BitValue = 0xFFFFFF;
    public const int MaximumDecorationCount = MaximumSegmentedPayloadBytes / PreambleBytesPerDecoration;

}
