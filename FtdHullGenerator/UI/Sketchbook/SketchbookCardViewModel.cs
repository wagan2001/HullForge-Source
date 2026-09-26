using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using FtdHullGenerator.Domain.Sketchbook;

namespace FtdHullGenerator.UI.Sketchbook;

/// <summary>
/// One card in the Hull Presets browser. It wraps one catalog entry by reference and adds only
/// presentation state, so the UI can never become a second geometry table.
/// </summary>
public sealed class SketchbookCardViewModel : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private BitmapSource? _sectionInset;
    private string? _thumbnailError;

    public SketchbookCardViewModel(AlternateNavalSketchbookEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    /// <summary>The catalog entry this card presents, held by reference; geometry is never copied.</summary>
    public AlternateNavalSketchbookEntry Entry { get; }

    public string Name => Entry.Name;

    public int Ordinal => Entry.Ordinal;

    /// <summary>The family grouping key for the browser; never rendered on the card itself.</summary>
    public string Family => Entry.Family;

    /// <summary>The suggested envelope, for the card's compact size line.</summary>
    public string SuggestedSummary => Entry.EnvelopeSummary;

    /// <summary>The smallest supported envelope, for the card's compact size line.</summary>
    public string MinimumSummary => Entry.MinimumSummary;

    /// <summary>
    /// Whether this identity depends on its lower body enough that a top-only view would be
    /// dishonest. The browser renders a real bow-on station section inset for these entries.
    /// </summary>
    public bool ShowsSectionInset => Ordinal is 3 or 15 or 16;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public BitmapSource? SectionInset
    {
        get => _sectionInset;
        set => Set(ref _sectionInset, value);
    }

    /// <summary>The honest per-card failure message, or null when the thumbnail is fine.</summary>
    public string? ThumbnailError
    {
        get => _thumbnailError;
        set
        {
            if (Set(ref _thumbnailError, value))
                OnPropertyChanged(nameof(HasThumbnailError));
        }
    }

    public bool HasThumbnailError => !string.IsNullOrWhiteSpace(_thumbnailError);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Returns the card to its pending state after a catalog change.</summary>
    internal void ResetThumbnails()
    {
        Thumbnail = null;
        SectionInset = null;
        ThumbnailError = null;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
