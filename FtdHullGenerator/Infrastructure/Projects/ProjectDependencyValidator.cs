using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Serialization.Projects;

namespace FtdHullGenerator.Infrastructure.Projects;

/// <summary>
/// Checks the external dependencies a document declares. A historical envelope is the only one
/// today: it names an asset by id, version and hash, and a missing asset is a recoverable
/// diagnostic rather than a substituted generic hull. Internal references (barbette nodes, tower
/// spans) are covered by <see cref="ShipDocument.Validate"/>.
/// </summary>
internal static class ProjectDependencyValidator
{
    public static IEnumerable<DesignDiagnostic> Validate(ShipDocument document, IHullAssetResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Source.Kind != HullSourceKind.HistoricalEnvelope)
            yield break;

        var assetId = document.Source.AssetId ?? string.Empty;
        var assetVersion = document.Source.AssetVersion ?? string.Empty;
        var assetHash = document.Source.AssetHash ?? string.Empty;

        if (resolver is null)
        {
            yield return DesignDiagnostic.Warning(ProjectDiagnosticCodes.MissingDependency,
                $"The historical envelope '{assetId}' v{assetVersion} was not checked because no asset " +
                "resolver is available. Hull Forge will not substitute a generic hull.",
                field: nameof(ShipDocument.Source));
            yield break;
        }

        if (!resolver.Exists(assetId, assetVersion, assetHash))
        {
            yield return new DesignDiagnostic(ProjectDiagnosticCodes.MissingDependency, DesignSeverity.Error,
                $"The historical envelope asset '{assetId}' v{assetVersion} ({assetHash}) is not available. " +
                "The document is retained, but its hull cannot be resolved.",
                Field: nameof(ShipDocument.Source),
                SuggestedCorrection: "Install or restore the referenced asset; a generic hull is never substituted.");
        }
    }
}
