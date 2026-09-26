using System.Security.Cryptography;
using System.Text;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.Infrastructure.Projects;
using FtdHullGenerator.Serialization.Projects;

namespace FtdHullGenerator.UI.Editor;

/// <summary>Machine-local autosave snapshots. Absolute recovery paths never enter a ship document.</summary>
public sealed class EditorRecoveryStore
{
    public EditorRecoveryStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public static bool TryGetDefaultDirectories(out string projectsDirectory, out string recoveryDirectory)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            projectsDirectory = recoveryDirectory = string.Empty;
            return false;
        }

        var root = Path.Combine(documents, "Hull Forge");
        projectsDirectory = Path.Combine(root, "Projects");
        recoveryDirectory = Path.Combine(root, "Recovery");
        return true;
    }

    public ProjectSaveResult Save(ShipDocument document)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ProjectSaveResult(null, null,
            [
                new DesignDiagnostic(ProjectDiagnosticCodes.WriteFailed, DesignSeverity.Error,
                    $"The recovery folder could not be created: {exception.Message}", Field: "path",
                    SuggestedCorrection: "Choose a writable Documents location and save the project manually.")
            ]);
        }

        return ProjectStore.Save(document, PathFor(document.DocumentId));
    }

    public IReadOnlyList<RecoveryCandidate> FindCandidates()
    {
        if (!Directory.Exists(RootDirectory))
            return [];

        var candidates = new List<RecoveryCandidate>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*" + ProjectStore.DocumentExtension,
                     SearchOption.TopDirectoryOnly))
        {
            var result = ProjectStore.Load(path);
            if (result.Succeeded)
                candidates.Add(new RecoveryCandidate(path, File.GetLastWriteTimeUtc(path), result.Document!));
        }
        return candidates.OrderByDescending(candidate => candidate.LastWriteTimeUtc).ToArray();
    }

    public void Delete(string documentId)
    {
        var path = PathFor(documentId);
        DeleteFile(path);
        DeleteFile(path + ProjectStore.BackupExtension);
    }

    public void DeleteCandidate(RecoveryCandidate candidate)
    {
        var fullPath = Path.GetFullPath(candidate.Path);
        var rootPrefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recovery candidate is outside this recovery store.");
        DeleteFile(fullPath);
        DeleteFile(fullPath + ProjectStore.BackupExtension);
    }

    private string PathFor(string documentId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId))).ToLowerInvariant();
        return Path.Combine(RootDirectory, hash + ProjectStore.DocumentExtension);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort; a retained recovery snapshot is safer than masking a save.
        }
    }
}

public sealed record RecoveryCandidate(string Path, DateTime LastWriteTimeUtc, ShipDocument Document);
