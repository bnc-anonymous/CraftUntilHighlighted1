using ExileCore;
using ExileCore.PoEMemory;

namespace CraftUntilHighlighted;

/// <summary>
/// The stash search box: where its text field is, and what is typed in it.
///
/// ExileCore names no stash search box (checked against ExileApi's ExileCore.dll, 2026-11-12: StashElement has no search
/// member, and SearchBarElement is used only by the expedition vendor). The text field is read at KnownFieldPath, a fixed
/// path of child indices below StashElement read in DevTree, as the PoE2 version reads its own. Its Element.Text is the
/// search: a PoE1 search box's typed text read as Element.Text in CurrencyExchangeFlipper's currency picker.
///
/// Should a patch move the field, so that nothing visible is at KnownFieldPath, the field is looked for the way the player
/// reaches it: while the game's input focus, IngameState.FocusedInputElement, is on a visible element below StashElement,
/// that element's path is kept as LearnedFieldPath and read instead. Clicking into the box then finds it. The learned path
/// is kept for the session and never saved, because one saved across a patch could name some other element with text in
/// it, and a search read as present while the real box is empty would start a run that nothing ever highlights. Either
/// path counts only while the element at it is visible (FieldOf).
/// </summary>
internal static class StashSearch
{
    /// <summary>
    /// Child indices from StashElement to the search box's text field. Read in DevTree, 2026-11-12, as
    /// "(OpenLeftPanel/StashElement)49->3->1->0": StashElement is child 49 of its parent, and the field is at [3,1,0]
    /// below it. The PoE2 version's field is at [3,3,0].
    /// </summary>
    public static readonly int[] KnownFieldPath = [3, 1, 0];

    /// <summary>The focused element's address at the last look that reached a conclusion, so a focus that stays put is walked once.</summary>
    private static long _focusLookedAt;

    /// <summary>
    /// Where the last input to have the focus below StashElement is, as child indices; null until one has had it this
    /// session. Read only when nothing visible is at KnownFieldPath.
    /// </summary>
    public static int[] LearnedFieldPath { get; private set; }

    /// <summary>
    /// Takes the focused input element as the search field's fallback when it lies below StashElement, the stash being
    /// open. Costs one read while the focus stays on the element it was on at the last look, and a walk up its parents
    /// when it moves.
    /// </summary>
    public static void LearnFromFocus(GameController gc)
    {
        var focused = Safe.Read(gc, static g => g.IngameState.FocusedInputElement, null);
        var address = Safe.Read(focused, static f => f?.Address ?? 0L, 0L);

        if (address == 0L)
        {
            _focusLookedAt = 0L;
            return;
        }

        if (address == _focusLookedAt)
            return;

        var stash = Safe.Read(gc, static g => g.IngameState.IngameUi.StashElement, null);

        // Not concluded while the stash is closed: the same focus is looked at again once it opens.
        if (!Safe.Read(stash, static s => s.IsVisible, false))
            return;

        _focusLookedAt = address;

        if (!Safe.Read(focused, static f => f.IsVisible, false))
            return;

        if (Safe.Read((Stash: stash, Focused: focused), static x => ElementPath.PathFrom(x.Stash, x.Focused), null) is
            { Length: > 0 } path)
            LearnedFieldPath = path;
    }

    /// <summary>
    /// The search box's text field: the element at KnownFieldPath, or failing that at LearnedFieldPath; null when neither
    /// has a visible element. Visible, because a closed stash's elements are hidden, and a learned input in a panel the
    /// stash opens and closes again, such as another tab's own filter, is hidden once that panel goes.
    /// </summary>
    public static Element FieldOf(GameController gc) => VisibleAt(gc, KnownFieldPath) ?? VisibleAt(gc, LearnedFieldPath);

    /// <summary>The element at a path below StashElement if it is visible, otherwise null.</summary>
    public static Element VisibleAt(GameController gc, int[] path)
    {
        if (path == null)
            return null;

        var element = Safe.Read((Gc: gc, Path: path), static x =>
            ElementPath.ChildAt(x.Gc.IngameState.IngameUi.StashElement, x.Path), null);

        return Safe.Read(element, static e => e is { IsVisible: true }, false) ? element : null;
    }

    /// <summary>The text in the stash search box, or null when the box cannot be found or read.</summary>
    public static string TextOf(GameController gc)
    {
        var field = FieldOf(gc);

        return field == null ? null : Safe.Read(field, static f => f.Text ?? "", null);
    }

    /// <summary>
    /// Whether the search box holds a search. Unknown and blank both count as none: a run with nothing to match applies
    /// orbs until something else stops it.
    /// </summary>
    public static bool HasSearch(GameController gc) => !string.IsNullOrWhiteSpace(TextOf(gc));

    /// <summary>
    /// The element inside which the game's input focus is not typed input for this plugin (CraftLoop.TryStart), or 0 when
    /// the search box is not found: the stash's child holding the field at KnownFieldPath, [3], the whole search bar, as
    /// the PoE2 version names its own search bar [3], so focus on another part of the bar does not block a run either. For
    /// a field found only by the focus, the field itself, since the first step of a learned path can be a far larger panel.
    /// </summary>
    public static long SearchBarAddressOf(GameController gc)
    {
        if (VisibleAt(gc, KnownFieldPath) != null)
            return Safe.Read(VisibleAt(gc, [KnownFieldPath[0]]), static e => e?.Address ?? 0L, 0L);

        return Safe.Read(VisibleAt(gc, LearnedFieldPath), static e => e?.Address ?? 0L, 0L);
    }
}
