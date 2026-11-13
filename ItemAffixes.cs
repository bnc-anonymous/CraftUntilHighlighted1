using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using ModsComponent = ExileCore.PoEMemory.Components.Mods;

namespace CraftUntilHighlighted;

/// <summary>
/// The prefixes and suffixes on a magic item, for the augment boxes (AugmentBoxes.WantsAugment): its ExplicitMods
/// counted by each modifier's ModRecord.AffixType, the field Artisan, a PoE1 crafting plugin, reads to tell the two
/// apart. A magic item has at most one of each.
/// </summary>
internal static class ItemAffixes
{
    public readonly record struct Counts(int Prefixes, int Suffixes)
    {
        public override string ToString() => $"{Prefixes} prefix(es), {Suffixes} suffix(es)";
    }

    /// <summary>
    /// The counts on a magic item, or null when the item is not magic or any part cannot be read: no explicit modifiers
    /// at all (an altered magic item always has one, so none read is an item still being filled in after an orb), or a
    /// modifier whose affix type will not read. A half-read item must never look like one with an empty slot, which would
    /// spend an Orb of Augmentation on a full item.
    /// </summary>
    public static Counts? OfMagicItem(NormalInventoryItem item) =>
        Safe.Read(item, static i =>
        {
            var mods = i?.Item?.GetComponent<ModsComponent>();

            if (mods == null || mods.ItemRarity != ItemRarity.Magic)
                return (Counts?)null;

            var explicitMods = mods.ExplicitMods;

            if (explicitMods is not { Count: > 0 })
                return null;

            var prefixes = 0;
            var suffixes = 0;

            foreach (var mod in explicitMods)
            {
                switch (mod?.ModRecord?.AffixType)
                {
                    case ModType.Prefix:
                        prefixes++;
                        break;
                    case ModType.Suffix:
                        suffixes++;
                        break;
                    case null:
                        return null;
                }
            }

            return new Counts(prefixes, suffixes);
        }, null);
}
