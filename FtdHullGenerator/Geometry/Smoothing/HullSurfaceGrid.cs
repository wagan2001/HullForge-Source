namespace FtdHullGenerator.Geometry.Smoothing;

/// <summary>A sampled, symmetric solid row; an empty row has MaxX smaller than MinX.</summary>
public readonly record struct HullSurfaceSpan(int MinX, int MaxX)
{
    public static HullSurfaceSpan Empty => new(1, 0);
    public bool IsEmpty => MaxX < MinX;
    public bool Contains(int x) => !IsEmpty && x >= MinX && x <= MaxX;
}

/// <summary>
/// Immutable snapshot of the sampled solid, before shell extraction. All planner
/// comparisons use this snapshot rather than an already regularized neighbour.
/// </summary>
public sealed class HullSurfaceGrid
{
    private readonly HullSurfaceSpan[,] _spans;
    private readonly bool[,] _protected;
    private readonly int[] _floors;
    private readonly int[] _decks;
    private readonly byte[,] _cavityFaces;

    public HullSurfaceGrid(int minX, int maxX, int minY, int maxY, int minZ, int maxZ,
        Func<int, int, HullSurfaceSpan> spanAt, Func<int, int> floorAt,
        Func<int, int> deckAt, Func<int, int, bool>? protectedRow = null)
    {
        ArgumentNullException.ThrowIfNull(spanAt);
        ArgumentNullException.ThrowIfNull(floorAt);
        ArgumentNullException.ThrowIfNull(deckAt);
        if (maxX < minX || maxY < minY || maxZ < minZ)
            throw new ArgumentException("The surface bounds must be ordered.");
        (MinX, MaxX, MinY, MaxY, MinZ, MaxZ) = (minX, maxX, minY, maxY, minZ, maxZ);
        _spans = new HullSurfaceSpan[maxY - minY + 1, maxZ - minZ + 1];
        _protected = new bool[maxY - minY + 1, maxZ - minZ + 1];
        _cavityFaces = new byte[maxY - minY + 1, maxZ - minZ + 1];
        _floors = new int[maxZ - minZ + 1];
        _decks = new int[maxZ - minZ + 1];
        for (var z = minZ; z <= maxZ; z++)
        {
            _floors[z - minZ] = floorAt(z);
            _decks[z - minZ] = deckAt(z);
            for (var y = minY; y <= maxY; y++)
            {
                var span = spanAt(y, z);
                if (!span.IsEmpty && (span.MinX < minX || span.MaxX > maxX ||
                                     span.MinX + span.MaxX != minX + maxX))
                    throw new ArgumentException("Surface rows must lie inside the bounds and share the symmetry plane.");
                _spans[y - minY, z - minZ] = span;
                _protected[y - minY, z - minZ] = protectedRow?.Invoke(y, z) == true ||
                    span.IsEmpty || y <= floorAt(z) || y >= deckAt(z) || z == minZ || z == maxZ ||
                    span.MaxX == maxX;
            }
        }
        for (var z = minZ; z <= maxZ; z++)
        for (var y = minY; y <= maxY; y++)
        {
            var span = SpanAt(y, z);
            if (span.IsEmpty) continue;
            byte mask = 1; // The inward (-X) membrane is always protected.
            for (var index = 1; index < FtdHullGenerator.Domain.BlockRotations.AxisDirections.Length; index++)
            {
                var axis = FtdHullGenerator.Domain.BlockRotations.AxisDirections[index];
                var neighbour = (X: span.MaxX + axis.X, Y: y + axis.Y, Z: z + axis.Z);
                if (Contains(neighbour) && !IsExterior(neighbour)) mask |= (byte)(1 << index);
            }
            _cavityFaces[y - minY, z - minZ] = mask;
        }
    }

    public int MinX { get; }
    public int MaxX { get; }
    public int MinY { get; }
    public int MaxY { get; }
    public int MinZ { get; }
    public int MaxZ { get; }
    public int MirrorSum => MinX + MaxX;
    public HullSurfaceSpan SpanAt(int y, int z) => InBounds(y, z)
        ? _spans[y - MinY, z - MinZ] : HullSurfaceSpan.Empty;
    public int FloorAt(int z) => _floors[z - MinZ];
    public int DeckAt(int z) => _decks[z - MinZ];
    public bool IsProtected(int y, int z) => !InBounds(y, z) || _protected[y - MinY, z - MinZ];
    internal byte CavityFaceMask(int y, int z) => _cavityFaces[y - MinY, z - MinZ];
    public bool Contains((int X, int Y, int Z) cell) => SpanAt(cell.Y, cell.Z).Contains(cell.X);
    public bool IsExterior((int X, int Y, int Z) cell)
    {
        var row = SpanAt(cell.Y, cell.Z);
        return row.Contains(cell.X) && (cell.X == row.MinX || cell.X == row.MaxX ||
            !SpanAt(cell.Y - 1, cell.Z).Contains(cell.X) || !SpanAt(cell.Y + 1, cell.Z).Contains(cell.X) ||
            !SpanAt(cell.Y, cell.Z - 1).Contains(cell.X) || !SpanAt(cell.Y, cell.Z + 1).Contains(cell.X));
    }
    private bool InBounds(int y, int z) => y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ;

    internal HullSurfaceGrid WithEdits(IEnumerable<SurfaceSpanEdit> edits)
    {
        var byRow = edits.ToDictionary(edit => (edit.Y, edit.Z), edit => edit.Revised);
        return new HullSurfaceGrid(MinX, MaxX, MinY, MaxY, MinZ, MaxZ,
            (y, z) => byRow.TryGetValue((y, z), out var value) ? value : SpanAt(y, z),
            FloorAt, DeckAt, IsProtected);
    }
}

public readonly record struct SurfaceSpanEdit(int Y, int Z, HullSurfaceSpan Original, HullSurfaceSpan Revised);
