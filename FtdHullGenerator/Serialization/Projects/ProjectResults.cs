using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;

namespace FtdHullGenerator.Serialization.Projects;

/// <summary>The outcome of serializing a document. <see cref="Json"/> is null when nothing was emitted.</summary>
public sealed record ProjectSerializeResult(string? Json, IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool Succeeded => Json is not null && !Diagnostics.HasErrors();

    public static ProjectSerializeResult Failure(params DesignDiagnostic[] diagnostics) =>
        new(null, diagnostics);
}

/// <summary>
/// The outcome of turning a persisted payload into design intent. A future major version returns
/// <see cref="IsReadOnlyFallback"/> with a null document so a caller cannot mistake it for default
/// settings or silently overwrite the existing manual save.
/// </summary>
public sealed record ProjectDocumentResult(
    ShipDocument? Document,
    IReadOnlyList<DesignDiagnostic> Diagnostics,
    bool IsReadOnlyFallback = false,
    string? MigratedFrom = null)
{
    public bool Succeeded => Document is not null && !Diagnostics.HasErrors();

    public static ProjectDocumentResult Failure(params DesignDiagnostic[] diagnostics) =>
        new(null, diagnostics);
}
