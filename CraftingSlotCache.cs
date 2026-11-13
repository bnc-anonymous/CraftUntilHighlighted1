using System;
using ExileCore;
using RectangleF = SharpDX.RectangleF;

namespace CraftUntilHighlighted;

/// <summary>
/// What the craft square needs between runs - where it goes and whether the item is highlighted - read at most once per
/// RefreshInterval instead of every frame. Drawing and the idle craft loop use it; a run reads the item itself every tick
/// and does not use this.
///
/// Each refresh first looks at the game's input focus, for the search box's fallback should a patch move it (StashSearch), then reads
/// only as far as it needs: with the stash closed, whether it is open; on another tab, the tab type; on a currency or
/// essence tab, the item, the search text, the selected currency and the highlight. The cached square is empty when no
/// square should be drawn: no crafting item, no search in the search box (or the box not found), or no currency from
/// the Allowed currencies list right-clicked for use (see SelectedCurrency).
///
/// Nothing a click depends on is taken from here. CraftLoop reads everything afresh before it starts a run.
/// </summary>
internal sealed class CraftingSlotCache
{
    /// <summary>
    /// How stale the cache may get. Chosen, not measured: 133 ms is 8 frames at 60 fps, short enough that the square
    /// appearing or changing colour after a search or a tab change looks immediate.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(133);

    private DateTime _refreshedAt = DateTime.MinValue;

    /// <summary>The craft square to draw, or empty when none should be drawn.</summary>
    public RectangleF Square { get; private set; }

    /// <summary>Whether the stash search highlighted the crafting item at the last refresh.</summary>
    public bool Highlighted { get; private set; }

    /// <summary>The crafting item's rectangle at the last refresh, or empty when there is none. For AugmentBoxes.</summary>
    public RectangleF ItemRect { get; private set; }

    /// <summary>
    /// The metadata path of the currency right-clicked for use at the last refresh, or empty: read only with a crafting
    /// item found. For AugmentBoxes, which shows for an Orb of Alteration whether or not it is on Allowed currencies.
    /// </summary>
    public string SelectedCurrencyPath { get; private set; } = "";

    public void RefreshIfDue(GameController gc, CraftUntilHighlightedSettings settings)
    {
        if (DateTime.UtcNow - _refreshedAt >= RefreshInterval)
            Refresh(gc, settings);
    }

    /// <summary>Makes the next RefreshIfDue refresh, so the square reflects a run that has just ended.</summary>
    public void Expire() => _refreshedAt = DateTime.MinValue;

    private void Refresh(GameController gc, CraftUntilHighlightedSettings settings)
    {
        _refreshedAt = DateTime.UtcNow;
        Square = default;
        Highlighted = false;
        ItemRect = default;
        SelectedCurrencyPath = "";

        StashSearch.LearnFromFocus(gc);

        var item = CraftingSlot.FindBetweenRuns(gc);

        if (item == null)
            return;

        var selected = SelectedCurrency.BaseItemTypeIfSelected(gc);

        ItemRect = Safe.Read(item, static i => i.GetClientRectCache, default(RectangleF));
        SelectedCurrencyPath = Safe.Read(selected, static t => t?.Metadata ?? "", "");

        if (!StashSearch.HasSearch(gc) || !SelectedCurrency.IsAllowed(selected, settings))
            return;

        Square = CraftingSlot.CraftSquareOf(ItemRect, settings.Display.CraftSquareSizePercent.Value);
        Highlighted = CraftingSlot.SearchHighlightOf(item).Highlighted;
    }
}
