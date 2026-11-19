using System;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using Color = SharpDX.Color;
using Graphics = ExileCore.Graphics;
using RectangleF = SharpDX.RectangleF;

namespace CraftUntilHighlighted;

/// <summary>
/// Two boxes stacked to the right of the crafting item, bottom-aligned with it, while an Orb of Alteration is selected
/// (or an Orb of Augmentation with Alt held, which is what Alt makes of a selected alteration) and the stash search box
/// holds a search: Augment on empty prefix above Augment on empty suffix. Not without a search, because the boxes act
/// only on a run and a run needs one, so with the box empty they would be a switch that does nothing. Moving the cursor
/// into one switches it, dim (off) to bright (on) or back; it switches again only once the cursor has left and come back
/// in. Hover rather than a click, because a click on the overlay goes through to the stash under it. Each starts off,
/// every session; neither is saved. Drawn only with Enable augment boxes on (CraftingSettings.EnableAugmentBoxes).
///
/// While either is on, a run of Orbs of Alteration applies an Orb of Augmentation after an alteration that leaves the
/// item magic, not highlighted, and with that box's slot empty (WantsAugment; CraftLoop does it). Styled as
/// AutoExpedition's tablet action zones for PoE2: a translucent black fill, a two-pixel inset outline in the open stash
/// tab's own colour as the game draws the tab's outline (StashTabColour; grey on a tab with none), and centred text, each
/// brighter when on.
/// </summary>
internal sealed class AugmentBoxes
{
    public const string AlterationPath = "Metadata/Items/Currency/CurrencyRerollMagic";

    public const string AugmentationPath = "Metadata/Items/Currency/CurrencyAddModToMagic";

    private const string PrefixLabel = "Augment on empty prefix";
    private const string SuffixLabel = "Augment on empty suffix";

    /// <summary>
    /// Between the crafting item's right edge and the boxes, between the two boxes, and round each label. Chosen; the
    /// first set by eye in game, 2026-11-13.
    /// </summary>
    private const float GapFromItem = 11f;

    private const float GapBetweenBoxes = 4f;

    private const float LabelPadding = 6f;

    /// <summary>AutoExpedition's zone fill, dim and lit (alpha 90 and 130 on black).</summary>
    private static readonly Color DimFill = Colours.FromArgb(90, 0, 0, 0);

    private static readonly Color BrightFill = Colours.FromArgb(130, 0, 0, 0);

    /// <summary>The outline's colour on a tab with no colour of its own: AutoExpedition's grey 120.</summary>
    private static readonly Color UncolouredTab = Grey(120f);

    /// <summary>
    /// The outline's outer and inner pixel as shares of the tab's colour: AutoExpedition's, read off the game's own outline
    /// on one PoE2 tab (#638000 drawn as #324400 outside and #5E7B00 inside), so near, not exact. Half as bright again
    /// when on, as AutoExpedition lights a zone.
    /// </summary>
    private const float OutlineOuterShare = 0.52f;

    private const float OutlineInnerShare = 0.95f;

    private const float OnBrightness = 1.5f;

    /// <summary>The open stash tab's colour at the last Tick, or null for none (StashTabColour).</summary>
    private Color? _tabColour;

    /// <summary>AutoExpedition's dim text (grey 160), and white when on.</summary>
    private static readonly Color DimText = Grey(160f);

    private static readonly Color BrightText = Colours.FromArgb(255, 255, 255, 255);

    /// <summary>The wider label's size in the HUD font, measured in Draw for Tick, which has no Graphics; zero before.</summary>
    private Vector2 _labelSize;

    private bool _shownLastTick;
    private bool _cursorInPrefixBox;
    private bool _cursorInSuffixBox;

    public bool AugmentOnEmptyPrefix { get; private set; }

    public bool AugmentOnEmptySuffix { get; private set; }

    /// <summary>Whether the boxes are drawn, as at the last Tick.</summary>
    public bool Shown { get; private set; }

    public RectangleF PrefixBox { get; private set; }

    public RectangleF SuffixBox { get; private set; }

    /// <summary>
    /// Places the boxes beside the crafting item and switches the one the cursor has just moved into. itemRect is the
    /// crafting item's, empty for none; selectedPath the selected currency's metadata, empty for none. When the boxes
    /// appear with the cursor already in one, that one does not switch until the cursor leaves it and comes back.
    ///
    /// The search is read here on every tick, last so it is read only when the boxes would otherwise show, rather than
    /// taken from CraftingSlotCache: a run does not refresh the cache, and a search emptied mid-run leaves the run going,
    /// at the highlighted gate, so a cached search would keep the boxes up until the run ended.
    /// </summary>
    public void Tick(GameController gc, CraftUntilHighlightedSettings settings, RectangleF itemRect, string selectedPath)
    {
        var forAlteration = selectedPath == AlterationPath ||
                            selectedPath == AugmentationPath && Safe.Read(static () => Input.IsKeyDown(Keys.Menu), false);

        Shown = settings.Crafting.EnableAugmentBoxes && itemRect.Width > 0f && _labelSize.X > 0f && forAlteration &&
                StashSearch.HasSearch(gc);

        if (!Shown)
        {
            _shownLastTick = false;
            return;
        }

        _tabColour = StashTabColour.Of(gc);

        var width = _labelSize.X + 2f * LabelPadding;
        var height = _labelSize.Y + 2f * LabelPadding;

        SuffixBox = new RectangleF(itemRect.Right + GapFromItem, itemRect.Bottom - height, width, height);
        PrefixBox = new RectangleF(SuffixBox.X, SuffixBox.Top - GapBetweenBoxes - height, width, height);

        var mouse = CraftingSlot.MouseOf(gc);
        var inPrefix = CraftingSlot.Contains(PrefixBox, mouse);
        var inSuffix = CraftingSlot.Contains(SuffixBox, mouse);

        if (_shownLastTick && inPrefix && !_cursorInPrefixBox)
            AugmentOnEmptyPrefix = !AugmentOnEmptyPrefix;

        if (_shownLastTick && inSuffix && !_cursorInSuffixBox)
            AugmentOnEmptySuffix = !AugmentOnEmptySuffix;

        _cursorInPrefixBox = inPrefix;
        _cursorInSuffixBox = inSuffix;
        _shownLastTick = true;
    }

    /// <summary>
    /// Whether the next orb of a run should be an Orb of Augmentation: Enable augment boxes is on, the run is of Orbs of
    /// Alteration, and the item is magic with room for an affix and the slot of a box that is on empty. Read off the item
    /// as it is now (ItemAffixes); an item that cannot be read gets no augmentation.
    /// </summary>
    public bool WantsAugment(NormalInventoryItem item, string runCurrencyPath, CraftUntilHighlightedSettings settings)
    {
        if (!settings.Crafting.EnableAugmentBoxes || !(AugmentOnEmptyPrefix || AugmentOnEmptySuffix) ||
            runCurrencyPath != AlterationPath)
            return false;

        if (ItemAffixes.OfMagicItem(item) is not { } counts || counts.Prefixes + counts.Suffixes >= 2)
            return false;

        return AugmentOnEmptyPrefix && counts.Prefixes == 0 || AugmentOnEmptySuffix && counts.Suffixes == 0;
    }

    /// <summary>
    /// Draws the boxes as the last Tick placed them, reading nothing of the game. A box under the item description panel
    /// is not drawn, as the craft square is not. Measures the labels first, for the next Tick.
    /// </summary>
    public void Draw(Graphics graphics, HoveredItemTooltip tooltip)
    {
        if (_labelSize.X <= 0f || Shown)
            _labelSize = Vector2.Max(graphics.MeasureText(PrefixLabel), graphics.MeasureText(SuffixLabel));

        if (!Shown)
            return;

        DrawBox(graphics, tooltip, PrefixBox, PrefixLabel, AugmentOnEmptyPrefix, _tabColour);
        DrawBox(graphics, tooltip, SuffixBox, SuffixLabel, AugmentOnEmptySuffix, _tabColour);
    }

    private static void DrawBox(Graphics graphics, HoveredItemTooltip tooltip, RectangleF box, string label, bool on,
        Color? tabColour)
    {
        if (tooltip.Covers(box))
            return;

        var colour = tabColour ?? UncolouredTab;
        var brightness = on ? OnBrightness : 1f;
        var outer = Shaded(colour, OutlineOuterShare * brightness);
        var inner = Shaded(colour, OutlineInnerShare * brightness);

        graphics.DrawBox(box, on ? BrightFill : DimFill);
        DrawOutline(graphics, box, outer);
        DrawOutline(graphics, new RectangleF(box.Left + 1f, box.Top + 1f, box.Width - 2f, box.Height - 2f), inner);

        var size = graphics.MeasureText(label);

        graphics.DrawText(label, new Vector2(box.Left + (box.Width - size.X) / 2f, box.Top + (box.Height - size.Y) / 2f),
            on ? BrightText : DimText);
    }

    /// <summary>A one-pixel outline just inside a rectangle, AutoExpedition's DrawInsetBorder with its one-pixel gap.</summary>
    private static void DrawOutline(Graphics graphics, RectangleF rect, Color colour)
    {
        var (left, top, right, bottom) = (rect.Left + 1f, rect.Top + 1f, rect.Right - 1f, rect.Bottom - 1f);

        if (right - left <= 2f || bottom - top <= 2f)
            return;

        graphics.DrawBox(new Vector2(left, top), new Vector2(right, top + 1f), colour);
        graphics.DrawBox(new Vector2(left, bottom - 1f), new Vector2(right, bottom), colour);
        graphics.DrawBox(new Vector2(left, top + 1f), new Vector2(left + 1f, bottom - 1f), colour);
        graphics.DrawBox(new Vector2(right - 1f, top + 1f), new Vector2(right, bottom - 1f), colour);
    }

    private static Color Grey(float level)
    {
        var channel = (int)Math.Clamp(MathF.Round(level), 0f, 255f);

        return Colours.FromArgb(255, channel, channel, channel);
    }

    /// <summary>A colour with each channel times a share, kept within 0 to 255: AutoExpedition's Shaded.</summary>
    private static Color Shaded(Color colour, float share)
    {
        static int Channel(byte value, float share) => (int)Math.Clamp(MathF.Round(value * share), 0f, 255f);

        return Colours.FromArgb(255, Channel(colour.R, share), Channel(colour.G, share), Channel(colour.B, share));
    }
}
