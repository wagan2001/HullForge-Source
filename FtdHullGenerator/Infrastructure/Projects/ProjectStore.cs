using System.Security;
using System.Text;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Serialization.Projects;

namespace FtdHullGenerator.Infrastructure.Projects;

/// <summary>The outcome of an atomic project save. <see cref="Path"/> is null when nothing was written.</summary>
public sealed record ProjectSaveResult(string? Path, string? BackupPath, IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool Succeeded => Path is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Reads and writes <c>.hfship</c> documents. Writes are atomic: the payload is written to a
/// same-directory temporary file, flushed to disk, then replaced. The previous valid file is kept
/// as a <c>.bak</c> backup. When the platform or filesystem cannot perform an atomic replace, the
/// original is kept and an explicit error is returned; atomic success is never simulated.
/// </summary>
public static class ProjectStore
{
    public const string DocumentExtension = ProjectPathSafety.DocumentExtension;
    public const string BackupExtension = ProjectPathSafety.BackupExtension;

    /// <summary>
    /// Writes a document. The existing design validation runs first; an invalid document never
    /// replaces the previous manual save. Access and engine limits are re-applied on
    /// <see cref="Load"/>, not here, because save trusts the live in-memory document.
    /// </summary>
    public static ProjectSaveResult Save(ShipDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<DesignDiagnostic>();
        if (!ProjectPathSafety.TryResolvePath(path, allowLegacySidecar: false, out var fullPath,
                out var pathDiagnostic))
        {
            diagnostics.Add(pathDiagnostic!);
            return new ProjectSaveResult(null, null, diagnostics);
        }

        var serialized = ProjectDocumentSerializer.Serialize(document);
        diagnostics.AddRange(serialized.Diagnostics);
        if (serialized.Json is null)
            return new ProjectSaveResult(null, null, diagnostics);

        var backupPath = fullPath + BackupExtension;
        try
        {
            WriteAtomically(fullPath, serialized.Json, backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              NotSupportedException or SecurityException)
        {
            diagnostics.Add(WriteFailure(exception));
            return new ProjectSaveResult(null, null, diagnostics);
        }

        return new ProjectSaveResult(fullPath, File.Exists(backupPath) ? backupPath : null, diagnostics);
    }

    /// <summary>
    /// Reads a document, or imports a supported generation-parameter sidecar into a fresh document.
    /// After conversion, engine and dependency validation runs. Runtime exposure is deliberately
    /// evaluated by the application after load so unavailable intent can still round-trip intact.
    /// </summary>
    public static ProjectDocumentResult Load(
        string path,
        IHullAssetResolver? resolver = null)
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (!ProjectPathSafety.TryResolvePath(path, allowLegacySidecar: true, out var fullPath,
                out var pathDiagnostic))
            return new ProjectDocumentResult(null, [pathDiagnostic!]);

        if (!File.Exists(fullPath))
            return new ProjectDocumentResult(null, [ReadFailure(
                new FileNotFoundException($"No project file exists at '{fullPath}'.", fullPath))]);

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ProjectDocumentResult(null, [ReadFailure(exception)]);
        }

        if (length > ProjectDocumentSerializer.MaxDocumentBytes)
            return new ProjectDocumentResult(null, [TooLarge(length)]);

        string json;
        try
        {
            json = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ProjectDocumentResult(null, [ReadFailure(exception)]);
        }

        var result = ProjectDocumentSerializer.Deserialize(json);
        if (result.Document is null)
            return result;

        diagnostics.AddRange(result.Diagnostics);
        diagnostics.AddRange(ProjectDependencyValidator.Validate(result.Document, resolver));
        return result with { Diagnostics = diagnostics };
    }

    private static void WriteAtomically(string targetPath, string json, string backupPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException("The project path has no directory.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");

        var tempPath = Path.Combine(directory,
            Path.GetFileName(targetPath) + ".tmp-" + Guid.NewGuid().ToString("n"));
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                // Atomic on NTFS and keeps the previous valid file as the backup. A filesystem
                // that cannot do this throws, and the original is left in place.
                File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static DesignDiagnostic WriteFailure(Exception exception)
    {
        var unsupported = exception is NotSupportedException;
        return new DesignDiagnostic(
            unsupported ? ProjectDiagnosticCodes.AtomicWriteUnsupported : ProjectDiagnosticCodes.WriteFailed,
            DesignSeverity.Error,
            unsupported
                ? $"The filesystem cannot replace the project atomically: {exception.Message}. " +
                  "Nothing was written and the previous file was kept."
                : $"The project file could not be written: {exception.Message}. The previous file was kept.",
            Field: "path",
            SuggestedCorrection: "Choose a local folder whose filesystem supports atomic replacement, then retry.");
    }

    private static DesignDiagnostic ReadFailure(Exception exception) =>
        new(ProjectDiagnosticCodes.ReadFailed, DesignSeverity.Error,
            $"The project file could not be read: {exception.Message}",
            Field: "path",
            SuggestedCorrection: "Check the file and folder permissions, or restore it from its backup.");

    private static DesignDiagnostic TooLarge(long bytes) =>
        new(ProjectDiagnosticCodes.FileTooLarge, DesignSeverity.Error,
            $"The file is {bytes} bytes; the persistence budget is {ProjectDocumentSerializer.MaxDocumentBytes} bytes.",
            Field: "length",
            SuggestedCorrection: "Open a Hull Forge project; a native blueprint is not a project input.");

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; never mask the original failure.
        }
    }
}
