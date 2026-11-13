using System;
using System.Collections.Generic;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using ModsComponent = ExileCore.PoEMemory.Components.Mods;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace CraftUntilHighlighted;

/// <summary>
/// The item in the crafting slot of a currency or essence stash tab, and the craft square drawn at its bottom-left.
///
/// The item is found by what it is, the rule Artisan, a PoE1 crafting plugin, measured: a currency tab holds only
/// currency and an essence tab only essences, all with metadata under Metadata/Items/Currency/, so the one item in the
/// tab's VisibleInventoryItems with a path outside it and a Mods component is the crafting slot's. Artisan found it
/// there, in the tab's own item list beside the stacks, and not in a sub-inventory. That the essence tab has a crafting
/// slot is Artisan's and WheresMyCraftAt's knowledge, not yet seen here.
///
/// The search reads every stack in the tab, about two milliseconds a call in Artisan's measurement. So once the item is
/// found its child-index path below StashElement is kept (ElementPath), and later reads go straight to that path; the
/// search runs again only when the element there does not hold a craftable item. A stale path cannot pick the wrong item:
/// in these tabs any craftable item is the crafting slot's. The PoE2 version reads a fixed path read off its dumps
/// instead; no PoE1 dump has been taken yet, and the dump prints the path found (CraftingItemPath). An empty slot misses
/// the path every time, so between runs a search that found the slot empty is not repeated on the same tab for
/// EmptySlotSearchInterval (FindBetweenRuns); the path is still read on every call.
///
/// The item element is read afresh on every call and never kept. Artisan measured in PoE1 that applying an orb replaces
/// both the item's entity and the element around it, so a remembered element would report the item as it was before the
/// orb. CraftingSlotCache keeps only values copied out of it, for drawing.
/// </summary>
internal static class CraftingSlot
{
    private const string CurrencyPath = "Metadata/Items/Currency/";

    private enum Miss
    {
        None,
        StashClosed,
        NotCraftingTab,
        ItemsUnreadable,
        SlotEmpty,
    }

    /// <summary>
    /// Where the crafting item was last found, as child indices below StashElement; null until it has been found this
    /// session. See the class summary.
    /// </summary>
    public static int[] CraftingItemPath { get; private set; }

    /// <summary>
    /// How long FindBetweenRuns goes without searching a tab again after finding its crafting slot empty. Chosen: a search
    /// is about two milliseconds (Artisan), which every CraftingSlotCache refresh would otherwise spend on an empty slot,
    /// and an item put in the slot shows its square within this long of the click.
    /// </summary>
    private static readonly TimeSpan EmptySlotSearchInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>When a search last found the crafting slot empty, and the tab's address then; 0 after one that found an item.</summary>
    private static DateTime _emptySlotSearchedAt;

    private static long _emptySlotSearchedTab;

    /// <summary>The visible stash tab, or null when the stash is closed or unreadable.</summary>
    public static Inventory VisibleStashOf(GameController gc) =>
        Safe.Read(gc, static g =>
        {
            var stash = g.IngameState.IngameUi.StashElement;

            return stash is { IsVisible: true } ? stash.VisibleStash : null;
        }, null);

    /// <summary>Whether a tab is one with a crafting slot: a currency or essence tab, by type rather than by name.</summary>
    private static bool HasCraftingSlot(Inventory stash) =>
        Safe.Read(stash, static s => s.InvType, InventoryType.InvalidInventory) is InventoryType.CurrencyStash
            or InventoryType.EssenceStash;

    /// <summary>
    /// Every item element the visible tab lists: its own, then each sub-inventory's, labelled with where it came from.
    /// Empty when nothing can be read. For the dump only.
    /// </summary>
    public static List<(string Source, NormalInventoryItem Item)> ItemsOfVisibleStash(GameController gc)
    {
        var found = new List<(string, NormalInventoryItem)>();
        var stash = VisibleStashOf(gc);

        if (stash == null)
            return found;

        foreach (var item in Safe.Read(stash, static s => s.VisibleInventoryItems, null) ?? [])
        {
            if (item != null)
                found.Add(("tab", item));
        }

        var subInventories = Safe.Read(stash, static s => s.SubInventories, null) ?? [];

        for (var i = 0; i < subInventories.Count; i++)
        {
            foreach (var item in Safe.Read(subInventories[i], static s => s?.VisibleInventoryItems, null) ?? [])
            {
                if (item != null)
                    found.Add(($"sub-inventory {i}", item));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether an item element holds something an orb can be applied to, by the rule in the class summary. The Mods
    /// component test is not redundant: it refuses a half-written entity whose path reads blank or stale (Artisan).
    /// </summary>
    public static bool IsCraftable(NormalInventoryItem item) =>
        Safe.Read(item, static i =>
        {
            var entity = i.Item;

            if (entity == null || !entity.IsValid)
                return false;

            var path = entity.Path;

            return !string.IsNullOrEmpty(path) &&
                   !path.StartsWith(CurrencyPath, StringComparison.Ordinal) &&
                   entity.HasComponent<ModsComponent>();
        }, false);

    /// <summary>
    /// The item in the crafting slot, or null. Stops reading at the first thing missing: a closed stash costs the
    /// visibility read, another tab the tab type.
    /// </summary>
    public static NormalInventoryItem Find(GameController gc) => Locate(gc, out _, false);

    /// <summary>
    /// Find, for the craft square between runs: a tab whose crafting slot a search found empty is not searched again for
    /// EmptySlotSearchInterval. Not for a run, which must see the item the moment it is back.
    /// </summary>
    public static NormalInventoryItem FindBetweenRuns(GameController gc) => Locate(gc, out _, true);

    /// <summary>
    /// The item in the crafting slot, or null with the reason. Builds the reason as text, so it is for the dump and debug
    /// mode, not for every frame.
    /// </summary>
    public static NormalInventoryItem Find(GameController gc, out string why)
    {
        var item = Locate(gc, out var miss, false);

        why = miss switch
        {
            Miss.None => "",
            Miss.StashClosed => "the stash is not open",
            Miss.NotCraftingTab =>
                $"the open tab is {Safe.Read(VisibleStashOf(gc), static s => s.InvType.ToString(), "unreadable")}, " +
                "not a currency or essence tab",
            Miss.ItemsUnreadable => "the tab's items could not be read",
            _ => "the crafting slot is empty",
        };

        return item;
    }

    private static NormalInventoryItem Locate(GameController gc, out Miss miss, bool skipRecentEmptySearch)
    {
        var stash = VisibleStashOf(gc);

        if (stash == null)
        {
            miss = Miss.StashClosed;
            return null;
        }

        if (!HasCraftingSlot(stash))
        {
            miss = Miss.NotCraftingTab;
            return null;
        }

        miss = Miss.None;

        if (CraftingItemPath != null)
        {
            var atPath = Safe.Read(gc, static g =>
                ElementPath.ChildAt(g.IngameState.IngameUi.StashElement, CraftingItemPath)?.AsObject<NormalInventoryItem>(), null);

            if (IsCraftable(atPath))
                return atPath;
        }

        var tab = Safe.Read(stash, static s => s.Address, 0L);

        if (skipRecentEmptySearch && tab != 0L && tab == _emptySlotSearchedTab &&
            DateTime.UtcNow - _emptySlotSearchedAt < EmptySlotSearchInterval)
        {
            miss = Miss.SlotEmpty;
            return null;
        }

        var items = Safe.Read(stash, static s => s.VisibleInventoryItems, null);

        if (items == null)
        {
            miss = Miss.ItemsUnreadable;
            return null;
        }

        foreach (var item in items)
        {
            if (!IsCraftable(item))
                continue;

            // Kept only when found; a path that cannot be worked out leaves the search to run on every read.
            if (Safe.Read((Gc: gc, Item: item), static x =>
                    ElementPath.PathFrom(x.Gc.IngameState.IngameUi.StashElement, x.Item), null) is { } path)
                CraftingItemPath = path;

            _emptySlotSearchedTab = 0L;
            return item;
        }

        _emptySlotSearchedAt = DateTime.UtcNow;
        _emptySlotSearchedTab = tab;
        miss = Miss.SlotEmpty;
        return null;
    }

    /// <summary>
    /// The craft square for an item's rectangle: a side of percentOfWidth of the item's width, inset from the item's
    /// bottom-left corner so a click inside it lands on the item. Shrunk to fit inside the item.
    /// </summary>
    public static RectangleF CraftSquareOf(RectangleF item, int percentOfWidth)
    {
        const float inset = 4f;

        var side = Math.Min(item.Width * percentOfWidth / 100f, Math.Min(item.Width, item.Height) - 2f * inset);

        if (side <= 0f)
            return default;

        return new RectangleF(item.Left + inset, item.Bottom - inset - side, side, side);
    }

    /// <summary>
    /// What the stash search is doing to one item element. Highlighted is Element.isHighlighted, which is what decides.
    ///
    /// Measured in PoE2 only (dumps of 2026-10-26, one ring and three searches): isHighlighted was true while the search
    /// matched the ring and false with no search or a search that did not match, and a match also set the ring's
    /// BordColor, which is kept for the readout. ExileCore has the same property; whether PoE1's stash search sets it is
    /// not yet measured. The craft square's highlighted colour shows it: with a search that matches the item, the square
    /// should turn purple.
    /// </summary>
    public readonly record struct SearchHighlight(bool Highlighted, ColorBGRA Border)
    {
        public override string ToString() =>
            $"isHighlighted {Highlighted}, border #{Border.A:X2}{Border.R:X2}{Border.G:X2}{Border.B:X2}";
    }

    public static SearchHighlight SearchHighlightOf(Element item) =>
        new(Safe.Read(item, static e => e.isHighlighted, false), Safe.Read(item, static e => e.BordColor, default(ColorBGRA)));

    public static bool Contains(RectangleF rect, Vector2 point) =>
        rect.Width > 0f && point.X >= rect.Left && point.X <= rect.Right && point.Y >= rect.Top && point.Y <= rect.Bottom;

    /// <summary>The game's own record of where its cursor is, in client space.</summary>
    public static Vector2 MouseOf(GameController gc) =>
        Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), new Vector2(-1f, -1f));
}
