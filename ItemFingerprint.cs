using System;
using System.Collections.Generic;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ModsComponent = ExileCore.PoEMemory.Components.Mods;
using QualityComponent = ExileCore.PoEMemory.Components.Quality;
using SocketsComponent = ExileCore.PoEMemory.Components.Sockets;

namespace CraftUntilHighlighted;

/// <summary>
/// What a currency can change about the crafting item, read before a click so the click can be seen to have changed it.
/// It stands in for NormalInventoryItem.ItemVersion, which the PoE2 version waits on and which ExileCore does not have.
///
/// After Artisan's ItemFingerprint (PoE1). The element and entity addresses come first: Artisan measured that an Orb of
/// Alteration replaces both the item's entity and the element around it, and in PoE2 a Chaos Orb did the same, so most
/// applications show there without the modifiers being read. Not every one does - Artisan found a Blessed Orb leaves the
/// entity where it was - so rarity, every modifier's name and values, quality, sockets and corruption are compared as
/// well. A reroll can still land the item exactly as it was at the same addresses; the click then reads as having changed
/// nothing, and the run stops at Item changed timeout rather than clicking again.
///
/// Unreadable is None, never a fingerprint of nothing, so a failed read cannot count as a change.
/// </summary>
internal readonly record struct ItemFingerprint(
    long ElementAddress,
    long EntityAddress,
    int Rarity,
    int ModHash,
    int ModCount,
    int Quality,
    int SocketHash,
    bool Corrupted)
{
    public static readonly ItemFingerprint None = default;

    public bool Readable => ElementAddress != 0L && EntityAddress != 0L;

    public static ItemFingerprint Of(NormalInventoryItem item)
    {
        try
        {
            var elementAddress = item?.Address ?? 0L;
            var entity = item?.Item;
            var entityAddress = entity?.Address ?? 0L;

            if (elementAddress == 0L || entityAddress == 0L)
                return None;

            var rarity = -1;
            var modHash = 0;
            var modCount = 0;

            if (entity.TryGetComponent<ModsComponent>(out var mods) && mods != null)
            {
                rarity = (int)mods.ItemRarity;
                (modHash, modCount) = HashMods(mods);
            }

            var quality = entity.TryGetComponent<QualityComponent>(out var q) && q != null ? q.ItemQuality : 0;
            var socketHash = entity.TryGetComponent<SocketsComponent>(out var s) && s != null ? HashSockets(s) : 0;
            var corrupted = entity.TryGetComponent<Base>(out var b) && b != null && b.isCorrupted;

            return new ItemFingerprint(elementAddress, entityAddress, rarity, modHash, modCount, quality, socketHash, corrupted);
        }
        catch
        {
            return None;
        }
    }

    /// <summary>
    /// Whether the item now differs from this reading. The addresses are read first, so an application that rebuilt the
    /// item costs two reads; only an item still at the same addresses has its modifiers read. False when either reading
    /// is unreadable.
    /// </summary>
    public bool ChangedIn(NormalInventoryItem item)
    {
        if (!Readable)
            return false;

        var elementAddress = Safe.Read(item, static i => i?.Address ?? 0L, 0L);
        var entityAddress = Safe.Read(item, static i => i?.Item?.Address ?? 0L, 0L);

        if (elementAddress == 0L || entityAddress == 0L)
            return false;

        if (elementAddress != ElementAddress || entityAddress != EntityAddress)
            return true;

        var now = Of(item);

        return now.Readable && now != this;
    }

    public override string ToString() =>
        Readable
            ? $"element 0x{ElementAddress:X}, entity 0x{EntityAddress:X}, rarity {Rarity}, {ModCount} mods hash {ModHash}, " +
              $"quality {Quality}, sockets hash {SocketHash}, corrupted {Corrupted}"
            : "unreadable";

    /// <summary>
    /// A hash over every modifier's name and rolled values, in the game's order. Values are included because a Divine Orb
    /// changes nothing else. The lists are Artisan's.
    /// </summary>
    private static (int Hash, int Count) HashMods(ModsComponent mods)
    {
        unchecked
        {
            var hash = 17;
            var count = 0;

            void Fold(List<ItemMod> list)
            {
                foreach (var mod in list ?? [])
                {
                    if (mod == null)
                        continue;

                    count++;
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(mod.RawName ?? "");

                    foreach (var value in mod.Values ?? [])
                        hash = hash * 31 + value;
                }
            }

            Fold(mods.ImplicitMods);
            Fold(mods.ExplicitMods);
            Fold(mods.EnchantedMods);
            Fold(mods.FracturedMods);
            Fold(mods.SynthesisMods);
            Fold(mods.CrucibleMods);
            Fold(mods.ScourgeMods);

            return (hash, count);
        }
    }

    /// <summary>A hash over every socket's colour and link group, in the game's order, so links and colours both count.</summary>
    private static int HashSockets(SocketsComponent sockets)
    {
        unchecked
        {
            var hash = 17;

            foreach (var socket in sockets.GetSocketInfo() ?? [])
            {
                if (socket != null)
                    hash = (hash * 31 + (int)socket.SocketColor) * 31 + socket.LinkGroup;
            }

            return hash;
        }
    }
}
