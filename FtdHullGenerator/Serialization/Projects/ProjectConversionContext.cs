using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Serialization.Projects;

/// <summary>
/// Collects conversion findings and enforces the value-level gates (finite numbers, bounded
/// coordinates, defined enumerations) before a domain object is constructed. Shared by the
/// document serializer and by the legacy sidecar migrations.
/// </summary>
internal sealed class ProjectConversionContext
{
    public List<DesignDiagnostic> Diagnostics { get; } = [];

    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.IsError);

    public void Error(string code, string message, string? nodeId = null, string? field = null) =>
        Diagnostics.Add(DesignDiagnostic.Error(code, message, nodeId, field));

    public double? Finite(double value, string field)
    {
        if (double.IsFinite(value))
            return value;
        Error(ProjectDiagnosticCodes.InvalidValue,
            $"The value in '{field}' is not a finite number.", field: field);
        return null;
    }

    public DesignMeasure? Measure(int twiceMetres, string field)
    {
        var measure = DesignMeasure.FromTwiceMetres(twiceMetres);
        if (measure.IsWithinDesignBounds)
            return measure;
        Diagnostics.Add(new DesignDiagnostic(ProjectDiagnosticCodes.InvalidValue, DesignSeverity.Error,
            $"The coordinate {twiceMetres} in '{field}' is outside the supported design range.",
            Field: field,
            Requested: measure,
            Realized: DesignMeasure.FromTwiceMetres(DesignLimits.MaxDesignTwiceMetres),
            SuggestedCorrection: "Reduce the coordinate magnitude."));
        return null;
    }

    public void RequireDefined<TEnum>(TEnum value, string field) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            Error(ProjectDiagnosticCodes.InvalidValue,
                $"'{field}' declares an unsupported enumeration value.", field: field);
    }
}
