namespace FtdHullGenerator.Serialization.Projects;

/// <summary>
/// Stable codes for project persistence findings. Codes are part of the persistence contract:
/// callers and tests match on them, so they are added, never repurposed. They live beside the
/// serializer rather than in <c>DesignDiagnosticCodes</c> because they describe the file and
/// store, not the design intent itself.
/// </summary>
public static class ProjectDiagnosticCodes
{
    /// <summary>The file is larger than the persistence budget, so it was rejected before reading.</summary>
    public const string FileTooLarge = "PRJ001";

    /// <summary>The payload is not valid JSON, or it is truncated.</summary>
    public const string InvalidJson = "PRJ002";

    /// <summary>The JSON envelope does not declare a recognized Hull Forge format.</summary>
    public const string UnsupportedFormat = "PRJ003";

    /// <summary>The file was written by a newer major envelope or schema; it is an explicit read-only path.</summary>
    public const string FutureVersion = "PRJ004";

    /// <summary>A value is malformed: NaN/infinity, an unsupported enum, or a bad numeric range.</summary>
    public const string InvalidValue = "PRJ006";

    /// <summary>A persisted display name or identifier contains path characters, traversal or a device name.</summary>
    public const string UnsafeName = "PRJ007";

    /// <summary>The document's extension payload exceeds its byte budget.</summary>
    public const string ExtensionBudget = "PRJ008";

    /// <summary>The document declares more objects than the persistence budget allows.</summary>
    public const string CountLimit = "PRJ009";

    /// <summary>The JSON nests deeper, or holds more elements or a longer string, than the budget allows.</summary>
    public const string NestingLimit = "PRJ010";

    /// <summary>The platform or filesystem cannot perform an atomic replace, so nothing was written.</summary>
    public const string AtomicWriteUnsupported = "PRJ011";

    /// <summary>A write failed; the previous file was left untouched.</summary>
    public const string WriteFailed = "PRJ012";

    /// <summary>A referenced historical envelope asset is missing. It is never replaced by a generic hull.</summary>
    public const string MissingDependency = "PRJ013";

    /// <summary>An existing access or engine limit rejected the loaded or saved document.</summary>
    public const string AccessDenied = "PRJ014";

    /// <summary>A native .blueprint is not a reversible project input.</summary>
    public const string NotAProjectInput = "PRJ015";

    /// <summary>The document failed the existing design validation after conversion.</summary>
    public const string ValidationFailed = "PRJ016";

    /// <summary>The requested file path is empty, unsafe, or uses an unsupported extension.</summary>
    public const string PathUnsafe = "PRJ017";

    /// <summary>A legacy sidecar's format is recognized but its version cannot be migrated.</summary>
    public const string MigrationUnsupported = "PRJ018";

    /// <summary>The file could not be read from disk.</summary>
    public const string ReadFailed = "PRJ019";

    /// <summary>
    /// A pre-frozen single-material/outside-diameter barbette cannot map losslessly to the frozen
    /// clear-volume model, so it is refused explicitly rather than having armor invented for it.
    /// </summary>
    public const string BarbetteMigrationUnsupported = "PRJ020";
}
