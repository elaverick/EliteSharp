namespace EliteSharp.Rendering.Scene;

/// <summary>
/// Turns the original's colours into something the 3D renderer can use. Elite
/// specifies colours as screen bytes, each of which holds four mode 1 pixels,
/// so a colour can be a solid colour or a repeating pattern of up to four
/// colours (the planet's green, for example, is alternating cyan and yellow
/// pixels). A pattern is resolved through the current palette into four RGBA
/// colours, which the shaders repeat across the screen in units of the
/// original's pixels, so the colours look the same at any resolution.
/// </summary>
public static class ColourPattern
{
    /// <summary>
    /// Resolve a mode 1 colour byte into four packed RGBA8 colours, one for each
    /// pixel of the byte from left to right. Pixels whose bits are all zero are
    /// transparent (alpha 0), as drawing them with EOR logic leaves the screen
    /// unchanged. The decoding is the same as the video ULA's, and the same as
    /// the 2D shader's.
    /// </summary>
    public static uint[] Resolve(int colourByte, int[] palette)
    {
        var pattern = new uint[4];
        uint value = (uint)colourByte & 0xFF;
        for (int pixel = 0; pixel < 4; pixel++)
        {
            // Does this pixel have any bits set in screen memory?
            if ((value & (0x88u >> pixel)) == 0)
            {
                continue;
            }

            // The ULA forms the palette index from bits 7, 5, 3 and 1, shifting
            // the byte left (with 1s coming in) for each pixel
            uint shifted = ((value << pixel) | ((1u << pixel) - 1)) & 0xFF;
            uint index = ((shifted >> 4) & 8) | ((shifted >> 3) & 4) | ((shifted >> 2) & 2) | ((shifted >> 1) & 1);
            pattern[pixel] = Physical(palette[index]);
        }

        return pattern;
    }

    /// <summary>Pack one of the BBC's eight physical colours (bit 0 red, bit 1 green, bit 2 blue) as opaque RGBA8.</summary>
    public static uint Physical(int colour) =>
        ((colour & 1) != 0 ? 0xFFu : 0) | ((colour & 2) != 0 ? 0xFF00u : 0) | ((colour & 4) != 0 ? 0xFF0000u : 0) | 0xFF000000u;
}
