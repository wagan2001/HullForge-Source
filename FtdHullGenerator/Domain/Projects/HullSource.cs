using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Projects;

/// <summary>Where a ship's hull shape comes from.</summary>
public enum HullSourceKind
{
    /// <summary>The existing Shape V2 regional model. The default and only generated source today.</summary>
    RegionalShapeV2 = 0,

    /// <summary>A read-only sampled historical envelope asset, identified by id, version and hash.</summary>
    HistoricalEnvelope = 1,
}

/// <summary>
/// The hull provenance of a document. A historical envelope carries the asset identity and
/// content hash so a missing dependency can be reported instead of silently substituted with
/// a generic animal hull.
/// </summary>
public sealed record HullSource(
    HullSourceKind Kind,
    string? AssetId = null,
    string? AssetVersion = null,
    string? AssetHash = null)
{
    public static HullSource Regional { get; } = new(HullSourceKind.RegionalShapeV2);

    public static HullSource Historical(string assetId, string assetVersion, string assetHash) =>
        new(HullSourceKind.HistoricalEnvelope, assetId, assetVersion, assetHash);

    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.HistoricalSourceIncomplete,
                "The document declares an unsupported hull source.");
            yield break;
        }

        if (Kind != HullSourceKind.HistoricalEnvelope)
            yield break;

        if (string.IsNullOrWhiteSpace(AssetId))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.HistoricalSourceIncomplete,
                "A historical envelope needs an asset identifier.", field: nameof(AssetId));
        if (string.IsNullOrWhiteSpace(AssetVersion))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.HistoricalSourceIncomplete,
                "A historical envelope needs an asset version.", field: nameof(AssetVersion));
        if (string.IsNullOrWhiteSpace(AssetHash))
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.HistoricalSourceIncomplete,
                "A historical envelope needs a content hash so provenance survives a missing dependency.",
                field: nameof(AssetHash));
    }
}

/// <summary>Explicit ship dimensions. Carried by a style only when the user opts in.</summary>
public sealed record HullDimensions(int Length, int Width, int Height)
{
    public static HullDimensions FromParameters(HullParameters parameters) =>
        new(parameters.Length, parameters.Width, parameters.Height);
}
