using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Projects;
using FtdHullGenerator.Domain.Sketchbook;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.Geometry.Composition;
using FtdHullGenerator.Infrastructure;

namespace FtdHullGenerator.UI.Sketchbook;

/// <summary>Which native view a sketchbook thumbnail draws.</summary>
public enum SketchbookThumbnailView
{
    /// <summary>The default three-quarter view.</summary>
    Isometric,

    /// <summary>A real bow-on cut through the widest midship station, for lower-body-dependent identities.</summary>
    BowSection,
}

/// <summary>
/// One completed native thumbnail: the frozen bitmap plus the honest labels that describe how it
/// was produced. It never claims speed, stability, seakeeping, armor or reconstruction.
/// </summary>
public sealed record SketchbookThumbnail(
    string EntryId,
    SketchbookThumbnailView View,
    BitmapSource Image,
    bool CatalogResolved,
    string ViewLabel,
    string StatusLine);

/// <summary>
/// Produces the browser's thumbnails from the real native C# generator and the real
/// <see cref="HullPreviewControl" /> renderer. No Python study, sampled envelope or diagnostic
/// render is an image source here.
/// </summary>
/// <remarks>
/// Generation is pure and thread-safe, so it may run on a background thread. Rendering hosts a
/// WPF <see cref="HullPreviewControl" /> and therefore must run on a Dispatcher (UI) thread.
/// Completed thumbnails are cached by entry, catalog identity, view and pixel size; a catalog
/// change must call <see cref="ClearCache" /> so a stale image can never remain.
/// </remarks>
public static class SketchbookThumbnailRenderer
{
    /// <summary>The default thumbnail width in device-independent pixels.</summary>
    public const int DefaultWidth = 264;

    /// <summary>The default thumbnail height in device-independent pixels.</summary>
    public const int DefaultHeight = 176;

    private const string NoCatalogIdentity = "no-catalog";

    private static readonly ConcurrentDictionary<string, SketchbookThumbnail> Cache =
        new(StringComparer.Ordinal);

    private static readonly Brush HostBackground = CreateHostBackground();

    /// <summary>
    /// Builds the resolved hull for one entry. With a catalog it uses the same
    /// <see cref="ShipGenerationService" /> snapshot the editor exports; without one it uses the
    /// provisional raw generator and reports that honestly through <paramref name="catalogResolved" />.
    /// A catalog that cannot resolve the entry throws rather than silently falling back.
    /// </summary>
    internal static GeneratedHull GenerateHull(
        AlternateNavalSketchbookEntry entry, FtdBlockCatalog? catalog, out bool catalogResolved)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The neutral catalog basis: the entry owns no smoothing and no armor, so the preview is
        // drawn on the same HullParameters.Default basis the committed native audit measured. That
        // keeps the shipped section inset exactly consistent with Fixtures/sketchbook-native-audit.json
        // instead of silently inheriting the editor's current smoothing.
        var basis = ShipDocument.CreateNew(entry.Name, HullParameters.Default);
        var document = SketchbookShapeInitializer.Apply(
            entry, SketchbookSizePolicy.SuggestedDimensions, basis).Document;

        if (catalog is not null)
        {
            var result = new ShipGenerationService().Generate(document, revision: 1, catalog);
            if (!result.IsValid || result.Snapshot is null)
            {
                throw new HullGenerationException(
                    result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
            }

            catalogResolved = true;
            return result.Snapshot.Hull;
        }

        catalogResolved = false;
        return new HullGenerator().Generate(document.Hull);
    }

    /// <summary>
    /// The stable cache identity of a catalog, or <c>"no-catalog"</c> for the provisional path.
    /// </summary>
    internal static string CatalogIdentity(FtdBlockCatalog? catalog) =>
        catalog is null ? NoCatalogIdentity : ShipCatalogFingerprint.Compute(catalog);

    /// <summary>
    /// Renders one already-generated hull. Must run on a Dispatcher thread. The result is frozen
    /// and cached; an identical request returns the cached thumbnail.
    /// </summary>
    public static SketchbookThumbnail RenderHull(
        AlternateNavalSketchbookEntry entry,
        GeneratedHull hull,
        bool catalogResolved,
        SketchbookThumbnailView view,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(hull);
        var identity = catalogResolved
            ? hull.ResolvedCatalogFingerprint ?? "catalog-resolved"
            : NoCatalogIdentity;
        var key = CacheKey(entry.Id, identity, view, width, height);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var thumbnail = RenderCore(entry, hull, catalogResolved, view, width, height);
        Cache[key] = thumbnail;
        return thumbnail;
    }

    /// <summary>
    /// Generates and renders one thumbnail. Generation runs inline, so callers on the UI thread
    /// should prefer generating off-thread and calling <see cref="RenderHull" />.
    /// </summary>
    public static SketchbookThumbnail Render(
        AlternateNavalSketchbookEntry entry,
        FtdBlockCatalog? catalog,
        SketchbookThumbnailView view,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var hull = GenerateHull(entry, catalog, out var catalogResolved);
        return RenderHull(entry, hull, catalogResolved, view, width, height);
    }

    /// <summary>Empties the completed-thumbnail cache. A catalog change must call this.</summary>
    public static void ClearCache() => Cache.Clear();

    private static SketchbookThumbnail RenderCore(
        AlternateNavalSketchbookEntry entry,
        GeneratedHull hull,
        bool catalogResolved,
        SketchbookThumbnailView view,
        int width,
        int height)
    {
        var control = new HullPreviewControl();
        var host = new Border
        {
            Width = width,
            Height = height,
            Background = HostBackground,
            Child = control,
        };
        // Arrange first so the camera framing sees the real pane aspect, then apply the hull and
        // the standing view, then lay out again before the single render.
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        control.SetHull(hull, highlightEdits: false);
        if (view == SketchbookThumbnailView.BowSection)
        {
            var evidence = SketchbookSectionEvidenceReader.Measure(hull);
            control.Cutaway = CutawayPlane.Station;
            control.CutawayFraction = SketchbookSectionEvidenceReader.CutFraction(hull, evidence);
            control.ShowView(PreviewView.Bow);
        }
        else
        {
            control.ShowView(PreviewView.Isometric);
        }

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();

        var viewLabel = ViewLabel(view);
        return new SketchbookThumbnail(
            entry.Id, view, bitmap, catalogResolved, viewLabel, StatusLine(catalogResolved, viewLabel));
    }

    private static string ViewLabel(SketchbookThumbnailView view) => view switch
    {
        SketchbookThumbnailView.BowSection => "Midship station section (bow-on)",
        _ => "Isometric",
    };

    private static string StatusLine(bool catalogResolved, string viewLabel) =>
        (catalogResolved
            ? "Native Hull Forge preview · catalog-resolved · "
            : "Native Hull Forge preview · provisional, no installed catalog · ") + viewLabel;

    private static string CacheKey(
        string entryId, string identity, SketchbookThumbnailView view, int width, int height) =>
        $"{entryId}|{identity}|{view}|{width}x{height}";

    private static Brush CreateHostBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E));
        brush.Freeze();
        return brush;
    }
}
