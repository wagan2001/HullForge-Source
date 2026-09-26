using System.Collections.Immutable;
using System.Text.Json;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Layout;
using FtdHullGenerator.Geometry.Layout;

/// <summary>
/// Solver expectations. The four-turret case is read from the plan's own fixture file and compared
/// against that file's hand-authored <c>expected</c> block, so the oracle is not derived from the
/// solver under test. The remaining cases are written out by hand.
/// </summary>
internal static class ArrangementSolverTests
{
    public static void Run()
    {
        var fixture = LoadFixture();
        VerifyFixtureArithmetic(fixture);
        VerifyLinkedGapEdit(fixture);
        VerifyCenterPitchDiffersFromEdgeGap();
        VerifyCenterPitchLengthAccounting();
        VerifySternDatumPlacement();
        VerifyFlexibleRemainderEdges();
        VerifyOverconstraintAndRemainder();
        VerifyParity();
        VerifyResizePolicies();
        VerifyInvalidChainIsReported();

        Console.WriteLine(
            "Arrangement solver: plan fixture intervals and world-Z centres, one linked-gap edit, " +
            "centre pitch versus edge gap, overconstraint, flexible remainder, parity, resize policy " +
            "and invalid-chain handling passed.");
    }

    private static void VerifyFixtureArithmetic(Fixture fixture)
    {
        var solution = ArrangementSolver.Solve(fixture.Arrangement, fixture.Options);
        Require(solution.IsSolved,
            $"The four-turret fixture must solve. {solution.Diagnostics.Summary()} " +
            $"{string.Join(" | ", solution.Diagnostics.Select(d => d.ToString()))}");
        Require(solution.Nodes.Count == 5, $"The fixture has five nodes, not {solution.Nodes.Count}.");

        foreach (var (nodeId, expected) in fixture.ExpectedIntervals)
        {
            var node = solution.FindNode(nodeId);
            Require(node is not null, $"The solution is missing node '{nodeId}'.");
            Require(node!.RulerSpan.Start.Metres == expected.Start,
                $"Node '{nodeId}' must start at {expected.Start} m, not {node.RulerSpan.Start.Metres}.");
            Require(node.RulerSpan.End.Metres == expected.End,
                $"Node '{nodeId}' must end at {expected.End} m, not {node.RulerSpan.End.Metres}.");
        }

        foreach (var (nodeId, expectedZ) in fixture.ExpectedWorldZCenters)
        {
            var node = solution.FindNode(nodeId);
            Require(node is not null, $"The solution is missing node '{nodeId}'.");
            Require(node!.WorldZCenter.Metres == expectedZ,
                $"Node '{nodeId}' must centre at world Z {expectedZ}, not {node.WorldZCenter.Metres}.");
        }

        // The linked 5 m gun-pair gap drives both ends, and the 12 m structure clearance both sides.
        var pairedGaps = solution.Gaps
            .Where(gap => gap.NamedGapId == "gun-pair-gap")
            .ToArray();
        Require(pairedGaps.Length == 2, "Both gun-pair segments must reference the shared variable.");
        Require(pairedGaps.All(gap => gap.Value.Metres == 5 && gap.RealizedClearGap.Metres == 5),
            "Both gun-pair segments must realize an exact 5 m clear gap.");
        Require(solution.Gaps.Where(gap => gap.NamedGapId == "structure-clearance")
                .All(gap => gap.RealizedClearGap.Metres == 12),
            "Both structure clearances must realize an exact 12 m clear gap.");

        var superstructure = solution.FindNode("superstructure")!;
        Require(superstructure.Handles.Count == 2, "The superstructure span owns two child handles.");
        var structureRoot = superstructure.Handles.Single(handle => handle.HandleId == "structure-root");
        var towerRoot = superstructure.Handles.Single(handle => handle.HandleId == "tower-root");
        Require(structureRoot.RulerPosition.Metres == fixture.ExpectedStructureRootRuler,
            $"The structure root must sit at ruler {fixture.ExpectedStructureRootRuler}.");
        Require(towerRoot.RulerPosition.Metres == fixture.ExpectedTowerRootRuler,
            $"The tower root must sit at ruler {fixture.ExpectedTowerRootRuler}.");
        Require(solution.RequiredRulerLength.Metres == 126,
            $"The chain must require 126 m of ruler, not {solution.RequiredRulerLength.Metres}.");
        Require(solution.UnusedRulerLength.Metres == fixture.ExpectedAftUnusedSpace,
            $"The fixture leaves {fixture.ExpectedAftUnusedSpace} m of unused ruler.");
        Require(superstructure.Handles.Count == 2 && solution.Nodes.Count == 5,
            "Child handles must not be counted as extra chain intervals.");
    }

    private static void VerifyLinkedGapEdit(Fixture fixture)
    {
        var widened = fixture.Arrangement with
        {
            NamedGaps = fixture.Arrangement.NamedGaps
                .Select(gap => gap.Id == "gun-pair-gap" ? gap with { Value = DesignMeasure.FromMetres(7) } : gap)
                .ToImmutableArray(),
        };
        var solution = ArrangementSolver.Solve(widened, fixture.Options);
        Require(solution.IsSolved, "The widened chain must still solve; the hull has spare ruler.");
        Require(solution.FindNode("B")!.RulerSpan.Start.Metres == 30,
            "Widening the shared gap by 2 m must move B's forward edge from 28 m to 30 m.");
        Require(solution.FindNode("Y")!.RulerSpan.End.Metres == 130,
            "Widening the shared gap by 2 m must move Y's aft edge from 126 m to 130 m.");
        Require(solution.UnusedRulerLength.Metres == 30,
            "The widened chain must leave 30 m of unused ruler, not " +
            $"{solution.UnusedRulerLength.Metres}.");
        // Everything aft of B shifts by the same 2 m, but the clearance *value* is untouched.
        Require(solution.FindNode("X")!.RulerSpan.Start.Metres == 97,
            "The downstream span must shift by exactly the linked gap delta.");
        Require(solution.Gaps.Where(gap => gap.NamedGapId == "structure-clearance")
                .All(gap => gap.RealizedClearGap.Metres == 12),
            "A linked-gap edit must not disturb the other named gap.");
    }

    private static void VerifyCenterPitchDiffersFromEdgeGap()
    {
        // Two 8 m rings 5 m apart centre-to-centre: a centre pitch of 5 is NOT a 5 m clear gap.
        var nodes = new[]
        {
            ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromMetres(4)),
            ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromMetres(4)),
        };
        var pitch = new Arrangement([.. nodes],
            [new ArrangementGap("pitch", ArrangementMeasure.CenterPitch, DesignMeasure.FromMetres(9))],
            [], DesignMeasure.FromMetres(2), DesignMeasure.FromMetres(2),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        var solution = ArrangementSolver.Solve(pitch, ArrangementSolveOptions.At(DesignMeasure.FromMetres(100)));
        Require(solution.IsSolved, "The centre-pitch chain must solve.");
        Require(solution.Nodes[1].RulerCenter.Metres - solution.Nodes[0].RulerCenter.Metres == 9,
            "A centre pitch of 9 m must place the second centre exactly 9 m behind the first.");
        Require(solution.Gaps[0].RealizedClearGap.Metres == 1,
            "An 8 m ring pair at 9 m centre pitch leaves a 1 m clear gap, not " +
            $"{solution.Gaps[0].RealizedClearGap.Metres}.");

        // The same 9 m as a clear edge gap leaves 9 m of water between the rings.
        var edge = pitch with
        {
            Gaps = [new ArrangementGap("pitch", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(9))],
        };
        var edgeSolution = ArrangementSolver.Solve(edge, ArrangementSolveOptions.At(DesignMeasure.FromMetres(100)));
        Require(edgeSolution.Gaps[0].RealizedClearGap.Metres == 9,
            "A 9 m clear edge gap must leave 9 m between the rings.");
        Require(edgeSolution.Nodes[1].RulerCenter.Metres - edgeSolution.Nodes[0].RulerCenter.Metres == 17,
            "A 9 m clear edge gap between 8 m rings is a 17 m centre distance.");
    }

    /// <summary>
    /// A centre pitch is a centre-to-centre distance, so the ruler length it consumes is
    /// <c>pitch - a - b</c>. Summing the declared pitch instead overstates the chain by a+b and
    /// misplaces every stern-datum chain, which is the defect an independent review found.
    /// </summary>
    private static void VerifyCenterPitchLengthAccounting()
    {
        var nodes = new[]
        {
            ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromMetres(4)),
            ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromMetres(6)),
        };
        // Unequal neighbours: an 8 m ring and a 12 m ring, 20 m centre to centre.
        var arrangement = new Arrangement([.. nodes],
            [new ArrangementGap("pitch", ArrangementMeasure.CenterPitch, DesignMeasure.FromMetres(20))],
            [], DesignMeasure.Zero, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);

        var solution = ArrangementSolver.Solve(arrangement,
            ArrangementSolveOptions.At(DesignMeasure.FromMetres(100)) with
            {
                SupportedRulerEnd = DesignMeasure.FromMetres(30),
            });
        Require(solution.IsSolved, $"A chain needing exactly 30 m must fit in 30 m. {solution.Diagnostics.Summary()}");
        Require(solution.RequiredRulerLength.Metres == 30,
            $"The chain must consume 20 - 4 - 6 = 10 m of ruler between the rings plus their 20 m of body, " +
            $"which is 30 m, not {solution.RequiredRulerLength.Metres} m.");
        Require(solution.UnusedRulerLength.Metres == 0, "A 30 m chain in a 30 m envelope leaves nothing.");
        Require(solution.FindNode("a")!.RulerSpan.Start.Metres == 0 &&
                solution.FindNode("a")!.RulerSpan.End.Metres == 8,
            "The 8 m ring must occupy [0, 8].");
        Require(solution.FindNode("b")!.RulerSpan.Start.Metres == 18 &&
                solution.FindNode("b")!.RulerSpan.End.Metres == 30,
            "The 12 m ring must occupy [18, 30] after a 10 m realized edge distance.");
        Require(solution.Gaps[0].RealizedClearGap.Metres == 10,
            $"A 20 m centre pitch between 4 m and 6 m half-extents leaves 10 m clear, not " +
            $"{solution.Gaps[0].RealizedClearGap.Metres} m.");

        var tooTight = ArrangementSolver.Solve(arrangement,
            ArrangementSolveOptions.At(DesignMeasure.FromMetres(100)) with
            {
                SupportedRulerEnd = DesignMeasure.FromMetres(29),
            });
        Require(tooTight.Status == ArrangementSolveStatus.DoesNotFit,
            "One metre short must not be reported as solved.");
        Require(tooTight.ExcessRulerLength.Metres == 1,
            $"The excess must be 1 m, not {tooTight.ExcessRulerLength.Metres} m.");

        var overlapping = ArrangementSolver.Solve(arrangement with
        {
            Gaps = [new ArrangementGap("pitch", ArrangementMeasure.CenterPitch, DesignMeasure.FromMetres(9))],
        }, ArrangementSolveOptions.At(DesignMeasure.FromMetres(100)));
        Require(overlapping.Status == ArrangementSolveStatus.DoesNotFit,
            "A centre pitch smaller than the two half-extents must not be reported as solved.");
        Require(overlapping.Diagnostics.Any(d => d.Code == ArrangementDiagnosticCodes.CenterPitchTooSmall),
            "Overlapping rings must carry the centre-pitch diagnostic.");
    }

    /// <summary>A stern-datum chain is positioned by the ruler it actually consumes.</summary>
    private static void VerifySternDatumPlacement()
    {
        var nodes = new[]
        {
            ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromMetres(4)),
            ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromMetres(4)),
        };
        var arrangement = new Arrangement([.. nodes],
            [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(5))],
            [], DesignMeasure.Zero, DesignMeasure.FromMetres(2),
            ArrangementAnchorKind.SternDatum, ArrangementResizePolicy.PreserveAbsolute, null);

        var solution = ArrangementSolver.Solve(arrangement,
            ArrangementSolveOptions.At(DesignMeasure.FromMetres(0)) with
            {
                SupportedRulerEnd = DesignMeasure.FromMetres(40),
            });
        Require(solution.IsSolved,
            $"A 23 m chain must fit in a 40 m envelope. {solution.Diagnostics.Summary()}");
        Require(solution.FindNode("a")!.RulerSpan.Start.Metres == 17,
            $"A stern-datum chain must start at 40 - 2 - 21 = 17, not " +
            $"{solution.FindNode("a")!.RulerSpan.Start.Metres} m.");
        Require(solution.FindNode("b")!.RulerSpan.End.Metres == 38,
            "The aft ring must end 2 m before the supported end, which is the stern margin.");

        var tooShort = ArrangementSolver.Solve(arrangement,
            ArrangementSolveOptions.At(DesignMeasure.FromMetres(0)) with
            {
                SupportedRulerEnd = DesignMeasure.FromMetres(10),
            });
        Require(tooShort.Diagnostics.Any(d => d.Code == ArrangementDiagnosticCodes.ChainStartsBeforeOrigin),
            "A stern-datum chain that would start before the origin must be reported.");
    }

    private static void VerifyFlexibleRemainderEdges()
    {
        var nodes = new[]
        {
            ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromMetres(4)),
            ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromMetres(4)),
        };
        var flexible = new Arrangement([.. nodes],
            [new ArrangementGap("flex", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(3))],
            [], DesignMeasure.FromMetres(2), DesignMeasure.FromMetres(2),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, "flex");

        // Without a supported end there is no remainder to solve; the declared value must stand.
        var declared = ArrangementSolver.Solve(flexible, ArrangementSolveOptions.At(DesignMeasure.FromMetres(0)));
        Require(declared.IsSolved, "A flexible chain with no supported end must still solve from its declared value.");
        Require(declared.Gaps[0].Value.Metres == 3 && declared.RequiredRulerLength.Metres == 23,
            $"The declared 3 m gap must stand, giving 23 m, not {declared.RequiredRulerLength.Metres} m.");
        Require(declared.Gaps[0].IsFlexible, "The flexible segment must be flagged even when it is not solved.");

        // A supported end turns the same segment into the remainder: 40 - 2 - 2 - 16 = 20 m.
        var remainder = ArrangementSolver.Solve(flexible, ArrangementSolveOptions.At(DesignMeasure.FromMetres(0)) with
        {
            SupportedRulerEnd = DesignMeasure.FromMetres(40),
        });
        Require(remainder.IsSolved, $"The remainder must solve with 20 m of slack. {remainder.Diagnostics.Summary()}");
        Require(remainder.Gaps[0].Value.Metres == 20,
            $"The flexible gap must absorb the slack and become 20 m, not {remainder.Gaps[0].Value.Metres} m.");
        Require(remainder.FindNode("b")!.RulerSpan.End.Metres == 38,
            "The chain must end two metres before the supported end.");

        // No slack at all leaves a negative remainder, which must be diagnosed and marked unfitted.
        var overfull = ArrangementSolver.Solve(flexible, ArrangementSolveOptions.At(DesignMeasure.FromMetres(0)) with
        {
            SupportedRulerEnd = DesignMeasure.FromMetres(16),
        });
        Require(overfull.Status == ArrangementSolveStatus.DoesNotFit,
            "A chain with no remainder must not be reported as solved.");
        Require(overfull.Diagnostics.Any(d => d.Code == ArrangementDiagnosticCodes.FlexibleRemainderNegative),
            "The negative remainder must carry its own diagnostic.");
    }

    private static void VerifyOverconstraintAndRemainder()
    {
        var fixture = LoadFixture();
        var shortSupport = fixture.Options with { SupportedRulerEnd = DesignMeasure.FromMetres(120) };
        var solution = ArrangementSolver.Solve(fixture.Arrangement, shortSupport);
        Require(solution.Status == ArrangementSolveStatus.DoesNotFit,
            "A 126 m chain in a 120 m support envelope must not be reported as solved.");
        Require(solution.ExcessRulerLength.Metres == 6, "The chain must report exactly 6 m of excess.");
        Require(solution.Diagnostics.Any(d => d.Code == ArrangementDiagnosticCodes.ChainExceedsSupportedLength),
            "The overrun must carry a stable diagnostic code.");
        Require(solution.Nodes.Count == 5,
            "A chain that does not fit must still report the requested intent so it stays editable.");
        Require(solution.Diagnostics.Any(d => d.SuggestedCorrection is not null),
            "The overrun must suggest a correction.");
        Require(solution.Diagnostics.Any(d => d.AffectedBounds is not null),
            "The overrun must name the affected bounds so a caller can highlight the offender.");

        // A single flexible remainder gap absorbs what is left of the supported interval.
        var flexible = fixture.Arrangement with { FlexibleGapId = "gap-2", Gaps =
            [
                new ArrangementGap("gap-1", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, "gun-pair-gap"),
                new ArrangementGap("gap-2", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(12)),
                new ArrangementGap("gap-3", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, "structure-clearance"),
                new ArrangementGap("gap-4", ArrangementMeasure.ClearEdgeGap, DesignMeasure.Zero, "gun-pair-gap"),
            ] };
        var flexibleSolution = ArrangementSolver.Solve(flexible, fixture.Options with
        {
            SupportedRulerEnd = DesignMeasure.FromMetres(150),
        });
        Require(flexibleSolution.IsSolved, "The flexible remainder must absorb 24 extra metres.");
        var flexibleGap = flexibleSolution.Gaps.Single(gap => gap.GapId == "gap-2");
        Require(flexibleGap.IsFlexible, "The solver must report which segment is the flexible remainder.");
        Require(flexibleGap.Value.Metres == 36,
            $"The flexible segment must resolve to 36 m, not {flexibleGap.Value.Metres}.");
    }

    private static void VerifyParity()
    {
        var nodes = new[]
        {
            ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromTwiceMetres(13)),
            ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromTwiceMetres(13)),
        };
        var arrangement = new Arrangement([.. nodes],
            [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(5))],
            [], DesignMeasure.FromMetres(10), DesignMeasure.FromMetres(10),
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);

        var aligned = ArrangementSolver.Solve(arrangement, ArrangementSolveOptions.At(DesignMeasure.FromMetres(120)));
        Require(aligned.IsSolved, "The 13 m ring fixture must solve without a parity requirement.");
        Require(!aligned.FindNode("a")!.RulerCenter.IsWholeMetre,
            "A 13 m ring starting at 10 m centres on a half metre; that is the parity hazard.");

        var strict = ArrangementSolver.Solve(arrangement,
            ArrangementSolveOptions.At(DesignMeasure.FromMetres(120)) with
            {
                Parity = ArrangementParityRequirement.WholeMetreCenters,
            });
        Require(strict.Status == ArrangementSolveStatus.DoesNotFit,
            "A half-metre centre must be reported when whole-metre centres are required.");
        Require(strict.Diagnostics.Any(d => d.Code == DesignDiagnosticCodes.ArrangementCenterlineUnrepresentable),
            "The parity conflict must carry a stable diagnostic code.");

        // Moving the bow margin by half a metre resolves it without touching the rings or the gap.
        var resolved = ArrangementSolver.Solve(arrangement with { BowMargin = DesignMeasure.FromTwiceMetres(21) },
            ArrangementSolveOptions.At(DesignMeasure.FromMetres(120)) with
            {
                Parity = ArrangementParityRequirement.WholeMetreCenters,
            });
        Require(resolved.IsSolved, "A half-metre margin must put both centres back on whole metres.");
        Require(resolved.FindNode("a")!.RulerCenter.Metres == 17,
            "The corrected first centre must sit at 17 m.");
        Require(resolved.Gaps[0].RealizedClearGap.Metres == 5,
            "Resolving parity must not change the requested clear gap.");
    }

    private static void VerifyResizePolicies()
    {
        var fixture = LoadFixture();
        var preserved = ArrangementResize.Apply(fixture.Arrangement,
            DesignMeasure.FromMetres(160), DesignMeasure.FromMetres(240),
            ArrangementResizePolicy.PreserveAbsolute);
        Require(preserved == fixture.Arrangement,
            "The default policy must keep every absolute value and only re-evaluate the anchor.");
        var preservedSolution = ArrangementSolver.Solve(preserved, fixture.Options with
        {
            SupportedRulerEnd = DesignMeasure.FromMetres(240),
        });
        Require(preservedSolution.IsSolved && preservedSolution.RequiredRulerLength.Metres == 126,
            "A longer hull must not stretch the ship's components under the default policy.");
        Require(preservedSolution.UnusedRulerLength.Metres == 114,
            "The longer hull must report the extra 114 m of unused ruler.");

        var withSternMargin = fixture.Arrangement with { SternMargin = DesignMeasure.FromMetres(10) };
        var scaled = ArrangementResize.Apply(withSternMargin,
            DesignMeasure.FromMetres(160), DesignMeasure.FromMetres(240),
            ArrangementResizePolicy.ScaleWithLength);
        Require(scaled.BowMargin.Metres == 15, "A 1.5x scale must move the 10 m bow margin to 15 m.");
        Require(scaled.SternMargin.Metres == 15, "A 1.5x scale must move the 10 m stern margin to 15 m.");
        var named = scaled.NamedGaps.Single(gap => gap.Id == "gun-pair-gap");
        Require(named.Value.Metres == 5, "A named construction rule must not be rescaled silently.");
    }

    private static void VerifyInvalidChainIsReported()
    {
        var broken = new Arrangement(
            [
                ArrangementNode.Create("a", ArrangementNodeKind.Barbette, "ba", DesignMeasure.FromMetres(4)),
                ArrangementNode.Create("b", ArrangementNodeKind.Barbette, "bb", DesignMeasure.FromMetres(4)),
            ],
            [], [], DesignMeasure.Zero, DesignMeasure.Zero,
            ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null);
        var solution = ArrangementSolver.Solve(broken, ArrangementSolveOptions.At(DesignMeasure.FromMetres(50)));
        Require(solution.Status == ArrangementSolveStatus.Invalid,
            "A chain with the wrong number of gaps must be reported as invalid, not solved.");
        Require(solution.Nodes.Count == 0, "An invalid chain must not report intervals as if they were real.");

        var sternDatum = broken with
        {
            Gaps = [new ArrangementGap("gap", ArrangementMeasure.ClearEdgeGap, DesignMeasure.FromMetres(5))],
            Anchor = ArrangementAnchorKind.SternDatum,
        };
        var withoutSupport = ArrangementSolver.Solve(sternDatum, ArrangementSolveOptions.At(DesignMeasure.FromMetres(50)));
        Require(withoutSupport.Diagnostics.Any(d =>
                d.Code == ArrangementDiagnosticCodes.SternDatumNeedsSupportedEnd),
            "A stern-datum chain without a supported end must be reported, not guessed.");
    }

    private static Fixture LoadFixture()
    {
        var path = FindRepositoryFile(Path.Combine("FtdHullGenerator.SelfTest", "Fixtures",
            "four-turret-arrangement.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var coordinates = root.GetProperty("coordinates");
        var layoutBowZ = DesignMeasure.FromMetres(coordinates.GetProperty("layoutBowZ").GetDouble());
        var supported = root.GetProperty("supportedRulerInterval");
        var supportedEnd = DesignMeasure.FromMetres(supported[1].GetDouble());

        var gaps = new List<ArrangementGap>();
        var gapIndex = 0;
        foreach (var gap in root.GetProperty("gaps").EnumerateArray())
            gaps.Add(new ArrangementGap($"gap-{++gapIndex}", ArrangementMeasure.ClearEdgeGap,
                DesignMeasure.Zero, gap.GetProperty("parameter").GetString()));

        var namedGaps = root.GetProperty("namedGaps").EnumerateObject()
            .Select(property => new NamedArrangementGap(property.Name, property.Name,
                property.Value.GetProperty("kind").GetString() == "CenterPitch"
                    ? ArrangementMeasure.CenterPitch
                    : ArrangementMeasure.ClearEdgeGap,
                DesignMeasure.FromMetres(property.Value.GetProperty("value").GetDouble())))
            .ToArray();

        var nodes = new List<ArrangementNode>();
        var expectedIntervals = new Dictionary<string, (double Start, double End)>(StringComparer.Ordinal);
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            var id = node.GetProperty("id").GetString()!;
            var footprint = node.GetProperty("realizedLongitudinalFootprint").GetDouble();
            var handles = new List<ArrangementHandle>();
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                {
                    var offsetFromForwardEdge = child.GetProperty("localRulerOffsetFromForwardEdge").GetDouble();
                    handles.Add(new ArrangementHandle(child.GetProperty("id").GetString()!, id,
                        DesignMeasure.FromMetres(offsetFromForwardEdge - footprint / 2)));
                }

            nodes.Add(ArrangementNode.Create(id,
                node.GetProperty("kind").GetString() == "barbette"
                    ? ArrangementNodeKind.Barbette
                    : ArrangementNodeKind.SuperstructureSpan,
                id, DesignMeasure.FromMetres(footprint / 2), [.. handles]));
        }

        var expected = root.GetProperty("expected");
        foreach (var interval in expected.GetProperty("intervals").EnumerateObject())
            expectedIntervals[interval.Name] = (interval.Value[0].GetDouble(), interval.Value[1].GetDouble());
        var expectedCenters = expected.GetProperty("barbetteWorldZCenters").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal);

        return new Fixture(
            new Arrangement([.. nodes], [.. gaps], [.. namedGaps], DesignMeasure.FromMetres(10), DesignMeasure.Zero,
                ArrangementAnchorKind.BowDatum, ArrangementResizePolicy.PreserveAbsolute, null),
            ArrangementSolveOptions.At(layoutBowZ) with { SupportedRulerEnd = supportedEnd },
            expectedIntervals,
            expectedCenters,
            expected.GetProperty("structureRootRulerPosition").GetDouble(),
            expected.GetProperty("towerRootRulerPosition").GetDouble(),
            expected.GetProperty("aftUnusedSpace").GetDouble());
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            if (File.Exists(Path.Combine(directory.FullName, "FtdHullGenerator.sln")))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate '{relativePath}' from the test output directory.");
    }

    private sealed record Fixture(
        Arrangement Arrangement,
        ArrangementSolveOptions Options,
        IReadOnlyDictionary<string, (double Start, double End)> ExpectedIntervals,
        IReadOnlyDictionary<string, double> ExpectedWorldZCenters,
        double ExpectedStructureRootRuler,
        double ExpectedTowerRootRuler,
        double ExpectedAftUnusedSpace);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
