using System.Collections.ObjectModel;

namespace FtdHullGenerator.Geometry;

/// <summary>
/// Describes a hull request or generated lattice that cannot produce a valid,
/// connected layered shell with a usable cavity.
/// </summary>
public sealed class HullGenerationException : InvalidOperationException
{
    /// <summary>
    /// Gets the individual validation failures that prevented generation.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Initializes an exception containing one or more generation failures.
    /// </summary>
    public HullGenerationException(IEnumerable<string> errors)
        : this(Normalize(errors))
    {
    }

    private HullGenerationException(string[] errors)
        : base(CreateMessage(errors))
    {
        Errors = new ReadOnlyCollection<string>(errors);
    }

    private static string[] Normalize(IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var normalized = errors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return normalized.Length == 0
            ? ["Hull generation failed for an unspecified reason."]
            : normalized;
    }

    private static string CreateMessage(IEnumerable<string> errors) =>
        $"Hull generation failed:{Environment.NewLine} - {string.Join(Environment.NewLine + " - ", errors)}";
}
