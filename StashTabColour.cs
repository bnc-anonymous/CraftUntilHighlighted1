using System;
using ExileCore;
using ExileCore.PoEMemory;
using SharpDX;
using Color = SharpDX.Color;

namespace CraftUntilHighlighted;

/// <summary>
/// The open stash tab's colour as the game draws it, for the augment boxes' outline: the background of the tab's entry in
/// the stash's tab list, the element at TabListPath below StashElement, then the tab's visible index, then child 0.
///
/// Read off dumps of 2026-11-13: the open "Currency" tab's entry was drawn #FF7C5436 at the game's default colour and
/// #FF0000FF once the player gave the tab a custom colour (00:46:45 and 00:46:58), and tabs coloured #638000 that were not
/// open had #B9638000. The entry gives the default colour as the brown the game shows, which the server's record of the
/// tab does not: it reads Color 0 for a tab at the default (00:35). AutoExpedition takes its tablet zones' colour from that
/// server record (ServerData.PlayerStashTabs) in PoE2; here the list read empty after a colour change (00:46: 0 entries,
/// where the 00:35 dumps had found the tab in it), so it is not used.
///
/// An entry that cannot be read, or reads fully transparent, gives DefaultTabColour. Read again when the open tab
/// changes, and otherwise at most once every RereadEvery, so a new colour shows within that.
/// </summary>
internal static class StashTabColour
{
    /// <summary>
    /// Child indices from StashElement to the stash's tab list, which holds one child per tab in visible-index order: 535
    /// children with 535 tabs (TotalStashes), the entry for visible index 7 at child 7 (dumps 2026-11-13).
    /// </summary>
    private static readonly int[] TabListPath = [2, 0, 0, 1, 4, 2];

    /// <summary>How often the open tab's colour is read again while the same tab stays open. Chosen.</summary>
    private static readonly TimeSpan RereadEvery = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The colour of a tab at the game's default, as the open tab's entry in the tab list was drawn with it (dumps
    /// 2026-11-13 00:35 to 00:46). Used when the entry cannot be read.
    /// </summary>
    private static readonly Color DefaultTabColour = Colours.FromArgb(255, 0x7C, 0x54, 0x36);

    private static int _readForIndex = -1;
    private static DateTime _readAt;
    private static Color? _colour;

    /// <summary>The open tab's colour, or the default colour when its entry cannot be read. With the stash closed, null.</summary>
    public static Color? Of(GameController gc)
    {
        var visible = Safe.Read(gc, static g =>
            g.IngameState.IngameUi.StashElement is { IsVisible: true } stash ? stash.IndexVisibleStash : -1, -1);

        if (visible < 0)
            return null;

        if (visible == _readForIndex && DateTime.UtcNow - _readAt < RereadEvery)
            return _colour;

        _readForIndex = visible;
        _readAt = DateTime.UtcNow;

        var drawn = Safe.Read(EntryOf(gc, visible), static e => e?.BgColor ?? default, default(ColorBGRA));

        _colour = drawn.A == 0 ? DefaultTabColour : Colours.FromArgb(255, drawn.R, drawn.G, drawn.B);
        return _colour;
    }

    /// <summary>The open tab's entry in the stash's tab list, whose background is the tab's colour, or null. See the class summary.</summary>
    public static Element EntryOf(GameController gc, int visible) =>
        visible < 0
            ? null
            : Safe.Read((Gc: gc, Visible: visible), static x =>
                ElementPath.ChildAt(x.Gc.IngameState.IngameUi.StashElement, [.. TabListPath, x.Visible, 0]), null);
}
