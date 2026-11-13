using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared.Enums;

namespace CraftUntilHighlighted;

/// <summary>
/// Which currency is right-clicked for use, and whether it is on the Allowed currencies list.
///
/// ExileCore names the selection. While a currency is right-clicked for use, IngameUi.Cursor.Action is UseItem and
/// Cursor.ItemType is its base item type; Artisan measured both in PoE1, an Orb of Alteration reading UseItem, metadata
/// Metadata/Items/Currency/CurrencyRerollMagic and base name "Orb of Alteration". The stack stays where it is and the
/// cursor inventory stays empty. The PoE2 version reads the type's hash raw off the cursor element, because ExileCore2
/// does not expose it; none of that is needed here. The type is taken only when it is a currency (metadata under
/// Metadata/Items/Currency/), so an item picked up (HoldItem) never counts. A selection that cannot be named is never
/// allowed.
/// </summary>
internal static class SelectedCurrency
{
    private const string CurrencyPath = "Metadata/Items/Currency/";

    /// <summary>
    /// How long one Allowed currencies pattern may take to match before it counts as no match. Chosen: far longer than a
    /// name or a metadata path takes, short enough that a pattern that backtracks badly cannot stall a frame.
    /// </summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>Each Allowed currencies line as a compiled pattern, or null where it is not a valid regular expression.</summary>
    private static readonly Dictionary<string, Regex> PatternOfLine = new(StringComparer.Ordinal);

    /// <summary>Whether something is right-clicked for use: Cursor.Action is UseItem.</summary>
    public static bool AnySelected(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.Cursor.Action, MouseActionType.Free) == MouseActionType.UseItem;

    /// <summary>The base item type of the currency right-clicked for use, or null when none is selected or it cannot be named.</summary>
    public static BaseItemType BaseItemTypeIfSelected(GameController gc)
    {
        if (!AnySelected(gc))
            return null;

        var itemType = Safe.Read(gc, static g => g.IngameState.IngameUi.Cursor.ItemType, null);

        return Safe.Read(itemType, static t => t?.Metadata ?? "", "").StartsWith(CurrencyPath, StringComparison.Ordinal)
            ? itemType
            : null;
    }

    /// <summary>Whether a currency is right-clicked for use and it is on the Allowed currencies list.</summary>
    public static bool AllowedIsSelected(GameController gc, CraftUntilHighlightedSettings settings) =>
        IsAllowed(BaseItemTypeIfSelected(gc), settings);

    /// <summary>
    /// Whether a base item type is on the Allowed currencies list: some line, as a regular expression ignoring case, matches
    /// the whole of its base name or the whole of its metadata path. Whole, so "Chaos Orb" does not also allow the Tainted
    /// Chaos Orb; ".* Catalyst" allows every catalyst. A line that is not a valid regular expression matches nothing.
    /// </summary>
    public static bool IsAllowed(BaseItemType itemType, CraftUntilHighlightedSettings settings)
    {
        var name = Safe.Read(itemType, static t => t?.BaseName, null);
        var metadata = Safe.Read(itemType, static t => t?.Metadata, null);

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(metadata))
            return false;

        foreach (var line in settings.AllowedCurrencies.Content)
        {
            if (PatternOf(line?.Value) is not { } pattern)
                continue;

            if (WholeMatch(pattern, name) || WholeMatch(pattern, metadata))
                return true;
        }

        return false;
    }

    /// <summary>A line's pattern, anchored to match whole, compiled once and kept; null for a blank or invalid line.</summary>
    private static Regex PatternOf(string line)
    {
        var text = line?.Trim();

        if (string.IsNullOrEmpty(text))
            return null;

        if (PatternOfLine.TryGetValue(text, out var known))
            return known;

        Regex pattern;

        try
        {
            pattern = new Regex($"^(?:{text})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException)
        {
            pattern = null;
        }

        PatternOfLine[text] = pattern;
        return pattern;
    }

    private static bool WholeMatch(Regex pattern, string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
