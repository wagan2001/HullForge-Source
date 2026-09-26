using FtdHullGenerator.Domain;

namespace FtdHullGenerator.Geometry;

/// <summary>Builds an optional superstructure on a completed hull.</summary>
public static class SuperstructureGenerator
{
    private readonly record struct Rect(int MinX, int MaxX, int MinZ, int MaxZ)
    {
        public int Width => MaxX - MinX + 1;
        public int Length => MaxZ - MinZ + 1;
        public Rect Inset(int amount) => new(MinX + amount, MaxX - amount, MinZ + amount, MaxZ - amount);
        public Rect Expand(int amount, Rect limit) => new(
            Math.Max(limit.MinX, MinX - amount), Math.Min(limit.MaxX, MaxX + amount),
            Math.Max(limit.MinZ, MinZ - amount), Math.Min(limit.MaxZ, MaxZ + amount));
    }

    private sealed record Fit(Rect Base, int DeckY);

    public static GeneratedHull Apply(GeneratedHull hull, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var settings = hull.Parameters.EffectiveSuperstructure;
        if (!settings.Enabled)
            return hull;
        var errors = settings.Validate(hull.Parameters.HasDeck);
        if (errors.Count > 0)
            throw new HullGenerationException(errors);

        var fit = FindFit(hull, settings, cancellationToken) ?? throw new HullGenerationException(
            [$"The {Label(settings.Style)} superstructure with {settings.Levels} level(s) does not fit on a flat deck area. Increase hull length or width, reduce the levels, or choose another style."]);
        var additions = Build(settings, fit, cancellationToken);
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        foreach (var block in additions)
        foreach (var cell in block.OccupiedCells)
        {
            if (!occupied.Add(cell))
                throw new HullGenerationException([$"The superstructure overlaps an existing block at ({cell.X}, {cell.Y}, {cell.Z})."]);
        }

        var blocks = hull.Blocks.Concat(additions).OrderBy(block => block.Z)
            .ThenBy(block => block.Y).ThenBy(block => block.X).ToArray();
        var cells = blocks.SelectMany(block => block.OccupiedCells).ToArray();
        var result = hull with
        {
            Blocks = blocks,
            MinX = cells.Min(cell => cell.X), MaxX = cells.Max(cell => cell.X),
            MinY = cells.Min(cell => cell.Y), MaxY = cells.Max(cell => cell.Y),
            MinZ = cells.Min(cell => cell.Z), MaxZ = cells.Max(cell => cell.Z),
        };
        HullGeometryValidator.EnsureValid(result);
        return result;
    }

    private static Fit? FindFit(GeneratedHull hull, SuperstructureSettings settings, CancellationToken token)
    {
        var occupied = hull.Blocks.SelectMany(block => block.OccupiedCells).ToHashSet();
        var top = occupied.GroupBy(cell => (cell.X, cell.Z))
            .ToDictionary(group => group.Key, group => group.Max(cell => cell.Y));
        var (widthRatio, lengthRatio, topInteriorWidth, topInteriorLength) = settings.Style switch
        {
            SuperstructureStyle.CenterIsland => (0.60, 0.40, 5, 9),
            SuperstructureStyle.FrenchHotel => (0.70, 0.30, 3, 5),
            SuperstructureStyle.JapanesePagoda => (0.35, 0.14, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(settings.Style)),
        };
        var maxInset = InsetFor(settings.Style, settings.Levels);
        var requiredWidth = MatchWidthParity(topInteriorWidth + 2 + 2 * maxInset, hull.OccupiedWidth);
        var requiredLength = MakeOdd(topInteriorLength + 2 + 2 * maxInset);
        var targetWidth = MatchWidthParity(Math.Max(requiredWidth, (int)Math.Round(hull.Parameters.Width * widthRatio)), hull.OccupiedWidth);
        var targetLength = MakeOdd(Math.Max(requiredLength, (int)Math.Round(hull.Parameters.Length * lengthRatio)));
        targetWidth = Math.Min(targetWidth, hull.OccupiedWidth);
        targetLength = LargestOddAtMost(Math.Min(targetLength, hull.OccupiedLength));
        var mirrorSum = hull.MinX + hull.MaxX;
        var targetZ = (hull.MinZ + hull.MaxZ) / 2 +
            (int)Math.Round(hull.OccupiedLength * settings.ForeAftPercent / 100.0);

        Fit? best = null;
        var bestArea = -1;
        var bestAspectError = double.MaxValue;
        var bestDistance = int.MaxValue;
        for (var width = targetWidth; width >= requiredWidth; width -= 2)
        for (var length = targetLength; length >= requiredLength; length -= 2)
        for (var centerZ = hull.MinZ; centerZ <= hull.MaxZ; centerZ++)
        {
            token.ThrowIfCancellationRequested();
            var minX = (mirrorSum - width + 1) / 2;
            var minZ = centerZ - length / 2;
            var rect = new Rect(minX, minX + width - 1, minZ, minZ + length - 1);
            if (rect.MinX - 1 < hull.MinX || rect.MaxX + 1 > hull.MaxX ||
                rect.MinZ - 1 < hull.MinZ || rect.MaxZ + 1 > hull.MaxZ)
                continue;
            if (!top.TryGetValue((rect.MinX, centerZ), out var deckY) || !HasFlatSupport(rect, deckY, top))
                continue;
            var area = width * length;
            var aspectError = Math.Abs((double)length / width - (double)targetLength / targetWidth);
            var distance = Math.Abs(centerZ - targetZ);
            if (area < bestArea || area == bestArea && aspectError > bestAspectError ||
                area == bestArea && Math.Abs(aspectError - bestAspectError) < 0.000001 && distance >= bestDistance)
                continue;
            best = new Fit(rect, deckY);
            bestArea = area;
            bestAspectError = aspectError;
            bestDistance = distance;
        }
        return best;

        static bool HasFlatSupport(Rect rect, int deckY, IReadOnlyDictionary<(int X, int Z), int> top)
        {
            for (var x = rect.MinX - 1; x <= rect.MaxX + 1; x++)
            for (var z = rect.MinZ - 1; z <= rect.MaxZ + 1; z++)
                if (!top.TryGetValue((x, z), out var y) || y != deckY)
                    return false;
            return true;
        }
    }

    private static IReadOnlyList<BlockPlacement> Build(SuperstructureSettings settings, Fit fit, CancellationToken token)
    {
        var blocks = new List<BlockPlacement>();
        var claimed = new HashSet<(int X, int Y, int Z)>();
        var floorY = fit.DeckY;
        for (var level = 1; level <= settings.Levels; level++)
        {
            token.ThrowIfCancellationRequested();
            var bounds = fit.Base.Inset(InsetFor(settings.Style, level));
            var clearHeight = SuperstructureSettings.ClearHeight(settings.Style, level);
            for (var y = floorY + 1; y <= floorY + clearHeight; y++)
            {
                for (var x = bounds.MinX; x <= bounds.MaxX; x++)
                for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                {
                    if (x != bounds.MinX && x != bounds.MaxX && z != bounds.MinZ && z != bounds.MaxZ)
                        continue;
                    AddCube(x, y, z);
                }
            }

            if (settings.Smoothing == SuperstructureSmoothingMethod.HorizontalSlopeFill &&
                level > 1 && InsetFor(settings.Style, level) > InsetFor(settings.Style, level - 1))
            {
                var y = floorY + 1;
                for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                {
                    AddSlope(bounds.MinX - 1, y, z, 23);
                    AddSlope(bounds.MaxX + 1, y, z, 22);
                }
            }

            var roof = settings.Style == SuperstructureStyle.JapanesePagoda && level is 3 or 5 or 7
                ? bounds.Expand(1, fit.Base) : bounds;
            var roofY = floorY + clearHeight + 1;
            for (var x = roof.MinX; x <= roof.MaxX; x++)
            for (var z = roof.MinZ; z <= roof.MaxZ; z++)
                AddCube(x, roofY, z);
            floorY = roofY;
        }
        return blocks;

        void AddCube(int x, int y, int z)
        {
            if (!claimed.Add((x, y, z)))
                return;
            blocks.Add(new BlockPlacement(BlockShape.Cube, settings.Material, x, y, z, 0)
            {
                Origin = BlockOrigin.Superstructure,
            });
        }

        void AddSlope(int x, int y, int z, int rotation)
        {
            if (!claimed.Add((x, y, z)))
                return;
            blocks.Add(new BlockPlacement(BlockShape.Slope1, settings.Material, x, y, z, rotation)
            {
                Origin = BlockOrigin.SuperstructureSmoothing,
            });
        }
    }

    private static int InsetFor(SuperstructureStyle style, int level) => style switch
    {
        SuperstructureStyle.CenterIsland => 0,
        SuperstructureStyle.FrenchHotel => (level - 1) / 3,
        SuperstructureStyle.JapanesePagoda => (level - 1) / 2,
        _ => 0,
    };

    private static int MatchWidthParity(int width, int hullWidth)
    {
        width = Math.Max(1, width);
        return width % 2 == hullWidth % 2 ? width : width + 1;
    }

    private static int MakeOdd(int value) => value % 2 == 0 ? value + 1 : value;

    private static int LargestOddAtMost(int value) => value % 2 == 0 ? value - 1 : value;

    private static string Label(SuperstructureStyle style) => style switch
    {
        SuperstructureStyle.CenterIsland => "Center Island",
        SuperstructureStyle.FrenchHotel => "French Hotel",
        SuperstructureStyle.JapanesePagoda => "Japanese Pagoda",
        _ => style.ToString(),
    };
}
