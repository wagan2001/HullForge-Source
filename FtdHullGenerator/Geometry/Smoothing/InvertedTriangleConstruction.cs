using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>
/// Fits complete, geometrically closed side assemblies before armor extraction.
/// Horizontal runs and compound diagonal bands compete in the same bounded search;
/// neither the previous shell classifier nor the rejected corner/cube refit is used.
/// </summary>
/// <remarks>
/// Status: work in progress. The editor disables this method for every access tier pending its
/// own in-game review, and the exporter refuses to write a hull whose fitted parts are missing
/// from the installed catalog rather than substituting cubes. Code existing here is not evidence
/// that the construction is correct in game.
/// Contract: at most one metre of local side-contour change per row, protected deck, keel,
/// maximum-width, chine and bottom landmarks left untouched, and only complete assemblies from
/// <see cref="SurfaceAssemblyLibrary" /> installed. Changes to corner construction or
/// protected landmarks require independent native geometry review.
/// </remarks>
public static class InvertedTriangleConstruction
{
    public const int BeamWidth = 128;
    public const int MaximumRegionExpansions = 50_000;

    public static SurfaceConstructionPlan Plan(HullSurfaceGrid grid, HullParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.HullArmor.Layers[0].Construction != ArmorConstruction.Solid)
            return new(grid, [], ["Inverted construction leaves specialized outer-layer construction unchanged."]);

        var candidates = new List<Candidate>();
        var sequence = 0;
        for (var z = grid.MinZ + 1; z < grid.MaxZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var y = grid.MinY + 1; y < grid.MaxY; y++)
            {
                var span = grid.SpanAt(y, z);
                if (span.IsEmpty || grid.IsProtected(y, z)) continue;
                // An unedited outer cell permits an inward cut of at most 1m.
                // An added cell permits an outward fill of at most 1m. Moving a
                // span inward AND clipping that new cell would allow 2m, so is forbidden.
                for (var x = span.MaxX; x <= Math.Min(grid.MaxX, span.MaxX + 1); x++)
                {
                    if (2 * x <= grid.MirrorSum + 1) continue;
                    foreach (var pattern in SurfaceAssemblyLibrary.Patterns)
                    {
                        // Long constant-section bands use the native four-metre
                        // packing phase. Short patterns still cover every cell at
                        // ends; redundant sliding long bands consume no search time.
                        if (pattern.IsContinuousBeamBand)
                        {
                            var forward = BlockRotations.GetRotationAxes(pattern.Placements[0].Rotation).Forward;
                            var phase = forward.Y != 0 ? y - grid.MinY : z - grid.MinZ;
                            if ((phase & 3) != 0) continue;
                        }
                        var candidate = TryCandidate(grid, pattern, x, y, z, sequence++);
                        if (candidate is not null) candidates.Add(candidate);
                    }
                }
            }
        }

        var selected = new List<Candidate>();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var support = new HashSet<(int X, int Y, int Z)>();
        var diagnostics = new List<string>();
        // Small spatial windows bound both memory and search time. Every candidate
        // already has a complete cap collar, so window boundaries cannot truncate a run.
        foreach (var window in candidates.GroupBy(candidate =>
                     ((candidate.Z - grid.MinZ) / 4, (candidate.Y - grid.MinY) / 4))
                 .OrderBy(group => group.Key.Item1).ThenBy(group => group.Key.Item2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = window.Where(candidate => Fits(candidate, claimed, support))
                .OrderByDescending(candidate => candidate.Improvement)
                .ThenBy(candidate => candidate.Deviation).ThenBy(candidate => candidate.Id).ToArray();
            var best = Search(available, cancellationToken, out var exhausted);
            if (exhausted)
            {
                diagnostics.Add($"Construction window {window.Key.Item1},{window.Key.Item2} retained its original surface at the search limit.");
                continue;
            }
            foreach (var candidate in best)
            {
                selected.Add(candidate);
                claimed.UnionWith(candidate.Cells);
                support.UnionWith(candidate.Guard);
            }
        }

        var regions = selected.OrderBy(candidate => candidate.Z).ThenBy(candidate => candidate.Y)
            .ThenBy(candidate => candidate.X).ThenBy(candidate => candidate.Id)
            .Select((candidate, id) => MakeRegion(grid, candidate, id)).ToArray();
        if (regions.Length == 0)
            diagnostics.Add("No complete supported hybrid assemblies improved this contour; its original construction was retained.");
        else
            diagnostics.Add($"Reserved {regions.Length} complete mirrored hybrid assemblies; unresolved surfaces retain their original contour.");
        return new(grid, regions, diagnostics);
    }

    private static Candidate? TryCandidate(HullSurfaceGrid grid, SurfaceAssemblyPattern pattern,
        int x, int y, int z, int id)
    {
        // Most templates cannot fit a particular sampled row. Reject them before
        // allocating footprints, edits, guards, or search state.
        foreach (var relative in pattern.Footprint)
        {
            var xx = x + relative.X;
            var yy = y + relative.Y;
            var zz = z + relative.Z;
            var original = grid.SpanAt(yy, zz);
            if (original.IsEmpty || grid.IsProtected(yy, zz) || xx < original.MaxX ||
                xx > original.MaxX + 1 || xx > grid.MaxX || 2 * xx <= grid.MirrorSum + 1)
                return null;
            // Preserve the complete inner wall of every original surface cell.
            // In particular an inward tetrahedral cut must not expose the cavity
            // through the missing half of its back face. Outward triangles can
            // sit over original-edge inverses whose inward faces remain full.
            if (xx == original.MaxX)
            {
                // The inner membrane turns vertically and longitudinally too:
                // a bulb/flare can have cavity above an outer source cell. A
                // partial top/back face is unsafe even when its inward-X face is full.
                var required = grid.CavityFaceMask(yy, zz);
                if ((pattern.FullFaceMasks[relative] & required) != required) return null;
            }
        }
        var cells = new (int X, int Y, int Z)[pattern.Footprint.Count];
        var edits = new List<SurfaceSpanEdit>();
        var edited = new Dictionary<(int Y, int Z), int>();
        var outwardCells = 0;
        for (var index = 0; index < cells.Length; index++)
        {
            var relative = pattern.Footprint[index];
            var cell = (X: x + relative.X, Y: y + relative.Y, Z: z + relative.Z);
            var original = grid.SpanAt(cell.Y, cell.Z);
            if (original.IsEmpty || grid.IsProtected(cell.Y, cell.Z) ||
                cell.X < original.MaxX || cell.X > original.MaxX + 1 || cell.X > grid.MaxX)
                return null;
            cells[index] = cell;
            if (cell.X != original.MaxX)
            {
                outwardCells++;
                edited[(cell.Y, cell.Z)] = cell.X;
                edits.Add(new(cell.Y, cell.Z, original, new(grid.MirrorSum - cell.X, cell.X)));
            }
        }
        // Never change a zero-height gap or narrow a flat bottom. Analytic chine and
        // shoulder pins arrive from the sampler; these local tests additionally
        // forbid new turning points between the pins.
        foreach (var edit in edits)
        foreach (var (dy, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var oldNeighbour = grid.SpanAt(edit.Y + dy, edit.Z + dz);
            if (oldNeighbour.IsEmpty) return null;
            var next = edited.GetValueOrDefault((edit.Y + dy, edit.Z + dz), oldNeighbour.MaxX);
            var before = edit.Original.MaxX - oldNeighbour.MaxX;
            var after = edit.Revised.MaxX - next;
            if (before > 0 && after < 0 || before < 0 && after > 0) return null;
        }

        var backing = new List<(int X, int Y, int Z)>();
        for (var index = 0; index < pattern.RequiredBacking.Count; index++)
        {
            var relative = pattern.RequiredBacking[index];
            var cell = (X: x + relative.X, Y: y + relative.Y, Z: z + relative.Z);
            // A triangular boundary can be genuinely exterior at the turn of
            // the bilge. Its exposed area is charged by the patch metric. Only
            // actual exterior solid cubes become required structural caps.
            if (grid.IsExterior(cell))
                backing.Add(cell);
        }
        // Raising the voxel contour must not make armor extraction discard the
        // original wall behind a new tetrahedral/partial skin cell. Retain that
        // original edge as real backing unless another fitted part in this same
        // complete panel replaces it with the full inward face checked above.
        foreach (var edit in edits)
        {
            var originalEdge = (X: edit.Original.MaxX, edit.Y, edit.Z);
            if (!pattern.FootprintSet.Contains((originalEdge.X - x, originalEdge.Y - y, originalEdge.Z - z)))
                backing.Add(originalEdge);
        }
        if (backing.Count == 0) return null;

        // The compound normal must follow a real contour change in each of its
        // active directions. A locally flat wall must not acquire diagonal dents.
        foreach (var normal in pattern.SurfaceNormals)
        {
            if (Math.Abs(normal.Y) > .001f && !HasGradient(grid, cells, 1, 0, -Math.Sign(normal.Y))) return null;
            if (Math.Abs(normal.Z) > .001f && !HasGradient(grid, cells, 0, 1, -Math.Sign(normal.Z))) return null;
        }
        var metric = pattern.SurfaceMetric.Compare(cell => grid.Contains((cell.X + x, cell.Y + y, cell.Z + z)));
        // Charge the residual faces of unchanged neighbouring cubes, too. A
        // triangle that merely carves a dent into a wall now makes area worse,
        // even though the triangle itself has a small outward-facing area.
        if (metric.SurfaceImprovement <= 1e-5 || metric.StepImprovement <= 1e-5) return null;
        var improvement = metric.StepImprovement;
        var deviation = Math.Abs(pattern.Volume - (cells.Length - outwardCells));
        var guard = new (int X, int Y, int Z)[pattern.GuardFootprint.Count];
        for (var index = 0; index < guard.Length; index++)
        {
            var relative = pattern.GuardFootprint[index];
            guard[index] = (relative.X + x, relative.Y + y, relative.Z + z);
        }
        return new(id, pattern, x, y, z, cells, backing.Distinct().ToArray(), guard, edits.ToArray(), improvement, deviation);
    }

    private static bool HasGradient(HullSurfaceGrid grid, IReadOnlyList<(int X, int Y, int Z)> cells,
        int dy, int dz, int expectedSign)
    {
        var found = false;
        foreach (var cell in cells)
        {
            var current = grid.SpanAt(cell.Y, cell.Z);
            foreach (var direction in new[] { -1, 1 })
            {
                var adjacent = grid.SpanAt(cell.Y + dy * direction, cell.Z + dz * direction);
                if (adjacent.IsEmpty) return false;
                var delta = (adjacent.MaxX - current.MaxX) * direction;
                if (delta * expectedSign < 0) return false;
                if (delta * expectedSign > 0) found = true;
            }
        }
        return found;
    }

    private static IReadOnlyList<Candidate> Search(IReadOnlyList<Candidate> candidates,
        CancellationToken token, out bool exhausted)
    {
        exhausted = false;
        if (candidates.Count == 0) return [];
        var states = new List<SearchState> { SearchState.Empty };
        var expansions = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var next = new List<SearchState>(states);
            foreach (var state in states)
            {
                if (++expansions > MaximumRegionExpansions)
                {
                    exhausted = true;
                    return [];
                }
                if (!Fits(candidate, state.Claimed, state.Support)) continue;
                next.Add(state.With(candidate));
            }
            states = next.OrderByDescending(state => state.Improvement)
                .ThenBy(state => state.Deviation).ThenBy(state => state.PlacementCount)
                .ThenBy(state => state.TieBreak, StringComparer.Ordinal).Take(BeamWidth).ToList();
        }
        return states[0].Candidates;
    }

    private static bool Fits(Candidate candidate, HashSet<(int X, int Y, int Z)> claimed,
        HashSet<(int X, int Y, int Z)> support) =>
        !candidate.Cells.Any(cell => claimed.Contains(cell) || support.Contains(cell)) &&
        !candidate.Guard.Any(claimed.Contains);

    private static SurfaceConstructionRegion MakeRegion(HullSurfaceGrid grid, Candidate candidate, int id)
    {
        var parts = candidate.Pattern.Placements.Select(part => part with
        {
            X = part.X + candidate.X, Y = part.Y + candidate.Y, Z = part.Z + candidate.Z,
        }).ToArray();
        var mirrored = parts.Select(part => part with
        {
            X = grid.MirrorSum - part.X,
            Shape = HullGeometryValidator.MirrorShape(part.Shape),
            Rotation = HullGeometryValidator.MirrorRotation(part.Rotation),
        });
        return new(id, candidate.Pattern.Name, parts.Concat(mirrored).ToArray(), candidate.Edits,
            candidate.Backing.Concat(candidate.Backing.Select(cell => (grid.MirrorSum - cell.X, cell.Y, cell.Z)))
                .Distinct().ToArray());
    }

    private sealed record Candidate(int Id, SurfaceAssemblyPattern Pattern, int X, int Y, int Z,
        IReadOnlyList<(int X, int Y, int Z)> Cells,
        IReadOnlyList<(int X, int Y, int Z)> Backing,
        IReadOnlyList<(int X, int Y, int Z)> Guard,
        IReadOnlyList<SurfaceSpanEdit> Edits, double Improvement, double Deviation);

    private sealed record SearchState(IReadOnlyList<Candidate> Candidates,
        HashSet<(int X, int Y, int Z)> Claimed, HashSet<(int X, int Y, int Z)> Support,
        double Improvement, double Deviation, int PlacementCount, string TieBreak)
    {
        public static SearchState Empty => new([], [], [], 0, 0, 0, "");
        public SearchState With(Candidate candidate) => new(Candidates.Append(candidate).ToArray(),
            new(Claimed.Concat(candidate.Cells)), new(Support.Concat(candidate.Guard)),
            Improvement + candidate.Improvement, Deviation + candidate.Deviation,
            PlacementCount + candidate.Pattern.Placements.Count, TieBreak + candidate.Id.ToString("D8") + ",");
    }
}
