using System.Collections.Immutable;
using FtdHullGenerator.Domain.Decorations;
using FtdHullGenerator.Domain.Design;

namespace FtdHullGenerator.Domain.Projects;

/// <summary>Explicit final-anchor intent. Legacy run limits are never converted into this recipe.</summary>
public sealed record ExplicitSlopeRefinement(string RecipeId, int RecipeVersion,
    ImmutableArray<SlopeExtensionRequest> Requests)
{
    public IEnumerable<DesignDiagnostic> Validate()
    {
        if (RecipeId != NativeSlopeExtensionRule.RecipeId || RecipeVersion != NativeSlopeExtensionRule.RecipeVersion)
            yield return DesignDiagnostic.Error(SlopeExtensionDiagnosticCodes.UnsupportedRecipe, "Unsupported explicit slope extension recipe/version.");
        if (Requests.IsDefault || Requests.Length > 6241)
        {
            yield return DesignDiagnostic.Error(SlopeExtensionDiagnosticCodes.PayloadLimit, "Explicit slope selection must contain at most 6241 requests.");
            yield break;
        }
        foreach (var request in Requests)
            if (request is null || request.VisualLengthMetres is < 1 or > 40)
                yield return DesignDiagnostic.Error(SlopeExtensionDiagnosticCodes.UnsupportedLength, "Explicit visual length must be 1–40 m; 1–4 m leaves the native host unchanged.");
    }
}
