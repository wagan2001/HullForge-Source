using System.Security;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Serialization.Projects;

namespace FtdHullGenerator.Infrastructure.Projects;

/// <summary>
/// Guards the project store and the serializer against unsafe file paths and against display
/// names or identifiers that could be used as paths. Contract C03 keeps machine-local paths out of
/// shareable documents; this helper keeps path characters out of the names that do travel.
/// </summary>
internal static class ProjectPathSafety
{
    public const string DocumentExtension = ".hfship";
    public const string BackupExtension = ".bak";
    public const string LegacySidecarExtension = ".generation.json";

    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

    /// <summary>Returns a human-readable reason when <paramref name="value"/> is unsafe, otherwise null.</summary>
    public static string? FindUnsafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "it is empty";
        if (value.Length > 256)
            return "it is longer than 256 characters";
        if (value is "." or "..")
            return "it is a relative path segment";
        if (value.IndexOfAny(InvalidNameCharacters) >= 0)
            return "it contains a path or filename character";
        if (value.Any(char.IsControl))
            return "it contains a control character";
        if (IsReservedDeviceName(value))
            return "it is a reserved Windows device name";
        return null;
    }

    public static bool IsReservedDeviceName(string name)
    {
        var stem = name;
        var separator = stem.IndexOf('.');
        if (separator >= 0)
            stem = stem[..separator];
        return ReservedDeviceNames.Contains(stem.TrimEnd(' '));
    }

    /// <summary>
    /// Resolves a user-supplied path and rejects empty, unsafe and unknown-extension paths. A
    /// relative path is resolved against the current directory rather than being rejected. Reads
    /// may also open a legacy generation-parameter sidecar; writes may only target a document.
    /// </summary>
    public static bool TryResolvePath(
        string? path,
        bool allowLegacySidecar,
        out string fullPath,
        out DesignDiagnostic? diagnostic)
    {
        fullPath = string.Empty;
        diagnostic = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostic = PathDiagnostic("The project path is empty.");
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                              PathTooLongException or SecurityException)
        {
            diagnostic = PathDiagnostic($"The project path is not usable: {exception.Message}");
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        var unsafeReason = FindUnsafeName(fileName);
        if (unsafeReason is not null)
        {
            diagnostic = PathDiagnostic($"The project filename is unsafe: {unsafeReason}.");
            return false;
        }

        var isDocument = fullPath.EndsWith(DocumentExtension, StringComparison.OrdinalIgnoreCase);
        var isLegacy = fullPath.EndsWith(LegacySidecarExtension, StringComparison.OrdinalIgnoreCase);
        if (!isDocument && !(allowLegacySidecar && isLegacy))
        {
            diagnostic = PathDiagnostic(allowLegacySidecar
                ? $"A project file must end in '{DocumentExtension}' or '{LegacySidecarExtension}'."
                : $"A project file must end in '{DocumentExtension}'.");
            return false;
        }

        return true;
    }

    private static DesignDiagnostic PathDiagnostic(string message) => new(
        ProjectDiagnosticCodes.PathUnsafe,
        DesignSeverity.Error,
        message,
        Field: "path",
        SuggestedCorrection: "Choose a normal file inside a writable folder.");

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }
}
