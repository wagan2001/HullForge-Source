using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FtdHullGenerator.Domain.Design;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Geometry.HullSources;

public sealed record HistoricalObjImportOptions(
    double MetresPerSourceUnit,
    ObjAxisMapping AxisMapping,
    IReadOnlySet<string> IncludedHullGroups,
    int MaxInputBytes = 16 * 1024 * 1024,
    int MaxLineCharacters = 64 * 1024,
    int MaxVertices = 500_000,
    int MaxTriangles = 1_000_000,
    int MaxGroups = 1_024,
    int MaxFaceVertices = 4)
{
    public static HistoricalObjImportOptions Metres(
        params string[] includedHullGroups) =>
        new(1, ObjAxisMapping.Identity,
            new HashSet<string>(includedHullGroups, StringComparer.Ordinal));
}

public readonly record struct HistoricalTriangle(int A, int B, int C, string Group);

/// <summary>Bounded in-memory hull-only mesh. It is authoring input and is never a packaged asset.</summary>
public sealed class HistoricalMesh
{
    internal HistoricalMesh(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<HistoricalTriangle> triangles,
        HullEnvelopeBounds bounds,
        string sourceContentHash,
        IReadOnlyList<string> includedGroups,
        HistoricalSourceTransform sourceTransform)
    {
        Vertices = Array.AsReadOnly(vertices.ToArray());
        Triangles = Array.AsReadOnly(triangles.ToArray());
        Bounds = bounds;
        SourceContentHash = sourceContentHash;
        IncludedGroups = Array.AsReadOnly(includedGroups.Order(StringComparer.Ordinal).ToArray());
        SourceTransform = sourceTransform;
    }

    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<HistoricalTriangle> Triangles { get; }
    public HullEnvelopeBounds Bounds { get; }
    public string SourceContentHash { get; }
    public IReadOnlyList<string> IncludedGroups { get; }
    public HistoricalSourceTransform SourceTransform { get; }
}

public sealed record HistoricalTopologySummary(
    int BoundaryEdgeCount,
    int NonManifoldEdgeCount,
    int InconsistentWindingEdgeCount,
    int ConnectedComponentCount,
    double SignedVolumeCubicMetres)
{
    public bool IsClosedOrientedManifold => BoundaryEdgeCount == 0 && NonManifoldEdgeCount == 0 &&
                                            InconsistentWindingEdgeCount == 0 &&
                                            ConnectedComponentCount == 1 &&
                                            double.IsFinite(SignedVolumeCubicMetres) &&
                                            SignedVolumeCubicMetres > 0;
}

public sealed record HistoricalObjImportResult(
    HistoricalMesh? Mesh,
    HistoricalTopologySummary? Topology,
    IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool Success => Mesh is not null && Topology?.IsClosedOrientedManifold == true &&
                           !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// Narrow offline OBJ reader: vertices plus triangular/quad faces under explicitly selected
/// <c>g</c>/<c>o</c> labels. Materials, normals and texture coordinates never affect geometry.
/// </summary>
public static class HistoricalObjImporter
{
    public static HistoricalObjImportResult Import(
        string path,
        HistoricalObjImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var optionErrors = ValidateOptions(options);
        if (optionErrors.Count != 0)
            return new HistoricalObjImportResult(null, null, optionErrors);

        FileInfo file;
        try
        {
            file = new FileInfo(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                          PathTooLongException)
        {
            return Failure(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                $"The OBJ source path is invalid: {exception.Message}");
        }
        if (!file.Exists)
            return Failure(HistoricalDiagnosticCodes.ObjSyntaxInvalid, "The OBJ source file does not exist.");
        if (file.Length < 1 || file.Length > options.MaxInputBytes)
            return Failure(HistoricalDiagnosticCodes.InputLimitExceeded,
                $"OBJ input is {file.Length} bytes; the configured limit is {options.MaxInputBytes} bytes.");

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(HistoricalDiagnosticCodes.ObjSyntaxInvalid, $"The OBJ source could not be read: {exception.Message}");
        }
        using (stream)
        {
            if (stream.Length is < 1 || stream.Length > options.MaxInputBytes)
                return Failure(HistoricalDiagnosticCodes.InputLimitExceeded,
                    $"OBJ input is {stream.Length} bytes; the configured limit is {options.MaxInputBytes} bytes.");
            var rawVertices = new List<Vector3>();
            var triangles = new List<HistoricalTriangle>();
            var knownGroups = new HashSet<string>(StringComparer.Ordinal);
            var activeGroup = string.Empty;
            var diagnostics = new List<DesignDiagnostic>();

            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: true);
            for (var lineNumber = 1; ; lineNumber++)
            {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = reader.ReadLineAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                    $"OBJ input is not valid UTF-8 near line {lineNumber}."));
                break;
            }
            if (line is null)
                break;
            if (line.Contains('\0'))
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                    $"OBJ line {lineNumber} contains a NUL character."));
                break;
            }
            if (line.Length > options.MaxLineCharacters)
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                    $"OBJ line {lineNumber} exceeds the configured character limit."));
                break;
            }

            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
                continue;

            switch (fields[0])
            {
                case "v":
                    ParseVertex(fields, lineNumber, rawVertices, options, diagnostics);
                    break;
                case "g":
                case "o":
                    if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[1]))
                    {
                        diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.HullGroupInvalid,
                            $"OBJ line {lineNumber} must name exactly one bounded hull group."));
                        break;
                    }
                    activeGroup = fields[1];
                    knownGroups.Add(activeGroup);
                    if (knownGroups.Count > options.MaxGroups)
                        diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                            $"OBJ group count exceeds the configured limit {options.MaxGroups}."));
                    break;
                case "f":
                    ParseFace(fields, lineNumber, activeGroup, rawVertices, triangles, options, diagnostics);
                    break;
                case "vt":
                case "vn":
                case "s":
                case "usemtl":
                case "mtllib":
                    break;
                default:
                    diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                        $"OBJ line {lineNumber} uses unsupported directive '{fields[0]}'."));
                    break;
            }

            if (diagnostics.Any(diagnostic => diagnostic.IsError))
                break;
            }

            reader.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length > options.MaxInputBytes)
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                    $"OBJ input grew beyond the configured {options.MaxInputBytes}-byte limit while it was read."));

            var missingGroups = options.IncludedHullGroups.Where(group => !knownGroups.Contains(group))
            .Order(StringComparer.Ordinal).ToArray();
        if (missingGroups.Length != 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.HullGroupInvalid,
                $"Selected hull group(s) were not found: {string.Join(", ", missingGroups)}."));
        if (triangles.Count == 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.HullGroupInvalid,
                "The selected hull groups contain no faces."));
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return new HistoricalObjImportResult(null, null, Array.AsReadOnly(diagnostics.ToArray()));

        stream.Position = 0;
        string hash;
        try
        {
            hash = ComputeHash(stream, options.MaxInputBytes, cancellationToken);
        }
        catch (IOException exception)
        {
            return Failure(HistoricalDiagnosticCodes.InputLimitExceeded, exception.Message);
        }

        var transformed = new Vector3[rawVertices.Count];
        var scale = (float)options.MetresPerSourceUnit;
        for (var index = 0; index < rawVertices.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = options.AxisMapping.Transform(rawVertices[index]) * scale;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.UnitsOrAxesInvalid,
                    "Unit scaling overflowed a transformed OBJ vertex."));
                return new HistoricalObjImportResult(null, null, Array.AsReadOnly(diagnostics.ToArray()));
            }
            transformed[index] = point;
        }

        // A signed reflection changes triangle orientation. Preserve the source's geometric
        // winding in Hull Forge's right-handed frame by swapping two indices once.
        if (options.AxisMapping.DeterminantSign < 0)
        {
            for (var index = 0; index < triangles.Count; index++)
                triangles[index] = triangles[index] with { B = triangles[index].C, C = triangles[index].B };
        }

        var bounds = BoundsFor(transformed, triangles);
        if (!bounds.IsValid)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                "The selected hull has zero or invalid extent on at least one mapped axis."));

        var topology = AnalyzeTopology(transformed, triangles, cancellationToken);
        AddTopologyDiagnostics(topology, diagnostics);
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return new HistoricalObjImportResult(null, topology, Array.AsReadOnly(diagnostics.ToArray()));

        var mesh = new HistoricalMesh(transformed, triangles, bounds, hash,
            options.IncludedHullGroups.ToArray(),
            new HistoricalSourceTransform(options.MetresPerSourceUnit, options.AxisMapping));
            return new HistoricalObjImportResult(mesh, topology, Array.AsReadOnly(diagnostics.ToArray()));
        }
    }

    private static string ComputeHash(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var total = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new IOException("OBJ input grew beyond its configured byte limit while hashing.");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<DesignDiagnostic> ValidateOptions(HistoricalObjImportOptions options)
    {
        var diagnostics = new List<DesignDiagnostic>();
        if (!double.IsFinite(options.MetresPerSourceUnit) || options.MetresPerSourceUnit <= 0 ||
            options.MetresPerSourceUnit > 100_000 || options.AxisMapping is null ||
            !options.AxisMapping.IsValid)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.UnitsOrAxesInvalid,
                "OBJ units must be finite and positive and axes must be a bijective signed permutation."));
        if (options.IncludedHullGroups is null || options.IncludedHullGroups.Count == 0 ||
            options.IncludedHullGroups.Count > options.MaxGroups ||
            options.IncludedHullGroups.Any(group => string.IsNullOrWhiteSpace(group) || group.Length > 128))
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.HullGroupInvalid,
                "At least one explicit bounded hull group label is required."));
        if (options.MaxInputBytes is < 1 or > 256 * 1024 * 1024 ||
            options.MaxLineCharacters is < 32 or > 1024 * 1024 ||
            options.MaxVertices is < 4 or > 5_000_000 || options.MaxTriangles is < 4 or > 10_000_000 ||
            options.MaxGroups is < 1 or > 100_000 || options.MaxFaceVertices is < 3 or > 4)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                "One or more OBJ resource limits are outside the supported safety bounds."));
        return diagnostics;
    }

    private static void ParseVertex(
        string[] fields,
        int line,
        ICollection<Vector3> vertices,
        HistoricalObjImportOptions options,
        ICollection<DesignDiagnostic> diagnostics)
    {
        if (fields.Length != 4 || !TryFiniteFloat(fields[1], out var x) ||
            !TryFiniteFloat(fields[2], out var y) || !TryFiniteFloat(fields[3], out var z))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                $"OBJ line {line} must contain exactly three finite vertex coordinates."));
            return;
        }
        if (vertices.Count >= options.MaxVertices)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                $"OBJ vertex count exceeds the configured limit {options.MaxVertices}."));
            return;
        }
        vertices.Add(new Vector3(x, y, z));
    }

    private static void ParseFace(
        string[] fields,
        int line,
        string activeGroup,
        IReadOnlyList<Vector3> vertices,
        ICollection<HistoricalTriangle> triangles,
        HistoricalObjImportOptions options,
        ICollection<DesignDiagnostic> diagnostics)
    {
        if (!options.IncludedHullGroups.Contains(activeGroup))
            return;
        var faceCount = fields.Length - 1;
        if (faceCount < 3 || faceCount > options.MaxFaceVertices)
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                $"OBJ line {line} has {faceCount} face vertices; supported range is 3..{options.MaxFaceVertices}."));
            return;
        }
        var indices = new int[faceCount];
        for (var i = 0; i < faceCount; i++)
        {
            var token = fields[i + 1].Split('/')[0];
            if (!int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var raw) || raw == 0)
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                    $"OBJ line {line} has an invalid face index."));
                return;
            }
            var index = raw > 0 ? raw - 1 : vertices.Count + raw;
            if (index < 0 || index >= vertices.Count)
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                    $"OBJ line {line} references vertex {raw} outside the already parsed vertex range."));
                return;
            }
            indices[i] = index;
        }

        if (indices.Length == 4 && !IsValidQuad(indices, vertices))
        {
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                $"OBJ line {line} quad must be planar, strictly convex and simply ordered."));
            return;
        }

        for (var i = 1; i < indices.Length - 1; i++)
        {
            if (triangles.Count >= options.MaxTriangles)
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.InputLimitExceeded,
                    $"OBJ triangle count exceeds the configured limit {options.MaxTriangles}."));
                return;
            }
            if (indices[0] == indices[i] || indices[0] == indices[i + 1] || indices[i] == indices[i + 1])
            {
                diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.ObjSyntaxInvalid,
                    $"OBJ line {line} contains a degenerate face."));
                return;
            }
            triangles.Add(new HistoricalTriangle(indices[0], indices[i], indices[i + 1], activeGroup));
        }
    }

    private static bool IsValidQuad(IReadOnlyList<int> indices, IReadOnlyList<Vector3> vertices)
    {
        var points = indices.Select(index => vertices[index]).ToArray();
        var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
        var normalLength = normal.Length();
        if (!float.IsFinite(normalLength) || normalLength <= 1e-7f)
            return false;
        var planeDistance = Math.Abs(Vector3.Dot(points[3] - points[0], normal)) / normalLength;
        var edgeScale = Enumerable.Range(0, 4)
            .Max(index => Vector3.Distance(points[index], points[(index + 1) % 4]));
        if (!float.IsFinite(planeDistance) || !float.IsFinite(edgeScale) ||
            planeDistance > 1e-5 * Math.Max(1, edgeScale))
            return false;

        var drop = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z) ? 0 :
            Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? 1 : 2;
        static (double A, double B) Project(Vector3 value, int axis) => axis switch
        {
            0 => (value.Y, value.Z),
            1 => (value.X, value.Z),
            _ => (value.X, value.Y),
        };
        var projected = points.Select(point => Project(point, drop)).ToArray();
        double? sign = null;
        for (var index = 0; index < 4; index++)
        {
            var a = projected[index];
            var b = projected[(index + 1) % 4];
            var c = projected[(index + 2) % 4];
            var cross = (b.A - a.A) * (c.B - b.B) - (b.B - a.B) * (c.A - b.A);
            if (!double.IsFinite(cross) || Math.Abs(cross) <= 1e-9)
                return false;
            sign ??= Math.Sign(cross);
            if (Math.Sign(cross) != sign)
                return false;
        }
        return true;
    }

    private static bool TryFiniteFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static HullEnvelopeBounds BoundsFor(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<HistoricalTriangle> triangles)
    {
        var used = triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C }).Distinct().ToArray();
        var first = vertices[used[0]];
        double minX = first.X, maxX = first.X, minY = first.Y, maxY = first.Y, minZ = first.Z, maxZ = first.Z;
        foreach (var index in used)
        {
            var point = vertices[index];
            minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
            minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
        }
        return new HullEnvelopeBounds(minX, maxX, minY, maxY, minZ, maxZ);
    }

    private static HistoricalTopologySummary AnalyzeTopology(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<HistoricalTriangle> triangles,
        CancellationToken cancellationToken)
    {
        var edges = new Dictionary<(int A, int B), List<(int From, int To, int Triangle)>>();
        double signedVolume = 0;
        for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var triangle = triangles[triangleIndex];
            AddEdge(triangle.A, triangle.B);
            AddEdge(triangle.B, triangle.C);
            AddEdge(triangle.C, triangle.A);
            var a = vertices[triangle.A];
            var b = vertices[triangle.B];
            var c = vertices[triangle.C];
            // Calculate in double precision so finite OBJ floats cannot overflow Vector3's
            // float cross product before topology validation sees the result.
            signedVolume += ((double)a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y) +
                             (double)a.Y * ((double)b.Z * c.X - (double)b.X * c.Z) +
                             (double)a.Z * ((double)b.X * c.Y - (double)b.Y * c.X)) / 6.0;

            void AddEdge(int from, int to)
            {
                var key = from < to ? (from, to) : (to, from);
                if (!edges.TryGetValue(key, out var uses))
                    edges[key] = uses = [];
                uses.Add((from, to, triangleIndex));
            }
        }

        var boundary = edges.Values.Count(uses => uses.Count == 1);
        var nonManifold = edges.Values.Count(uses => uses.Count > 2);
        var inconsistent = edges.Values.Count(uses => uses.Count == 2 &&
            uses[0].From == uses[1].From && uses[0].To == uses[1].To);

        var adjacency = Enumerable.Range(0, triangles.Count).Select(_ => new List<int>()).ToArray();
        foreach (var uses in edges.Values.Where(uses => uses.Count >= 2))
        {
            // Edge-connected components are required. Merely touching at one vertex does not join
            // two hull surfaces into one manifold component.
            var owningTriangles = uses.Select(use => use.Triangle).Distinct().ToArray();
            for (var i = 1; i < owningTriangles.Length; i++)
            {
                adjacency[owningTriangles[0]].Add(owningTriangles[i]);
                adjacency[owningTriangles[i]].Add(owningTriangles[0]);
            }
        }

        var remaining = new HashSet<int>(Enumerable.Range(0, triangles.Count));
        var components = 0;
        var queue = new Queue<int>();
        while (remaining.Count != 0)
        {
            components++;
            var seed = remaining.First();
            remaining.Remove(seed);
            queue.Enqueue(seed);
            while (queue.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = queue.Dequeue();
                foreach (var adjacent in adjacency[index])
                {
                    if (remaining.Remove(adjacent))
                        queue.Enqueue(adjacent);
                }
            }
        }

        return new HistoricalTopologySummary(boundary, nonManifold, inconsistent, components, signedVolume);
    }

    private static void AddTopologyDiagnostics(
        HistoricalTopologySummary topology,
        ICollection<DesignDiagnostic> diagnostics)
    {
        if (topology.BoundaryEdgeCount != 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.TopologyOpen,
                $"Selected hull mesh has {topology.BoundaryEdgeCount} open boundary edge(s)."));
        if (topology.NonManifoldEdgeCount != 0)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.TopologyNonManifold,
                $"Selected hull mesh has {topology.NonManifoldEdgeCount} non-manifold edge(s)."));
        if (topology.InconsistentWindingEdgeCount != 0 ||
            !double.IsFinite(topology.SignedVolumeCubicMetres) ||
            topology.SignedVolumeCubicMetres <= 1e-9)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.WindingInvalid,
                "Selected hull mesh has inconsistent or inward/zero-volume winding."));
        if (topology.ConnectedComponentCount != 1)
            diagnostics.Add(DesignDiagnostic.Error(HistoricalDiagnosticCodes.TopologyDisconnected,
                $"Selected hull mesh has {topology.ConnectedComponentCount} disconnected surface components."));
    }

    private static HistoricalObjImportResult Failure(string code, string message) =>
        new(null, null, Array.AsReadOnly(new[] { DesignDiagnostic.Error(code, message) }));
}
