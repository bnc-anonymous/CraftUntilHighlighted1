using System;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using RectangleF = SharpDX.RectangleF;

namespace CraftUntilHighlighted;

/// <summary>
/// Applies the selected orb to the crafting item until the stash search highlights it.
///
/// A run starts when every StartBlocker condition is clear: the plugin is enabled, ExileInput is running, the item is in
/// the crafting slot and not highlighted, the search box has a search, a currency on the Allowed currencies list is
/// right-clicked for use (SelectedCurrency), the cursor is in the craft square, and the cursor has been outside the square
/// since the last run ended. The first run of a new install waits out the first-use countdown before it starts. A run
/// takes the cursor through ExileInput, holds shift, waits a drawn shift lead, and clicks. Each click waits for the item's
/// ItemFingerprint to change - ExileCore has no ItemVersion, which is what the PoE2 version waits on - then checks the
/// highlight, waits a drawn item changed delay, and clicks again.
///
/// The cursor is never moved, and the only stop the mouse makes is the craft square: the cursor leaving it ends the run
/// and lets go of shift. Movement inside it does not. ExileInput's own "player moved the mouse" stop never fires on its
/// position check, which needs a move sent (MousePointer.Interfered), but on the hardware-input boards the mouse board
/// reports the player's hand moving, or a button, wherever the cursor is, and ExileInput stops the holder for that. So
/// each run asks ExileInput to ignore the player's mouse (IgnorePlayerMouse), renewed every tick. That is also what lets
/// the player click by hand while shift is held, as the highlighted gate below relies on. The mouse board still cancels
/// its own click in progress when the player's hand moves (ArbitrationPolicy.Interrupt), so a click that changed nothing
/// while the cursor moved is sent again rather than ending the run.
/// A key pressed while ExileInput holds shift stops the run too: ExileInput swallows it and reports PlayerPressedAKey.
/// Alt is the exception. The player holds it to read the item's advanced description, often once the square turns
/// purple, so each run asks ExileInput to let it through (AllowPlayerKeys), and the next click waits while it is down.
/// The run renews its claim on the cursor every tick, since ExileInput takes it back, letting go of shift, after five
/// seconds without a call that renews it, and the highlighted gate makes none on its own.
///
/// Never clicks without an allowed currency selected, checked again before every click: a shift-click on the item with
/// nothing selected moves the item out of the slot.
/// </summary>
internal sealed class CraftLoop
{
    private enum Phase
    {
        Idle,
        FirstUseCountdown,
        ShiftLead,
        AwaitingItemChange,
        ItemChangedDelay,
        HighlightedGate,

        /// <summary>Alt is held for an Orb of Augmentation, waiting for the cursor to show one. See ClickWhenSafe.</summary>
        AwaitingAugmentationSelected,
    }

    private static readonly Random Pick = new();

    /// <summary>
    /// Range of the time between pressing shift and a run's first click; each run draws a value in it. Chosen, not
    /// measured: long enough for the game to see shift held before the click.
    /// </summary>
    private const int ShiftLeadMinMs = 100;

    private const int ShiftLeadMaxMs = 200;

    /// <summary>How long the first-use countdown runs before the first run of a new install starts. Chosen.</summary>
    private static readonly TimeSpan FirstUseCountdownLength = TimeSpan.FromSeconds(5);

    private const string FirstUseMessage = "CraftUntilHighlighted: First time use, about to use currency...";

    /// <summary>
    /// How long each IgnorePlayerMouse asks ExileInput to ignore the player's mouse: ExileInput's most (MouseBlock.MostMs).
    /// Renewed every tick of a run, so it lapses only if the HUD stalls this long; releasing the cursor ends it.
    /// </summary>
    private const int PlayerMouseIgnoredMs = 1000;

    private readonly ExileInputClient _input;
    private readonly CraftingSlotCache _cache;
    private readonly AugmentBoxes _augmentBoxes;

    private Phase _phase = Phase.Idle;
    private DateTime _phaseEndsAt;
    private DateTime _clickedAt;
    private ItemFingerprint _fingerprintBeforeClick;
    private int _orbsThisRun;

    /// <summary>
    /// The metadata path of the currency the run started with. Every click but an augment wants it selected, so a currency
    /// still shown from an augment, or anything else, is never clicked in its place.
    /// </summary>
    private string _runCurrencyPath = "";

    /// <summary>
    /// Whether the run holds Left Alt for an Orb of Augmentation: from just before the augment click until the item is
    /// seen to change, as Artisan lets go once the application is confirmed. See ClickWhenSafe.
    /// </summary>
    private bool _altHeldForAugment;

    /// <summary>Where the game had the cursor at the last click, and whether it has moved since. See TickRunning.</summary>
    private System.Numerics.Vector2 _mouseAtClick;

    private bool _mouseMovedSinceClick;

    /// <summary>The craft square as it was when the run started, used for the whole run. See TickRunning.</summary>
    private RectangleF _runSquare;

    /// <summary>Whether the cursor has been outside the square since the last run ended, so a run is not restarted in place.</summary>
    private bool _cursorLeftSinceLastRun;

    /// <summary>When the start conditions are next read while the cursor waits in the square. See TickIdle.</summary>
    private DateTime _nextStartCheckAt;

    /// <summary>Why the last start check did not start a run; empty when there was nothing worth saying.</summary>
    private string _startBlocker = "";

    public CraftLoop(ExileInputClient input, CraftingSlotCache cache, AugmentBoxes augmentBoxes)
    {
        _input = input;
        _cache = cache;
        _augmentBoxes = augmentBoxes;
    }

    public bool Running => _phase != Phase.Idle;

    /// <summary>The craft square the running run uses; empty when no run is running.</summary>
    public RectangleF RunSquare => Running ? _runSquare : default;

    /// <summary>Whether a run is holding shift on a highlighted item, waiting for the cursor to leave the square.</summary>
    public bool RunHighlighted => _phase == Phase.HighlightedGate;

    /// <summary>Whether the cursor was in the craft square at the last tick; false while no square is drawn.</summary>
    public bool CursorInSquare { get; private set; }

    /// <summary>The share of the first-use countdown still to run, from 1 to 0, or negative when it is not running.</summary>
    public double FirstUseCountdownLeft => _phase == Phase.FirstUseCountdown
        ? Math.Clamp((_phaseEndsAt - DateTime.UtcNow).TotalMilliseconds / FirstUseCountdownLength.TotalMilliseconds, 0d, 1d)
        : -1d;

    public void Tick(GameController gc, CraftUntilHighlightedSettings settings)
    {
        if (_phase == Phase.Idle)
            TickIdle(gc, settings);
        else
            TickRunning(gc, settings);
    }

    /// <summary>
    /// Between runs. The cursor is read every tick while the cached square is drawn, so the square's hover colour and a
    /// run's start follow the cursor at once; with no square drawn nothing is read. The start conditions are read afresh
    /// when the cursor enters the square, and again each CraftingSlotCache.RefreshInterval while it stays in it without
    /// starting, with the last reason kept on screen in between.
    /// </summary>
    private void TickIdle(GameController gc, CraftUntilHighlightedSettings settings)
    {
        var square = _cache.Square;
        var wasInSquare = CursorInSquare;

        CursorInSquare = square.Width > 0f && CraftingSlot.Contains(square, CraftingSlot.MouseOf(gc));

        if (!CursorInSquare)
        {
            _cursorLeftSinceLastRun = true;
            return;
        }

        var now = DateTime.UtcNow;

        if (!wasInSquare || now >= _nextStartCheckAt)
        {
            _nextStartCheckAt = now + CraftingSlotCache.RefreshInterval;

            // The cursor not having left the square since the last run is not said: the message the run ended with is
            // showing, and saying this would replace it.
            _startBlocker = !_cursorLeftSinceLastRun ? "" : TryStart(gc, settings) ?? "";
        }

        if (_phase == Phase.Idle && _startBlocker.Length > 0)
            CursorMessage.ShowWhile(CursorMessage.Kind.Error, _startBlocker);
    }

    /// <summary>
    /// During a run, every tick: the cursor, ExileInput's stop, and whatever the phase waits on. A delay reads nothing
    /// else until it ends.
    ///
    /// A run uses the square it started with, not one read from the item each tick. The item element is rebuilt when an
    /// orb lands, and in PoE2 the rebuilt element could read as missing or with a rectangle somewhere else for a moment;
    /// either moved the square away from the cursor and stopped the run as "the cursor left the square" with the cursor
    /// still inside it (PoE2 run of 2026-10-26 01:42:56). The slot does not move while the stash is open.
    /// </summary>
    private void TickRunning(GameController gc, CraftUntilHighlightedSettings settings)
    {
        var mouse = CraftingSlot.MouseOf(gc);

        CursorInSquare = CraftingSlot.Contains(_runSquare, mouse);
        _mouseMovedSinceClick |= mouse != _mouseAtClick;

        if (_phase == Phase.FirstUseCountdown)
        {
            TickFirstUseCountdown(gc, settings);
            return;
        }

        // Every phase of a run ends on these.
        if (!CursorInSquare)
        {
            if (_phase == Phase.HighlightedGate)
                Stop(CursorMessage.Kind.Success, $"Highlighted: {_orbsThisRun} orbs");
            else
                Stop(CursorMessage.Kind.Info, $"Stopped: {_orbsThisRun} orbs");
            return;
        }

        if (_input.Stopped() is { Length: > 0 } stopped)
        {
            Stop(CursorMessage.Kind.Error, $"ExileInput stopped: {stopped}");
            return;
        }

        // Renews the claim, so shift stays held through a long wait at the highlighted gate. A refusal now is ExileInput
        // stopping this run, the game having lost focus, say, and Stopped says why.
        if (!_input.Take())
        {
            Stop(CursorMessage.Kind.Error, $"ExileInput stopped: {(_input.Stopped() is { Length: > 0 } why ? why : "cursor taken")}");
            return;
        }

        // The player's hand on the mouse is not a stop while the cursor stays in the square. See the class summary.
        _input.IgnorePlayerMouse(PlayerMouseIgnoredMs, false);

        var crafting = settings.Crafting;

        switch (_phase)
        {
            case Phase.ShiftLead:
            case Phase.ItemChangedDelay:
                if (DateTime.UtcNow < _phaseEndsAt)
                    return;

                // The player reading the advanced description: no click until Alt is let go, and the click's timeout
                // counts from then. A click with Alt down is a different click, and UIHover changes while it is held.
                // Not the run's own Alt, held for an augment that is being sent again.
                if (!_altHeldForAugment && AltHeld())
                {
                    _phaseEndsAt = DateTime.UtcNow;
                    return;
                }

                ClickWhenSafe(gc, settings, CraftingSlot.Find(gc));
                return;

            case Phase.AwaitingAugmentationSelected:
                // The click goes as soon as the cursor shows the Orb of Augmentation; ClickWhenSafe checks it again.
                if (SelectedPath(gc) == AugmentBoxes.AugmentationPath)
                {
                    ClickWhenSafe(gc, settings, CraftingSlot.Find(gc));
                    return;
                }

                if (DateTime.UtcNow - _phaseEndsAt > TimeSpan.FromMilliseconds(crafting.ItemChangedTimeoutMs.Value))
                    Stop(CursorMessage.Kind.Error, "Alt did not select an Orb of Augmentation");

                return;

            case Phase.AwaitingItemChange:
            {
                // The item element is rebuilt when an orb lands, so it can read as missing for a moment.
                var item = CraftingSlot.Find(gc);

                if (item == null || !_fingerprintBeforeClick.ChangedIn(item))
                {
                    if (DateTime.UtcNow - _clickedAt <= TimeSpan.FromMilliseconds(crafting.ItemChangedTimeoutMs.Value))
                        return;

                    // The cursor moved while waiting: the mouse board may have cancelled the click for the player's hand,
                    // so it is sent again, through the same checks as any click, the highlight first. Held still, a click
                    // that changed nothing means the orbs ran out or do not apply.
                    if (_mouseMovedSinceClick)
                    {
                        CursorMessage.Show(CursorMessage.Kind.Info, "No change - clicking again");
                        _phase = Phase.ItemChangedDelay;
                        _phaseEndsAt = DateTime.UtcNow;
                        return;
                    }

                    Stop(CursorMessage.Kind.Error, ItemDidNotChangeMessage(_orbsThisRun));
                    return;
                }

                // The augment has landed: Alt goes up, and the cursor goes back to the Orb of Alteration, which the next
                // click waits to see (ClickWhenSafe).
                if (_altHeldForAugment)
                {
                    _input.Let(Keys.LMenu);
                    _altHeldForAugment = false;
                }

                _orbsThisRun++;
                AfterItemChanged(settings, item);
                return;
            }

            case Phase.HighlightedGate:
            {
                // Shift is still held. A click of the player's own may roll the highlighted modifier away, in which case
                // the run carries on as if that orb had been its own.
                var item = CraftingSlot.Find(gc);

                if (item != null && !CraftingSlot.SearchHighlightOf(item).Highlighted)
                {
                    CursorMessage.Show(CursorMessage.Kind.Info, "Highlight gone - continuing");
                    BeginItemChangedDelay(crafting);
                }
                else
                {
                    // Kept up while shift is held, so the player knows a click of theirs will apply the orb.
                    CursorMessage.ShowWhile(CursorMessage.Kind.Success, $"Highlighted: {_orbsThisRun} orbs - shift held");
                }

                return;
            }
        }
    }

    /// <summary>
    /// The first run of a new install waits FirstUseCountdownLength with a warning and a bar under the cursor, holding no
    /// input, so a player who did not know what the square does can move away before anything is clicked. Leaving the
    /// square cancels it; once it runs out, Debug > Enable first time use warning is turned off so it is not shown again,
    /// and the run starts.
    /// </summary>
    private void TickFirstUseCountdown(GameController gc, CraftUntilHighlightedSettings settings)
    {
        if (!CursorInSquare)
        {
            _phase = Phase.Idle;
            _cache.Expire();
            CursorMessage.Show(CursorMessage.Kind.Info, "Cancelled");
            return;
        }

        if (DateTime.UtcNow < _phaseEndsAt)
        {
            CursorMessage.ShowWhile(CursorMessage.Kind.Info, FirstUseMessage);
            return;
        }

        settings.Debug.EnableFirstUseWarning.Value = false;
        _phase = Phase.Idle;
        _startBlocker = TryStart(gc, settings) ?? "";
    }

    /// <summary>
    /// Reads the start conditions afresh and starts a run if they are met. Returns null when a run started, otherwise why
    /// not (empty for a reason not worth saying), which TickIdle shows while the cursor stays in the square.
    /// </summary>
    private string TryStart(GameController gc, CraftUntilHighlightedSettings settings)
    {
        var item = CraftingSlot.Find(gc);

        if (StartBlocker(gc, settings, item) is { } blocker)
            return blocker;

        var square = CraftingSlot.CraftSquareOf(Safe.Read(item, static i => i.GetClientRectCache, default(RectangleF)),
            settings.Display.CraftSquareSizePercent.Value);

        if (!CraftingSlot.Contains(square, CraftingSlot.MouseOf(gc)))
            return "";

        if (settings.Debug.EnableFirstUseWarning)
        {
            _runSquare = square;
            _cursorLeftSinceLastRun = false;
            _phase = Phase.FirstUseCountdown;
            _phaseEndsAt = DateTime.UtcNow + FirstUseCountdownLength;
            CursorMessage.ShowWhile(CursorMessage.Kind.Info, FirstUseMessage);
            return null;
        }

        // The text cursor is usually still in the search box after typing the search, which ExileInput treats as the game
        // taking typed input and refuses. Focus in the search box is allowed for this plugin: it sends shift, which types
        // nothing, and clicks on the item, not the box. Focus anywhere else, chat included, still blocks it. Named afresh on
        // every start because the box's element can be rebuilt when the stash is reopened.
        if (StashSearch.SearchBarAddressOf(gc) is var searchBar and not 0L)
            _input.AllowFocusUnder([searchBar]);

        if (!_input.Take())
        {
            var why = _input.Stopped();

            // A refusal with a stop reason (the game taking typed input, say, while the search box still has focus)
            // stands until it is acknowledged, so it is released here and the next tick asks again.
            if (why.Length > 0)
                _input.Release();

            return why.Length > 0 ? $"ExileInput: {why}" : "ExileInput busy";
        }

        // Before shift goes down, so an Alt the player already has down passes too. False from an ExileInput older than
        // bridge version 7, where Alt then stops the run, which the start message below says.
        var altAllowed = _input.AllowPlayerKeys([Keys.Menu]);

        // From the start, so the player's hand during the shift lead does not stop the run either. Renewed every tick.
        _input.IgnorePlayerMouse(PlayerMouseIgnoredMs, false);

        if (!_input.Hold(Keys.LShiftKey))
        {
            _input.Release();
            return "ExileInput would not hold shift";
        }

        _orbsThisRun = 0;
        _runCurrencyPath = SelectedPath(gc);
        _altHeldForAugment = false;
        _runSquare = square;
        _cursorLeftSinceLastRun = false;
        _phase = Phase.ShiftLead;
        _phaseEndsAt = DateTime.UtcNow + DrawMs(ShiftLeadMinMs, ShiftLeadMaxMs);

        if (altAllowed)
            CursorMessage.Show(CursorMessage.Kind.Info, "Crafting");
        else
            CursorMessage.Show(CursorMessage.Kind.Error, "Crafting - update ExileInput, Alt stops runs");

        return null;
    }

    /// <summary>
    /// Why a run cannot start now, or null when it can; empty for a reason not worth saying. Checked in this order.
    /// </summary>
    private string StartBlocker(GameController gc, CraftUntilHighlightedSettings settings, NormalInventoryItem item)
    {
        if (!settings.Enable)
            return "";

        if (item == null)
            return "No crafting item";

        // Not said: the craft square is not drawn without a search, so the cursor is not plainly in it.
        if (!StashSearch.HasSearch(gc))
            return "";

        if (CraftingSlot.SearchHighlightOf(item).Highlighted)
            return "Already highlighted";

        // Not said: the craft square is not drawn without an allowed currency selected either.
        if (!SelectedCurrency.AllowedIsSelected(gc, settings))
            return "";

        if (!_input.Available)
            return "ExileInput not running - install and enable it";

        return null;
    }

    /// <summary>
    /// Clicks if the orb is still selected, the item can be read, and the game says the cursor is over the item; otherwise
    /// waits for that, and stops after the item changed timeout. UIHover can name the old element for a moment after an
    /// orb rebuilds the item.
    ///
    /// The orb is the run's own currency, from the Allowed currencies, unless the augment boxes want an Orb of
    /// Augmentation on the item as it is now (AugmentBoxes.WantsAugment). Then Left Alt is held first, which turns the
    /// selected Orb of Alteration into an Orb of Augmentation for as long as it is down, and the click waits for the cursor
    /// to show the Orb of Augmentation (AwaitingAugmentationSelected) - Artisan's swap, which reads what the cursor became
    /// rather than assuming. The left Alt by name: Artisan found Keys.Menu reaching the game as Ctrl+Alt on a layout with
    /// AltGr. Alt goes up once the augment is seen to land (TickRunning). The highlight is checked before either.
    /// </summary>
    private void ClickWhenSafe(GameController gc, CraftUntilHighlightedSettings settings, NormalInventoryItem item)
    {
        var timeout = TimeSpan.FromMilliseconds(settings.Crafting.ItemChangedTimeoutMs.Value);
        var fingerprint = ItemFingerprint.Of(item);

        string NotReady()
        {
            if (item == null)
                return "No crafting item";

            if (_altHeldForAugment)
            {
                if (SelectedPath(gc) != AugmentBoxes.AugmentationPath)
                    return "No Orb of Augmentation selected";
            }
            else
            {
                if (!SelectedCurrency.AllowedIsSelected(gc, settings))
                    return SelectedCurrency.AnySelected(gc) ? "Currency not allowed" : "No orb selected";

                if (SelectedPath(gc) != _runCurrencyPath)
                    return "Different currency selected";
            }

            // A click whose effect cannot be seen would be followed by another as soon as the timeout allowed it.
            if (!fingerprint.Readable)
                return "Item unreadable";

            return CursorOverItem(gc, item, _altHeldForAugment) ? null : "Cursor not over the item";
        }

        // Checked again here, at the end of the item changed delay, in case the highlight lagged behind the item changing: a match
        // seen only now must not be clicked past.
        if (item != null && CraftingSlot.SearchHighlightOf(item).Highlighted)
        {
            OnHighlighted();
            return;
        }

        if (!_altHeldForAugment && item != null && _augmentBoxes.WantsAugment(item, _runCurrencyPath, settings))
        {
            if (!_input.Hold(Keys.LMenu))
            {
                Stop(CursorMessage.Kind.Error, "ExileInput would not hold Alt");
                return;
            }

            _altHeldForAugment = true;
            _phase = Phase.AwaitingAugmentationSelected;
            _phaseEndsAt = DateTime.UtcNow;
            CursorMessage.Show(CursorMessage.Kind.Info, $"Augmenting: {_orbsThisRun} orbs");
            return;
        }

        if (NotReady() is { } why)
        {
            if (DateTime.UtcNow - _phaseEndsAt > timeout)
                Stop(CursorMessage.Kind.Error, why);

            return;
        }

        _fingerprintBeforeClick = fingerprint;

        // Shift is held through Hold, so the click itself names no modifiers; naming shift here would have the stroke
        // press and release it around the click and drop the orb's selection.
        if (!_input.Click())
        {
            if (DateTime.UtcNow - _phaseEndsAt > timeout)
                Stop(CursorMessage.Kind.Error, "ExileInput refused the click");

            return;
        }

        _clickedAt = DateTime.UtcNow;
        _mouseAtClick = CraftingSlot.MouseOf(gc);
        _mouseMovedSinceClick = false;
        _phase = Phase.AwaitingItemChange;
    }

    /// <summary>
    /// Whether the game says the cursor is over the crafting item: UIHover is the item's element, or an inventory item
    /// whose entity is the item's. The entity comparison is Artisan's (PoE1), which reads UIHover as a NormalInventoryItem
    /// and compares entities; the element comparison is the PoE2 version's.
    ///
    /// UIHover naming nothing is a no, except with altHeld. Artisan found UIHover names nothing while Alt is held, which
    /// failed its check on exactly the click Alt was held for; its answer, kept here, is that naming another item is a no
    /// and naming nothing leaves it to where the cursor is. The cursor is in the run's square, inside the item, or the run
    /// would have stopped (TickRunning).
    /// </summary>
    private static bool CursorOverItem(GameController gc, NormalInventoryItem item, bool altHeld)
    {
        var hovered = Safe.Read(gc, static g => g.IngameState.UIHover, null);
        var hoveredAddress = Safe.Read(hovered, static h => h?.Address ?? 0L, 0L);

        if (hoveredAddress == 0L)
            return altHeld;

        if (hoveredAddress == Safe.Read(item, static i => i.Address, -1L))
            return true;

        var hoveredEntity = Safe.Read(hovered, static h => h.AsObject<NormalInventoryItem>().Item?.Address ?? 0L, 0L);

        return hoveredEntity != 0L && hoveredEntity == Safe.Read(item, static i => i.Item?.Address ?? -1L, -1L);
    }

    private void AfterItemChanged(CraftUntilHighlightedSettings settings, NormalInventoryItem item)
    {
        var crafting = settings.Crafting;

        if (CraftingSlot.SearchHighlightOf(item).Highlighted)
        {
            OnHighlighted();
            return;
        }

        if (crafting.MaximumOrbsPerRun.Value > 0 && _orbsThisRun >= crafting.MaximumOrbsPerRun.Value)
        {
            Stop(CursorMessage.Kind.Info, $"Maximum reached: {_orbsThisRun} orbs");
            return;
        }

        BeginItemChangedDelay(crafting);
        CursorMessage.Show(CursorMessage.Kind.Info, $"Crafting: {_orbsThisRun} orbs");
    }

    /// <summary>
    /// Keeps shift held, with the orb still selected, until the cursor leaves the craft square, so the player can click
    /// to apply the orb again by hand; if that removes the highlight, the run carries on.
    /// </summary>
    private void OnHighlighted()
    {
        _phase = Phase.HighlightedGate;
        CursorMessage.ShowWhile(CursorMessage.Kind.Success, $"Highlighted: {_orbsThisRun} orbs - shift held");
    }

    private void BeginItemChangedDelay(CraftingSettings crafting)
    {
        _phase = Phase.ItemChangedDelay;
        _phaseEndsAt = DateTime.UtcNow + DrawMs(crafting.ItemChangedMinDelayMs.Value, crafting.ItemChangedMaxDelayMs.Value);
    }

    /// <summary>
    /// Ends the run, letting go of shift and the cursor, and says why at the cursor and in the log. Does nothing when no
    /// run is going, so a stop from outside cannot replace the message a run ended with.
    /// </summary>
    public void Stop(CursorMessage.Kind kind, string why)
    {
        if (_phase == Phase.Idle)
            return;

        // The first-use countdown holds no input, so there is nothing to let go of. Release lets go of shift and, mid
        // augment, Alt.
        if (_phase != Phase.FirstUseCountdown)
            Safe.Read(_input, static i => i.Release(), false);
        DebugWindow.LogMsg($"[CraftUntilHighlighted] {why}", 5f);
        CursorMessage.Show(kind, why);

        _phase = Phase.Idle;
        _altHeldForAugment = false;
        _cache.Expire();
    }

    /// <summary>Whether Alt is down, the player's or the run's own (see _altHeldForAugment).</summary>
    private static bool AltHeld() => Safe.Read(static () => Input.IsKeyDown(Keys.Menu), false);

    /// <summary>The metadata path of the currency right-clicked for use, or empty for none (SelectedCurrency).</summary>
    private static string SelectedPath(GameController gc) =>
        Safe.Read(SelectedCurrency.BaseItemTypeIfSelected(gc), static t => t?.Metadata ?? "", "");

    /// <summary>
    /// Why a run stopped when a click did not change the item. After some orbs, the stack has most likely run out; before
    /// any, the currency most likely cannot be used on this item, which the game refuses without changing it.
    /// </summary>
    private static string ItemDidNotChangeMessage(int orbsUsed) => orbsUsed > 0
        ? $"Item did not change. Used {orbsUsed} {(orbsUsed == 1 ? "orb" : "orbs")}, did we run out?"
        : "Item did not change. Used 0 orbs, is the orb valid for this item?";

    /// <summary>A uniform draw between two values in milliseconds, either way round.</summary>
    private static TimeSpan DrawMs(int a, int b)
    {
        var (low, high) = a <= b ? (a, b) : (b, a);

        return TimeSpan.FromMilliseconds(Pick.Next(low, high + 1));
    }
}
