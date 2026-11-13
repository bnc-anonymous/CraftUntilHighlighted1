using System;
using System.Diagnostics;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Nodes;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;

namespace CraftUntilHighlighted;

/// <summary>
/// Applies a currency orb to the item in the crafting slot of a currency or essence stash tab until the game's stash
/// search highlights it. The Path of Exile 1 port of the ExileCore2 plugin of the same name.
///
/// The intended use: type a regex for the wanted modifiers into the stash search, right-click a currency on the Allowed
/// currencies list, and move the cursor into the craft square at the bottom-left of the item. The plugin then holds shift
/// and left-clicks, waits for the item to change and a randomised item changed delay, and clicks again, until the search
/// highlights the item. All input goes through ExileInput.
///
/// The run itself is CraftLoop. What differs from PoE2 is how the game is read: the crafting item is found by search
/// (CraftingSlot), the search box by the input focus (StashSearch), the selected currency through ExileCore's
/// Cursor.ItemType (SelectedCurrency), and a click's effect by ItemFingerprint. See Dump and STATE.md.
/// </summary>
public class CraftUntilHighlighted : BaseSettingsPlugin<CraftUntilHighlightedSettings>
{
    /// <summary>
    /// The ExileInput bridge version this plugin needs (ExileInput.BridgeVersion). 7 adds AllowPlayerKeys, which lets the
    /// player's Alt through while a run holds shift. From 2, the first version of ExileInput for PoE1, runs work without
    /// it, but Alt stops them; a run's start message says so (CraftLoop.TryStart).
    ///
    /// ExileInput missing or out of date is said only under the cursor, in the craft square, when a run would start: a
    /// line in the HUD's log at load could not be taken back once ExileInput was enabled, and a notice at load is easily
    /// missed. The client goes on looking for ExileInput (ExileInputClient.Look), so the message clears once it is enabled.
    /// </summary>
    public const int ExileInputVersionNeeded = 7;

    private ExileInputClient _input;
    private CraftLoop _craftLoop;
    private CraftingSlotCache _craftingSlotCache;
    private readonly HoveredItemTooltip _hoveredItemTooltip = new();
    private readonly AugmentBoxes _augmentBoxes = new();
    private readonly CallTimeAverage _tickTime = new();
    private readonly CallTimeAverage _renderTime = new();

    public override bool Initialise()
    {
        DumpFolder.ConfigDirectory = ConfigDirectory;
        _input = new ExileInputClient(GameController, "CraftUntilHighlighted");
        _craftingSlotCache = new CraftingSlotCache();
        _craftLoop = new CraftLoop(_input, _craftingSlotCache, _augmentBoxes);

        // ListNode choices are not saved, so they are supplied on every start. Selecting a preset sets the ranges.
        var preset = Settings.Crafting.TimingPreset;
        preset.SetListValues(TimingPresetRanges.Names);
        preset.OnValueSelected += name => TimingPresetRanges.Named(name)?.ApplyTo(Settings.Crafting);

        RegisterHotkey(Settings.Debug.DumpHotkey);
        ApplySettingsChanges();

        return true;
    }

    /// <summary>The ConfigVersion a saved file reaches once every step below has run.</summary>
    private const int CurrentConfigVersion = 0;

    /// <summary>
    /// Brings a saved settings file up to date with changed defaults, once per change. No default has changed since the
    /// plugin was published, so there are no steps yet; the first adds one and raises CurrentConfigVersion to 1.
    /// </summary>
    private void ApplySettingsChanges()
    {
        if (Settings.ConfigVersion >= CurrentConfigVersion)
            return;

        Settings.ConfigVersion = CurrentConfigVersion;
    }

    /// <summary>
    /// Tells Input about a hotkey so PressedOnce answers, and again whenever it is rebound in the menu. A key left unbound
    /// is not registered, since registering None would claim a key nobody pressed.
    /// </summary>
    private static void RegisterHotkey(HotkeyNodeV2 hotkey)
    {
        Bind(hotkey);
        hotkey.OnValueChanged += () => Bind(hotkey);
    }

    private static void Bind(HotkeyNodeV2 hotkey)
    {
        if (Safe.Read(hotkey, static h => h.Value.Key, Keys.None) != Keys.None)
            Input.RegisterKey(hotkey.Value);
    }

    /// <summary>ExileApi's Tick returns a job to run in the background; this plugin runs none.</summary>
    public override Job Tick()
    {
        TickOnMainThread();
        return null;
    }

    private void TickOnMainThread()
    {
        // The dump answers with the plugin disabled too: what it is for is reading the client before the plugin does
        // anything with it.
        if (Settings.Debug.DumpHotkey.PressedOnce() || Dump.AskedForInSettings)
        {
            Dump.AskedForInSettings = false;
            Dump.Write(this, _input);
        }

        var startedAt = Stopwatch.GetTimestamp();

        ShowCustomWhenRangesEdited();

        if (!Settings.Enable)
        {
            if (_craftLoop.Running)
                _craftLoop.Stop(CursorMessage.Kind.Info, "Stopped: plugin disabled");

            return;
        }

        // Between runs the square comes from the cache; a run reads the item itself every tick.
        if (!_craftLoop.Running)
            _craftingSlotCache.RefreshIfDue(GameController, Settings);

        _craftLoop.Tick(GameController, Settings);

        // From the cache, which a run does not refresh: the slot does not move, and the run's currency stays selected.
        _augmentBoxes.Tick(GameController, Settings, _craftingSlotCache.ItemRect, _craftingSlotCache.SelectedCurrencyPath);

        // Read only while a craft square or the augment boxes are drawn, which is the only thing it is for.
        if (_craftLoop.Running || _craftingSlotCache.Square.Width > 0f || _augmentBoxes.Shown)
            _hoveredItemTooltip.Refresh(GameController);
        else
            _hoveredItemTooltip.Clear();

        _tickTime.AddSince(startedAt);
    }

    /// <summary>Shows Custom in the preset dropdown once the ranges no longer match the preset it names.</summary>
    private void ShowCustomWhenRangesEdited()
    {
        var crafting = Settings.Crafting;

        if (TimingPresetRanges.Named(crafting.TimingPreset.Value) is { } named && !named.Matches(crafting))
            crafting.TimingPreset.Value = TimingPresetRanges.Custom;
    }

    public override void AreaChange(AreaInstance area) => _craftLoop?.Stop(CursorMessage.Kind.Info, "Stopped: area changed");

    public override void Render()
    {
        // Before the early return, so a message raised as the plugin is disabled is still seen.
        var message = CursorMessage.Draw(Graphics, GameController, Settings.Display.CursorMessageSeconds.Value);

        if (Settings.Enable)
            DrawFirstUseCountdown(message);

        if (Settings.Enable)
        {
            var startedAt = Stopwatch.GetTimestamp();
            DrawCraftSquare();
            _augmentBoxes.Draw(Graphics, _hoveredItemTooltip);
            _renderTime.AddSince(startedAt);
        }

        if (Settings.Debug.ShowOverlay)
            RenderDebug();
    }

    /// <summary>
    /// The first-use countdown's bar, under its message: AutoExpedition's hold bar (80 by 6 px, a black edge, full when it
    /// starts and empty when it ends), in its green.
    /// </summary>
    private void DrawFirstUseCountdown(RectangleF message)
    {
        var left = _craftLoop.FirstUseCountdownLeft;

        if (left < 0d || message.Width <= 0f)
            return;

        const float wide = 80f;
        const float high = 6f;
        var at = new Vector2(message.X, message.Bottom + 4f);

        Graphics.DrawBox(new RectangleF(at.X - 1f, at.Y - 1f, wide + 2f, high + 2f), Color.Black);
        Graphics.DrawBox(new RectangleF(at.X, at.Y, wide * (float)left, high), Colours.FromArgb(230, 90, 255, 120));
    }

    /// <summary>
    /// Draws the craft square without reading the game: between runs from CraftingSlotCache and the cursor test the
    /// loop's tick made, during a run from the run's locked square. The item element is rebuilt by every orb and can read
    /// as missing, or with a rectangle elsewhere, for a frame, so a square read from it during a run would blink or jump.
    /// Not drawn while it overlaps an item description panel (HoveredItemTooltip), which the HUD would draw it over.
    /// </summary>
    private void DrawCraftSquare()
    {
        var display = Settings.Display;

        if (_craftLoop.Running)
        {
            if (_hoveredItemTooltip.Covers(_craftLoop.RunSquare))
                return;

            // The cursor is in the run's square while it runs; leaving it ends the run on the next tick.
            Graphics.DrawFrame(_craftLoop.RunSquare,
                _craftLoop.RunHighlighted ? display.CraftSquareHighlightedColour : display.CraftSquareHoveredColour, 2);
            return;
        }

        var square = _craftingSlotCache.Square;

        if (square.Width <= 0f || _hoveredItemTooltip.Covers(square))
            return;

        var colour = _craftingSlotCache.Highlighted ? display.CraftSquareHighlightedColour
            : _craftLoop.CursorInSquare ? display.CraftSquareHoveredColour
            : display.CraftSquareColour;

        Graphics.DrawFrame(square, colour, 2);
    }

    /// <summary>
    /// Writes the plugin's average Tick and Render times above the cursor, and while the stash is open frames the crafting
    /// item and the element under the cursor with a status line. Reads the game every frame, so it is not part of the
    /// Render time it shows.
    /// </summary>
    private void RenderDebug()
    {
        var mouse = CraftingSlot.MouseOf(GameController);
        var lineAt = mouse + new Vector2(16f, -60f);
        var background = Colours.FromArgb(180, 0, 0, 0);

        // Above the cursor, so it does not cover the cursor message below it.
        var timing = $"tick {_tickTime.Microseconds:0.0} us | render {_renderTime.Microseconds:0.0} us (averages)";
        lineAt.Y += Graphics.DrawTextWithBackground(timing, lineAt, Color.White, background).Y + 2f;

        if (!Safe.Read(GameController, static g => g.IngameState.IngameUi.StashElement.IsVisible, false))
            return;

        var item = CraftingSlot.Find(GameController, out var why);
        var itemRect = Safe.Read(item, static i => i.GetClientRectCache, default(RectangleF));

        if (itemRect.Width > 0f)
            Graphics.DrawFrame(itemRect, Color.Cyan, 1);

        var hover = Safe.Read(GameController, static g => g.IngameState.UIHover, null);
        var hoverRect = Safe.Read(hover, static h => h.GetClientRectCache, default(RectangleF));

        if (hoverRect.Width > 0f)
            Graphics.DrawFrame(hoverRect, Color.Magenta, 1);

        var search = StashSearch.TextOf(GameController) is { } text ? $"search \"{text}\"" : "search box not found";
        var itemLine = item == null ? $"no crafting item: {why}" : $"{search}: {CraftingSlot.SearchHighlightOf(item)}";
        var selected = Safe.Read(SelectedCurrency.BaseItemTypeIfSelected(GameController), static t => t?.BaseName, null);
        var cursor = Safe.Read(GameController, static g => g.IngameState.IngameUi.Cursor.Action.ToString(), "?") +
                     (selected == null ? "" : $" {selected}");
        var line = $"{itemLine} | cursor {cursor} | ExileInput {(_input?.Available == true ? "up" : "missing")}";

        Graphics.DrawTextWithBackground(line, lineAt, Color.White, background);
    }

    public override void OnUnload()
    {
        // Release lets go of shift and every other key this plugin holds through ExileInput.
        _craftLoop?.Stop(CursorMessage.Kind.Info, "Stopped: plugin unloaded");
        Safe.Read(_input, static i => i?.Release() ?? false, false);
    }
}
