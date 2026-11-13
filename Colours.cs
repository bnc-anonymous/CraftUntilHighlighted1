using Color = SharpDX.Color;

namespace CraftUntilHighlighted;

/// <summary>
/// System.Drawing.Color's constructors for SharpDX.Color, which is the colour ExileApi draws and saves with. The plugin
/// was written against ExileCore2, which uses System.Drawing.Color; these keep its argument order (alpha first) so the
/// colours read the same. The same helper as AutoExpedition's PoE1 port.
/// </summary>
internal static class Colours
{
    public static Color FromArgb(int alpha, int red, int green, int blue) =>
        new((byte)red, (byte)green, (byte)blue, (byte)alpha);

    /// <summary>The same colour with a different alpha.</summary>
    public static Color FromArgb(int alpha, Color colour) => new(colour.R, colour.G, colour.B, (byte)alpha);
}
