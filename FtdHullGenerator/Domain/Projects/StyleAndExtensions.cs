using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using FtdHullGenerator.Domain.Components;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Domain.Superstructures;

namespace FtdHullGenerator.Domain.Projects;

/// <summary>Which rule groups a style copies. Absolute dimensions are excluded by default.</summary>
public sealed record CopiedStyleFields(
    bool HullShape = true,
    bool Armor = true,
    bool Internals = true,
    bool ArrangementRules = true,
    bool Superstructure = true,
    bool Smoothing = true,
    bool Dimensions = false)
{
    public static CopiedStyleFields Everything { get; } = new();

    public static CopiedStyleFields Nothing { get; } =
        new(false, false, false, false, false, false, false);

    [JsonIgnore]
    public bool CopiesAnything =>
        HullShape || Armor || Internals || ArrangementRules || Superstructure || Smoothing || Dimensions;
}

/// <summary>
/// Provenance of a style that was copied into this document. The values live in the document;
/// this record only remembers where they came from, so editing the library later cannot mutate a
/// saved ship.
/// </summary>
public sealed record AppliedStyleProvenance(
    string StyleId,
    int StyleVersion,
    CopiedStyleFields CopiedFields,
    string? StyleName = null)
{
    public IEnumerable<DesignDiagnostic> Validate()
    {
        foreach (var diagnostic in Arrangement.RequireIdentifier(StyleId, "applied style", null))
            yield return diagnostic;
        if (StyleVersion < 1)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.StyleProvenanceInvalid,
                $"The applied style version must be at least 1, but is {StyleVersion}.",
                field: nameof(StyleVersion));
        if (!CopiedFields.CopiesAnything)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.StyleProvenanceInvalid,
                "An applied style must record at least one copied rule group.",
                field: nameof(CopiedFields));
    }
}

/// <summary>
/// A reusable style entry. Applying one copies resolved values with an explicit field mask; it
/// never links a project to a mutable library node.
/// </summary>
public sealed record FactionStyleEntry(
    string StyleId,
    int StyleVersion,
    string Name,
    string? Description,
    CopiedStyleFields Fields,
    HullShapeSettings? Shape = null,
    ArmorLayout? HullArmor = null,
    ArmorLayout? BottomArmor = null,
    ArmorLayout? DeckArmor = null,
    InternalStructure? Internals = null,
    IReadOnlyList<NamedArrangementGap>? NamedGaps = null,
    SuperstructureLayout? Superstructure = null,
    SmoothingSettings? Smoothing = null,
    HullDimensions? Dimensions = null)
{
    public IEnumerable<DesignDiagnostic> Validate()
    {
        var errors = new List<DesignDiagnostic>();
        errors.AddRange(Arrangement.RequireIdentifier(StyleId, "style", null));
        if (StyleVersion < 1)
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.StyleProvenanceInvalid,
                $"Style '{StyleId}' must carry a version of at least 1.", field: nameof(StyleVersion)));
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.DocumentNameMissing,
                $"Style '{StyleId}' needs a display name.", field: nameof(Name)));
        if (!Fields.CopiesAnything)
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.StyleProvenanceInvalid,
                $"Style '{StyleId}' copies no rule group.", field: nameof(Fields)));
        if (Fields.CopiesAnything && Shape is null && HullArmor is null && BottomArmor is null &&
            DeckArmor is null && Internals is null && NamedGaps is null && Superstructure is null &&
            Smoothing is null && Dimensions is null)
            errors.Add(DesignDiagnostic.Error(DesignDiagnosticCodes.StyleProvenanceInvalid,
                $"Style '{StyleId}' declares copied groups but carries no values to copy."));
        return errors;
    }
}

/// <summary>
/// Bounded, JSON-only extension values. Unknown future fields live here, never as .NET type
/// names or an arbitrary object graph.
/// </summary>
public sealed record DocumentExtensions(ImmutableDictionary<string, string> Values)
{
    public static DocumentExtensions Empty { get; } =
        new(ImmutableDictionary<string, string>.Empty);

    [JsonIgnore]
    public int ByteCount => Values.Sum(pair =>
        Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value));

    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (ByteCount > DesignLimits.MaxDocumentExtensionBytes)
            yield return DesignDiagnostic.Error(DesignDiagnosticCodes.ExtensionBudgetExceeded,
                $"The document's extension values occupy {ByteCount} bytes; the budget is " +
                $"{DesignLimits.MaxDocumentExtensionBytes}.",
                field: nameof(ByteCount));

        foreach (var key in Values.Keys)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > DesignLimits.MaxIdentifierLength)
                yield return DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                    $"Extension key '{key}' is empty or longer than {DesignLimits.MaxIdentifierLength} characters.",
                    field: nameof(Values));
            else if (!key.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
                yield return DesignDiagnostic.Error(DesignDiagnosticCodes.IdentifierMissing,
                    $"Extension key '{key}' may only contain letters, digits, '.', '-' and '_'.",
                    field: nameof(Values));
        }
    }
}
