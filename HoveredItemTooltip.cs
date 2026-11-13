using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using RectangleF = SharpDX.RectangleF;

namespace CraftUntilHighlighted;

/// <summary>
/// Where the item description panel the game is showing sits on screen, so the craft square is not drawn over it.
///
/// The panel is the hovered item's Tooltip, as NinjaPricer for PoE1 reads it. The hovered item is read from
/// UIHoverElement rather than UIHover: in PoE2, UIHover read nothing while Alt was held for the advanced description,
/// though the panel was still shown, and Artisan found in PoE1 too that holding Alt changes what UIHover names. While Alt
/// is held the game keeps the description of the item it showed open wherever the cursor goes, including over other
/// items, while UIHoverElement follows the cursor, so the item shown when Alt went down is kept until Alt is let go.
/// Read in NinjaPricer for PoE2 with the stash open, 2026-10-27; not yet seen in PoE1. The panel element itself still read
/// visible after the hover ended, so its own IsVisible cannot say whether it is drawn.
/// </summary>
internal sealed class HoveredItemTooltip
{
    private NormalInventoryItem _itemShownWhileAltHeld;

    /// <summary>The panel's rectangle at the last Refresh, or empty when no item description is shown.</summary>
    public RectangleF Rect { get; private set; }

    public void Refresh(GameController gc)
    {
        var altHeld = Input.IsKeyDown(Keys.Menu);
        var item = altHeld && _itemShownWhileAltHeld != null ? _itemShownWhileAltHeld : HoveredItemOf(gc);

        _itemShownWhileAltHeld = altHeld ? item : null;
        Rect = Safe.Read(item, static i => i?.Tooltip is { Address: not 0, IsVisible: true } tooltip
            ? tooltip.GetClientRectCache
            : default, default(RectangleF));
    }

    public void Clear()
    {
        _itemShownWhileAltHeld = null;
        Rect = default;
    }

    /// <summary>Whether a rectangle overlaps the item description panel shown at the last Refresh.</summary>
    public bool Covers(RectangleF rect) => Rect.Width > 0f && Rect.Intersects(rect);

    /// <summary>
    /// The item under the cursor from UIHoverElement, or from UIHover when UIHoverElement names no item: UIHover is what
    /// NinjaPricer for PoE1 reads, and which of the two names the item in PoE1 is not yet measured.
    /// </summary>
    private static NormalInventoryItem HoveredItemOf(GameController gc) =>
        ItemOrNull(Safe.Read(gc, static g => g.IngameState.UIHoverElement?.AsObject<NormalInventoryItem>(), null)) ??
        ItemOrNull(Safe.Read(gc, static g => g.IngameState.UIHover?.AsObject<NormalInventoryItem>(), null));

    private static NormalInventoryItem ItemOrNull(NormalInventoryItem item) =>
        Safe.Read(item, static i => i != null && i.Address != 0 && i.Item is { Address: not 0, IsValid: true }, false)
            ? item
            : null;
}
