using FtdHullGenerator.Domain.Design;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FtdHullGenerator.Domain.Historical;

public enum SignedSourceAxis
{
    PositiveX = 0,
    NegativeX = 1,
    PositiveY = 2,
    NegativeY = 3,
    PositiveZ = 4,
    NegativeZ = 5,
}

/// <summary>A bijective signed permutation from source coordinates into Hull Forge X/Y/Z.</summary>
public sealed record ObjAxisMapping(
    SignedSourceAxis Starboard,
    SignedSourceAxis Up,
    SignedSourceAxis Forward)
{
    public static ObjAxisMapping Identity { get; } = new(
        SignedSourceAxis.PositiveX, SignedSourceAxis.PositiveY, SignedSourceAxis.PositiveZ);

    public bool IsValid => Enum.IsDefined(Starboard) && Enum.IsDefined(Up) && Enum.IsDefined(Forward) &&
                           AxisNumber(Starboard) != AxisNumber(Up) &&
                           AxisNumber(Starboard) != AxisNumber(Forward) &&
                           AxisNumber(Up) != AxisNumber(Forward);

    public Vector3 Transform(Vector3 source)
    {
        if (!IsValid)
            throw new InvalidOperationException("The OBJ axis map is not a bijective signed permutation.");
        return new Vector3(Component(source, Starboard), Component(source, Up), Component(source, Forward));
    }

    public int DeterminantSign => !IsValid ? 0 : Math.Sign(Vector3.Dot(
        AxisVector(Starboard), Vector3.Cross(AxisVector(Up), AxisVector(Forward))));

    private static int AxisNumber(SignedSourceAxis axis) => (int)axis / 2;
    private static float Component(Vector3 value, SignedSourceAxis axis) => axis switch
    {
        SignedSourceAxis.PositiveX => value.X,
        SignedSourceAxis.NegativeX => -value.X,
        SignedSourceAxis.PositiveY => value.Y,
        SignedSourceAxis.NegativeY => -value.Y,
        SignedSourceAxis.PositiveZ => value.Z,
        SignedSourceAxis.NegativeZ => -value.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };
    private static Vector3 AxisVector(SignedSourceAxis axis) => axis switch
    {
        SignedSourceAxis.PositiveX => Vector3.UnitX,
        SignedSourceAxis.NegativeX => -Vector3.UnitX,
        SignedSourceAxis.PositiveY => Vector3.UnitY,
        SignedSourceAxis.NegativeY => -Vector3.UnitY,
        SignedSourceAxis.PositiveZ => Vector3.UnitZ,
        SignedSourceAxis.NegativeZ => -Vector3.UnitZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };
}

/// <summary>The evidence tier attached to a historical hull description.</summary>
public enum HistoricalAccuracyLabel
{
    Inspired = 0,
    Approximate = 1,
    SourceFaithful = 2,
}

/// <summary>The finite, serialized result of one bounded historical-authoring attempt.</summary>
[JsonConverter(typeof(HistoricalAuthoringOutcomeJsonConverter))]
public enum HistoricalAuthoringOutcome
{
    Representable = 0,
    RepresentableWithDocumentedApproximation = 1,
    UnsupportedByCurrentEnvelopeRepresentation = 2,
    SourceOrRightsBlocked = 3,
    InvalidInput = 4,
}

public sealed class HistoricalAuthoringOutcomeJsonConverter : JsonConverter<HistoricalAuthoringOutcome>
{
    public override HistoricalAuthoringOutcome Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Historical authoring outcome must be a string.");
        return reader.GetString() switch
        {
            "representable" => HistoricalAuthoringOutcome.Representable,
            "representable with documented approximation" =>
                HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation,
            "unsupported by current envelope representation" =>
                HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation,
            "source/rights blocked" => HistoricalAuthoringOutcome.SourceOrRightsBlocked,
            "invalid input" => HistoricalAuthoringOutcome.InvalidInput,
            _ => throw new JsonException("Historical authoring outcome is unsupported."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        HistoricalAuthoringOutcome value,
        JsonSerializerOptions options) => writer.WriteStringValue(value switch
        {
            HistoricalAuthoringOutcome.Representable => "representable",
            HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation =>
                "representable with documented approximation",
            HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation =>
                "unsupported by current envelope representation",
            HistoricalAuthoringOutcome.SourceOrRightsBlocked => "source/rights blocked",
            HistoricalAuthoringOutcome.InvalidInput => "invalid input",
            _ => throw new JsonException("Historical authoring outcome is unsupported."),
        });
}

public static class HistoricalAuthoringOutcomes
{
    public static int ExitCode(this HistoricalAuthoringOutcome outcome) => outcome switch
    {
        HistoricalAuthoringOutcome.Representable or
            HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation => 0,
        HistoricalAuthoringOutcome.InvalidInput => 2,
        HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation => 4,
        HistoricalAuthoringOutcome.SourceOrRightsBlocked => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static HistoricalAuthoringOutcome ClassifyRepresentability(
        bool shapeFitAccepted,
        HistoricalFidelityReport fidelity)
    {
        ArgumentNullException.ThrowIfNull(fidelity);
        var fallbackRepresentable = fidelity.Representation ==
                                    HistoricalHullRepresentation.SampledEnvelopeFallback &&
                                    fidelity.GeometryAccepted &&
                                    fidelity.RuntimeRepresentationSupported;
        if ((shapeFitAccepted || fallbackRepresentable) &&
            !fidelity.Packaging.CanPackageDerivedEnvelope)
            return HistoricalAuthoringOutcome.SourceOrRightsBlocked;
        if (!fallbackRepresentable)
            return HistoricalAuthoringOutcome.UnsupportedByCurrentEnvelopeRepresentation;
        return fidelity.RequestedAccuracy == HistoricalAccuracyLabel.SourceFaithful
            ? HistoricalAuthoringOutcome.Representable
            : HistoricalAuthoringOutcome.RepresentableWithDocumentedApproximation;
    }
}

/// <summary>The construction status stated by the cited historical source.</summary>
public enum HistoricalConstructionStatus
{
    Built = 0,
    Planned = 1,
    Cancelled = 2,
    PlannedCancelled = 3,
}

/// <summary>Rights disposition for distributing a derived envelope, not the source file itself.</summary>
public enum HistoricalRightsDisposition
{
    Unreviewed = 0,
    RightsBlocked = 1,
    ClearedForDerivedRedistribution = 2,
}

/// <summary>Whether the dimensions used to scale the source were checked against adequate evidence.</summary>
public enum HistoricalDimensionEvidence
{
    Unverified = 0,
    Verified = 1,
}

/// <summary>
/// Rights and evidence needed to distribute a derived historical envelope. This record deliberately
/// says nothing about geometric fidelity: a permitted source can still be inaccurate.
/// </summary>
public sealed record HistoricalPackagingStatus(
    HistoricalRightsDisposition Rights,
    HistoricalDimensionEvidence Dimensions,
    string? LicenseIdentifier,
    string? Attribution,
    bool DerivativesPermitted,
    bool RedistributionPermitted,
    bool HumanRightsReviewComplete,
    string? ReviewNote = null)
{
    public bool CanPackageDerivedEnvelope =>
        Rights == HistoricalRightsDisposition.ClearedForDerivedRedistribution &&
        Dimensions == HistoricalDimensionEvidence.Verified &&
        DerivativesPermitted && RedistributionPermitted && HumanRightsReviewComplete &&
        !string.IsNullOrWhiteSpace(LicenseIdentifier);

    public IReadOnlyList<DesignDiagnostic> Validate()
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (!Enum.IsDefined(Rights) || !Enum.IsDefined(Dimensions))
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.PackagingEvidenceInvalid,
                "Historical packaging evidence contains an unsupported status."));

        if (Rights == HistoricalRightsDisposition.RightsBlocked && CanPackageDerivedEnvelope)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.PackagingEvidenceInvalid,
                "A rights-blocked source cannot be packageable."));

        if (Rights == HistoricalRightsDisposition.ClearedForDerivedRedistribution &&
            string.IsNullOrWhiteSpace(LicenseIdentifier))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.PackagingEvidenceInvalid,
                "Cleared packaging evidence must identify the licence or permission record.",
                field: nameof(LicenseIdentifier)));
        }

        return diagnostics;
    }
}

public enum HistoricalFidelityStageStatus
{
    NotMeasured = 0,
    Passed = 1,
    Failed = 2,
}

public enum HistoricalHullRepresentation
{
    ShapeV2Fit = 0,
    SampledEnvelopeFallback = 1,
}

/// <summary>Measured invariants of the exact sampled-envelope voxel composition used for review.</summary>
public sealed record HistoricalFallbackValidation(
    string HullParametersHash,
    SmoothingMethod Smoothing,
    int OccupiedCellCount,
    int ShellCellCount,
    int StructuralArmorCellCount,
    int ReservedAirCellCount,
    int UsableCavityCellCount,
    int MinX,
    int MaxX,
    int MinY,
    int MaxY,
    int MinZ,
    int MaxZ,
    bool FaceConnected,
    bool ContextHullBoundsMatch,
    string Measurement)
{
    public IReadOnlyList<DesignDiagnostic> Validate()
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (string.IsNullOrWhiteSpace(HullParametersHash) || HullParametersHash.Length != 64 ||
            HullParametersHash.Any(character => !Uri.IsHexDigit(character)) ||
            Smoothing != SmoothingMethod.None ||
            OccupiedCellCount <= 0 || ShellCellCount <= 0 ||
            StructuralArmorCellCount <= 0 || ReservedAirCellCount < 0 ||
            UsableCavityCellCount <= 0 ||
            StructuralArmorCellCount + ReservedAirCellCount >= OccupiedCellCount ||
            MinX > MaxX || MinY > MaxY || MinZ > MaxZ ||
            !FaceConnected || !ContextHullBoundsMatch || string.IsNullOrWhiteSpace(Measurement))
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "Sampled fallback validation must record connected occupied geometry, a usable cavity, " +
                "bounded shell/armor intent and matching context/hull bounds.",
                field: nameof(HistoricalFallbackValidation)));
        return diagnostics;
    }
}

/// <summary>One independently measured fidelity stage.</summary>
public sealed record HistoricalFidelityMetric(
    HistoricalFidelityStageStatus Status,
    double MaximumErrorMetres,
    double MeanErrorMetres,
    double RootMeanSquareErrorMetres,
    int SampleCount,
    double? AcceptanceThresholdMetres,
    string Measurement,
    string? Note = null)
{
    public static HistoricalFidelityMetric NotMeasured(string measurement, string? note = null) =>
        new(HistoricalFidelityStageStatus.NotMeasured, 0, 0, 0, 0, null, measurement, note);

    public IReadOnlyList<DesignDiagnostic> Validate(string field)
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (!Enum.IsDefined(Status) || !double.IsFinite(MaximumErrorMetres) || MaximumErrorMetres < 0 ||
            !double.IsFinite(MeanErrorMetres) || MeanErrorMetres < 0 || MeanErrorMetres > MaximumErrorMetres + 1e-9 ||
            !double.IsFinite(RootMeanSquareErrorMetres) || RootMeanSquareErrorMetres < MeanErrorMetres - 1e-9 ||
            RootMeanSquareErrorMetres > MaximumErrorMetres + 1e-9 || SampleCount < 0 ||
            AcceptanceThresholdMetres is < 0 ||
            AcceptanceThresholdMetres is double threshold && !double.IsFinite(threshold) ||
            string.IsNullOrWhiteSpace(Measurement))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "A historical fidelity metric is malformed.", field: field));
        }

        if (Status != HistoricalFidelityStageStatus.NotMeasured && SampleCount == 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "A measured historical fidelity stage must contain samples.", field: field));
        if (AcceptanceThresholdMetres is double acceptance &&
            (Status == HistoricalFidelityStageStatus.Passed && MaximumErrorMetres > acceptance + 1e-9 ||
             Status == HistoricalFidelityStageStatus.Failed && MaximumErrorMetres <= acceptance + 1e-9))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "The fidelity stage status disagrees with its recorded maximum and threshold.", field: field));
        }

        return diagnostics;
    }
}

/// <summary>
/// Versioned fidelity schema. Source fitting, voxel quantization, native smoothing and legal
/// packaging remain separate fields so one successful stage cannot hide another failed stage.
/// </summary>
public sealed record HistoricalFidelityReport(
    int SchemaVersion,
    HistoricalHullRepresentation Representation,
    HistoricalFidelityMetric SourceFit,
    HistoricalFidelityMetric VoxelQuantization,
    HistoricalFidelityMetric NativeSmoothingChange,
    HistoricalFallbackValidation? FallbackValidation,
    HistoricalPackagingStatus Packaging,
    int UnsupportedRowCount,
    int AsymmetryNormalizedRowCount,
    string SourceContentHash,
    HistoricalAccuracyLabel RequestedAccuracy)
{
    public const int CurrentSchemaVersion = 2;
    public const double MaximumVoxelAcceptanceMetres = 1;

    public bool GeometryAccepted =>
        SourceFit.Status == HistoricalFidelityStageStatus.Passed &&
        VoxelQuantization.Status == HistoricalFidelityStageStatus.Passed &&
        (NativeSmoothingChange.Status == HistoricalFidelityStageStatus.Passed ||
         Representation == HistoricalHullRepresentation.SampledEnvelopeFallback &&
         FallbackValidation?.Smoothing == SmoothingMethod.None &&
         NativeSmoothingChange.Status == HistoricalFidelityStageStatus.NotMeasured) &&
        UnsupportedRowCount == 0;

    /// <summary>
    /// A famous name becomes usable only when both independent gates pass. Inspired/private
    /// authoring results may still be inspected without satisfying this property.
    /// </summary>
    public bool RuntimeRepresentationSupported =>
        Representation == HistoricalHullRepresentation.SampledEnvelopeFallback &&
        FallbackValidation is not null && !FallbackValidation.Validate().Any(item => item.IsError);

    public bool CanUseNamedPreset => RuntimeRepresentationSupported && GeometryAccepted &&
                                     Packaging.CanPackageDerivedEnvelope;

    public IReadOnlyList<DesignDiagnostic> Validate()
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (SchemaVersion != CurrentSchemaVersion)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                $"Historical fidelity schema {SchemaVersion} is unsupported."));
        if (!Enum.IsDefined(Representation) || UnsupportedRowCount < 0 || AsymmetryNormalizedRowCount < 0 ||
            string.IsNullOrWhiteSpace(SourceContentHash) || !Enum.IsDefined(RequestedAccuracy))
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "Historical fidelity report metadata is malformed."));
        if (SourceFit is null || VoxelQuantization is null || NativeSmoothingChange is null || Packaging is null)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "Historical fidelity report stages are incomplete."));
            return diagnostics;
        }
        diagnostics.AddRange(SourceFit.Validate(nameof(SourceFit)));
        diagnostics.AddRange(VoxelQuantization.Validate(nameof(VoxelQuantization)));
        diagnostics.AddRange(NativeSmoothingChange.Validate(nameof(NativeSmoothingChange)));
        diagnostics.AddRange(Packaging.Validate());
        if (Representation == HistoricalHullRepresentation.SampledEnvelopeFallback)
        {
            if (FallbackValidation is null)
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                    "A sampled fallback report must bind the exact adapter-produced voxel validation."));
            else
                diagnostics.AddRange(FallbackValidation.Validate());
        }
        else if (FallbackValidation is not null)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "Shape V2 fit reports cannot carry sampled-fallback validation."));
        }
        if (SourceFit.Status != HistoricalFidelityStageStatus.NotMeasured &&
            (SourceFit.AcceptanceThresholdMetres is not { } directThreshold || directThreshold > 1))
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                "Direct continuous source-to-candidate acceptance must be measured at a threshold of one metre or less.",
                field: nameof(SourceFit)));
        if (VoxelQuantization.Status != HistoricalFidelityStageStatus.NotMeasured &&
            (VoxelQuantization.AcceptanceThresholdMetres is not { } voxelThreshold ||
             voxelThreshold > MaximumVoxelAcceptanceMetres))
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.FidelityInvalid,
                $"Voxel acceptance must use a finite threshold no greater than " +
                $"{MaximumVoxelAcceptanceMetres:0.###} metre.",
                field: nameof(VoxelQuantization)));
        return diagnostics;
    }
}

/// <summary>Provenance retained with a normalized sampled-envelope derivative.</summary>
public sealed record HistoricalEnvelopeMetadata(
    string AssetId,
    string AssetVersion,
    string SourceContentHash,
    string SourceTitle,
    string Designation,
    string EraOrRefit,
    HistoricalConstructionStatus ConstructionStatus,
    HistoricalSourceTransform SourceTransform,
    double WaterlineMetres,
    double DeckDatumMetres,
    double KeelDatumMetres)
{
    public IReadOnlyList<DesignDiagnostic> Validate()
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (new[] { AssetId, AssetVersion, SourceContentHash, SourceTitle, Designation,
                EraOrRefit }.Any(string.IsNullOrWhiteSpace) || SourceTransform is null ||
            !SourceTransform.IsValid ||
            !Enum.IsDefined(ConstructionStatus) ||
            !double.IsFinite(WaterlineMetres) || !double.IsFinite(DeckDatumMetres) ||
            !double.IsFinite(KeelDatumMetres) || DeckDatumMetres <= KeelDatumMetres ||
            WaterlineMetres < KeelDatumMetres || WaterlineMetres > DeckDatumMetres)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.MetadataInvalid,
                "Historical envelope metadata is incomplete or has invalid datums."));
        }
        return diagnostics;
    }
}

/// <summary>The exact numeric transform applied to source vertices before sampling.</summary>
public sealed record HistoricalSourceTransform(
    double MetresPerSourceUnit,
    ObjAxisMapping AxisMapping)
{
    public bool IsValid => double.IsFinite(MetresPerSourceUnit) && MetresPerSourceUnit > 0 &&
                           MetresPerSourceUnit <= 100_000 && AxisMapping is { IsValid: true };
}

/// <summary>Stable H02 diagnostic codes. Add codes; never repurpose them.</summary>
public static class HistoricalDiagnosticCodes
{
    public const string InputLimitExceeded = "HIS001";
    public const string ObjSyntaxInvalid = "HIS002";
    public const string UnitsOrAxesInvalid = "HIS003";
    public const string HullGroupInvalid = "HIS004";
    public const string TopologyOpen = "HIS005";
    public const string TopologyNonManifold = "HIS006";
    public const string WindingInvalid = "HIS007";
    public const string TopologyDisconnected = "HIS008";
    public const string UnsupportedRowIntervals = "HIS009";
    public const string SymmetryRejected = "HIS010";
    public const string FitBudgetExceeded = "HIS011";
    public const string PackagingEvidenceInvalid = "HIS012";
    public const string FidelityInvalid = "HIS013";
    public const string MetadataInvalid = "HIS014";
    public const string AssetLoadInvalid = "HIS015";
}
