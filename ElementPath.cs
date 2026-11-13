using System;
using System.Collections.Generic;
using ExileCore.PoEMemory;

namespace CraftUntilHighlighted;

/// <summary>
/// An element's place in the interface as a path of child indices below another element, and the element at such a path.
/// CraftingSlot and StashSearch find their elements once and keep the path rather than the element: the element itself is
/// rebuilt (an orb replaces the crafting item's, reopening the stash can replace the search box's), while its place stays.
///
/// Both directions read one child pointer per step through Element.ChildCount and Element.GetChildAtIndex rather than
/// listing every child through Element.Children. GetChildAtIndex is only called below ChildCount: ExileCore2's
/// GetChildFromIndices logs a line for every miss, and an empty crafting slot is a miss on every read.
/// </summary>
internal static class ElementPath
{
    /// <summary>How far up PathFrom walks before giving up. Chosen: the PoE2 crafting item sat 10 levels below StashElement.</summary>
    private const int MostDepth = 32;

    /// <summary>
    /// The most children of one parent PathFrom compares against. Chosen: a currency tab's slot container holds one child
    /// per slot, which ran to 52 in PoE2.
    /// </summary>
    private const int MostChildrenCompared = 1024;

    /// <summary>The element at a child-index path below root, or null when any step is missing.</summary>
    public static Element ChildAt(Element root, int[] path)
    {
        var element = root;

        foreach (var index in path)
        {
            if (element == null || index >= element.ChildCount)
                return null;

            element = element.GetChildAtIndex(index);
        }

        return element;
    }

    /// <summary>
    /// The child-index path from root down to element, walking up element's Parent chain and finding each step's index
    /// among its parent's children by address. Null when element is not below root within MostDepth levels, or a step
    /// cannot be read.
    /// </summary>
    public static int[] PathFrom(Element root, Element element)
    {
        var rootAddress = root?.Address ?? 0L;

        if (rootAddress == 0L)
            return null;

        var indices = new List<int>();
        var at = element;

        for (var depth = 0; depth < MostDepth && at != null && at.Address != 0L; depth++)
        {
            if (at.Address == rootAddress)
            {
                indices.Reverse();
                return indices.ToArray();
            }

            var parent = at.Parent;

            if (parent == null || parent.Address == 0L)
                return null;

            var index = IndexAmongChildren(parent, at.Address);

            if (index < 0)
                return null;

            indices.Add(index);
            at = parent;
        }

        return null;
    }

    /// <summary>A path written as `[2,0,4]`, or "unknown" for none.</summary>
    public static string Describe(int[] path) => path == null ? "unknown" : $"[{string.Join(",", path)}]";

    private static int IndexAmongChildren(Element parent, long address)
    {
        var count = (int)Math.Min(parent.ChildCount, MostChildrenCompared);

        for (var i = 0; i < count; i++)
        {
            if (parent.GetChildAtIndex(i)?.Address == address)
                return i;
        }

        return -1;
    }
}
