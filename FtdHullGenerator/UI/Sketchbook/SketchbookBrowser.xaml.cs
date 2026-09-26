using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Domain.Sketchbook;
using FtdHullGenerator.Infrastructure;

namespace FtdHullGenerator.UI.Sketchbook;

/// <summary>The one entry + size policy a browser action asks the editor to apply.</summary>
public sealed class SketchbookEntryEventArgs(
    AlternateNavalSketchbookEntry entry, SketchbookSizePolicy policy) : EventArgs
{
    public AlternateNavalSketchbookEntry Entry { get; } = entry;

    public SketchbookSizePolicy Policy { get; } = policy;
}

/// <summary>
/// The Hull Presets browse surface: sixteen minimal cards in catalog order, grouped by family,
/// each showing its two size choices and a real native thumbnail.
/// </summary>
/// <remarks>
/// The browser is a launcher, not a mode. Every card reads its geometry and presentation from the
/// single <see cref="AlternateNavalSketchbookCatalog" />; this control owns no entry data of its
/// own and never commits an editor transaction. Choosing a size raises <see cref="EntrySelected" />
/// with the entry and policy; the host editor applies it as one transaction.
/// </remarks>
public sealed partial class SketchbookBrowser : UserControl
{
    public SketchbookBrowser()
    {
        InitializeComponent();
        Cards = AlternateNavalSketchbookCatalog.All
            .Select(entry => new SketchbookCardViewModel(entry))
            .ToArray();

        var view = new ListCollectionView(Cards.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SketchbookCardViewModel.Family)));
        CardList.ItemsSource = view;
    }

    /// <summary>The sixteen cards, in catalog order, holding the catalog entries by reference.</summary>
    public IReadOnlyList<SketchbookCardViewModel> Cards { get; }

    // Bumped by every invalidation so a population started for a superseded catalog stops
    // assigning instead of repainting a stale image over the new one.
    private int _thumbnailGeneration;

    /// <summary>
    /// Raised when the user chooses a size for one entry. The host editor resolves and commits the
    /// entry; the browser keeps no selection state.
    /// </summary>
    public event EventHandler<SketchbookEntryEventArgs>? EntrySelected;

    internal ItemsControl CardListForTests => CardList;

    internal ICollectionView? CardViewForTests => CardList.ItemsSource as ICollectionView;

    /// <summary>Raises the selection event for one catalog id, without any highlight state.</summary>
    internal void SelectForTests(string id, SketchbookSizePolicy policy)
    {
        var card = Cards.FirstOrDefault(candidate =>
                string.Equals(candidate.Entry.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown sketchbook entry id '{id}'.");
        EntrySelected?.Invoke(this, new SketchbookEntryEventArgs(card.Entry, policy));
    }

    /// <summary>Clears every rendered thumbnail and the renderer cache after a catalog change.</summary>
    internal void InvalidateThumbnails()
    {
        _thumbnailGeneration++;
        foreach (var card in Cards)
            card.ResetThumbnails();
        SketchbookThumbnailRenderer.ClearCache();
    }

    /// <summary>
    /// Fills in every card's native thumbnails, generating off the UI thread and rendering on it.
    /// A per-card failure is reported on that card and never aborts the loop. A population
    /// superseded by a later invalidation stops instead of repainting a stale catalog image.
    /// </summary>
    internal async Task PopulateThumbnailsAsync(FtdBlockCatalog? catalog, CancellationToken cancellationToken)
    {
        var generation = _thumbnailGeneration;
        foreach (var card in Cards)
        {
            if (cancellationToken.IsCancellationRequested || generation != _thumbnailGeneration)
                return;
            try
            {
                var entry = card.Entry;
                var generated = await Task.Run(() =>
                {
                    var hull = SketchbookThumbnailRenderer.GenerateHull(entry, catalog, out var resolved);
                    return (Hull: hull, Resolved: resolved);
                }, cancellationToken);

                if (generation != _thumbnailGeneration)
                    return;
                RenderCard(card, generated.Hull, generated.Resolved);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                card.ThumbnailError = error.Message;
            }
        }
    }

    /// <summary>
    /// The synchronous headless equivalent of <see cref="PopulateThumbnailsAsync" />. It renders
    /// only the requested ids and returns how many were populated; a per-card failure is reported
    /// on that card.
    /// </summary>
    internal int PopulateThumbnailsForTests(FtdBlockCatalog? catalog, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var populated = 0;
        foreach (var id in ids)
        {
            var card = Cards.FirstOrDefault(candidate =>
                string.Equals(candidate.Entry.Id, id, StringComparison.Ordinal));
            if (card is null)
                continue;
            try
            {
                var hull = SketchbookThumbnailRenderer.GenerateHull(card.Entry, catalog, out var resolved);
                RenderCard(card, hull, resolved);
                populated++;
            }
            catch (Exception error)
            {
                card.ThumbnailError = error.Message;
            }
        }

        return populated;
    }

    private static void RenderCard(
        SketchbookCardViewModel card, GeneratedHull hull, bool catalogResolved)
    {
        var thumbnail = SketchbookThumbnailRenderer.RenderHull(
            card.Entry, hull, catalogResolved, SketchbookThumbnailView.Isometric);
        card.Thumbnail = thumbnail.Image;
        card.ThumbnailError = null;

        if (!card.ShowsSectionInset)
            return;
        var section = SketchbookThumbnailRenderer.RenderHull(
            card.Entry, hull, catalogResolved, SketchbookThumbnailView.BowSection);
        card.SectionInset = section.Image;
    }

    private void UseSuggestedSizeClicked(object sender, RoutedEventArgs eventArgs) =>
        RaiseEntrySelected(sender, SketchbookSizePolicy.SuggestedDimensions);

    private void KeepCurrentSizeClicked(object sender, RoutedEventArgs eventArgs) =>
        RaiseEntrySelected(sender, SketchbookSizePolicy.KeepCurrentDimensions);

    private void RaiseEntrySelected(object sender, SketchbookSizePolicy policy)
    {
        if ((sender as FrameworkElement)?.DataContext is not SketchbookCardViewModel card)
            return;
        EntrySelected?.Invoke(this, new SketchbookEntryEventArgs(card.Entry, policy));
    }
}
