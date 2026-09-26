using FtdHullGenerator.Geometry;
using FtdHullGenerator.Infrastructure;

internal static class ExperimentalConstructionTests
{
    public static void Run(HullGenerator generator, FtdBlockCatalog catalog)
    {
        var hull = InvertedTriangleFillTests.Run(generator);
        var directory = Path.Combine(Path.GetTempPath(), "HullForge-Experimental-" + Guid.NewGuid().ToString("N"));
        try
        {
            InvertedTriangleFillTests.VerifyExport(hull, catalog, directory);
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            var allowedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "HullForge-Experimental-");
            if (resolved.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }

        Console.WriteLine("Experimental construction: inverted fill, synthetic construction dependencies, and export passed.");
    }
}
