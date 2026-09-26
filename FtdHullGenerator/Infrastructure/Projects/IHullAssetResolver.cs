namespace FtdHullGenerator.Infrastructure.Projects;

/// <summary>
/// Resolves a historical envelope asset by its identity and content hash. The store accepts one so
/// a project can report a missing dependency instead of silently substituting a generic hull.
/// </summary>
public interface IHullAssetResolver
{
    bool Exists(string assetId, string assetVersion, string assetHash);
}
