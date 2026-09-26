using System.Text.Json;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Serialization.Projects.Migrations;

/// <summary>
/// The ordered set of supported legacy sidecar formats. It inspects the real format that
/// <see cref="GenerationParametersWriter"/> emits; it does not invent a format. A native
/// <c>.blueprint</c> is deliberately absent: it is not a reversible project input.
/// </summary>
internal static class ProjectMigrationRegistry
{
    /// <summary>The format identifier emitted by <see cref="GenerationParametersWriter"/>.</summary>
    public const string GenerationParametersFormat = "HullForge.GenerationParameters";

    public static IReadOnlyList<IProjectMigration> Migrations { get; } =
    [
        new GenerationParametersV1Migration(),
    ];

    /// <summary>
    /// Routes a non-envelope payload. The caller has already bounded its size and nesting.
    /// </summary>
    public static ProjectDocumentResult Migrate(JsonElement root)
    {
        var format = ReadString(root, "Format") ?? ReadString(root, "format");
        var version = ReadInt(root, "FormatVersion") ?? ReadInt(root, "formatVersion");

        if (string.Equals(format, GenerationParametersFormat, StringComparison.Ordinal))
        {
            if (version is null || version < 1)
                return Failure(ProjectDiagnosticCodes.MigrationUnsupported,
                    "The generation-parameter sidecar has no usable format version.");
            if (version > GenerationParametersWriter.CurrentFormatVersion)
                return FutureVersion(version.Value);

            var migration = Migrations.FirstOrDefault(candidate => candidate.CanMigrate(format!, version.Value));
            if (migration is null)
                return Failure(ProjectDiagnosticCodes.MigrationUnsupported,
                    $"No migration reads {GenerationParametersFormat} version {version}.");
            return migration.Migrate(root);
        }

        // A native blueprint has its own schema and is not reversibly editable project intent.
        if (root.TryGetProperty("Blueprint", out _) || root.TryGetProperty("blueprint", out _) ||
            root.TryGetProperty("SavedTotalBlockCount", out _))
        {
            return Failure(ProjectDiagnosticCodes.NotAProjectInput,
                "This is a native From the Depths .blueprint. A blueprint is not a reversible project " +
                "input: it records physical blocks, not design intent, and cannot be opened as a .hfship " +
                "document. Use a .hfship project or import a generation-parameter sidecar.");
        }

        return Failure(ProjectDiagnosticCodes.UnsupportedFormat,
            "The payload does not declare a Hull Forge project or a supported generation-parameter sidecar.");
    }

    private static ProjectDocumentResult FutureVersion(int version)
    {
        var diagnostic = new DesignDiagnostic(ProjectDiagnosticCodes.FutureVersion, DesignSeverity.Error,
            $"The generation-parameter sidecar declares version {version}, but this build understands at most " +
            $"{GenerationParametersWriter.CurrentFormatVersion}. It will not be imported as default settings.",
            Field: "FormatVersion",
            SuggestedCorrection: "Update Hull Forge to read this sidecar, or keep the file unchanged.");
        return new ProjectDocumentResult(null, [diagnostic], IsReadOnlyFallback: true);
    }

    private static ProjectDocumentResult Failure(string code, string message) =>
        ProjectDocumentResult.Failure(DesignDiagnostic.Error(code, message));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
