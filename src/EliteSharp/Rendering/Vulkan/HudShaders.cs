namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The GLSL shaders for the HUD. Rectangles (characters, images and filled
/// rectangles) and lines are drawn as instances, positioned in the HUD's
/// layout units (the original's pixels), and coloured with inks, whose colours
/// come from the current palette.
/// </summary>
internal static class HudShaders
{
    /// <summary>The declarations shared by the HUD's shaders.</summary>
    private static readonly string Common = $$"""
        #version 450

        // The colours of each ink, as a pattern of four packed RGBA8 colours (see Palette)
        layout(set = 0, binding = 0) uniform Inks
        {
            uvec4 inks[{{Inks.Count}}];
        };

        // The atlas of the HUD's images, whose texels are inks (see HudAtlas)
        layout(set = 0, binding = 1) uniform usampler2D atlas;

        layout(push_constant) uniform Layout
        {
            vec2 size;           // the size of the area being drawn into, in layout units
            vec2 patternOrigin;  // where the inks' patterns start, in framebuffer pixels
            float pixelSize;     // the size of one of the original's pixels in framebuffer pixels
        } layout_;

        // Layout units to normalised device coordinates for the viewport
        vec4 toClip(vec2 position)
        {
            return vec4(position / layout_.size * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The vertex shader for the HUD's rectangles. Each instance is a
    /// rectangle on the screen and the region of the atlas to fill it with,
    /// drawn as two triangles (six vertices).
    /// </summary>
    public static readonly string QuadVertex = Common + """

        layout(location = 0) in vec4 inRectangle;   // x, y, width, height
        layout(location = 1) in vec4 inSource;      // the atlas region: x, y, width, height
        layout(location = 2) in uint inInk;

        layout(location = 0) out vec2 outTexel;
        layout(location = 1) flat out vec4 outSource;
        layout(location = 2) flat out uint outInk;

        const vec2 corners[6] = vec2[](
            vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
            vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

        void main()
        {
            vec2 corner = corners[gl_VertexIndex];
            gl_Position = toClip(inRectangle.xy + corner * inRectangle.zw);
            outTexel = inSource.xy + corner * inSource.zw;
            outSource = inSource;
            outInk = inInk;
        }
        """;

    /// <summary>
    /// The vertex shader for the HUD's lines. Each instance is a line (two
    /// vertices), which is solid in its ink.
    /// </summary>
    public static readonly string LineVertex = Common + $$"""

        layout(location = 0) in vec4 inEnds;        // x1, y1, x2, y2
        layout(location = 1) in uint inInk;

        layout(location = 0) out vec2 outTexel;
        layout(location = 1) flat out vec4 outSource;
        layout(location = 2) flat out uint outInk;

        void main()
        {
            gl_Position = toClip(gl_VertexIndex == 0 ? inEnds.xy : inEnds.zw);

            // Lines use the atlas's solid texel
            outSource = vec4({{HudAtlas.Solid.X}}.0, {{HudAtlas.Solid.Y}}.0, 1.0, 1.0);
            outTexel = outSource.xy + 0.5;
            outInk = inInk;
        }
        """;

    /// <summary>
    /// The fragment shader. The texel from the atlas says which ink to use
    /// (the instance's own ink, for characters and solid rectangles), and the
    /// ink's pattern repeats across the screen, one colour per original pixel.
    /// </summary>
    public static readonly string Fragment = Common + $$"""

        layout(location = 0) in vec2 inTexel;
        layout(location = 1) flat in vec4 inSource;
        layout(location = 2) flat in uint inInk;

        layout(location = 0) out vec4 outColour;

        void main()
        {
            // Stay inside the region, even at its very edges
            ivec2 texel = ivec2(clamp(floor(inTexel), inSource.xy, inSource.xy + inSource.zw - 1.0));
            uint ink = texelFetch(atlas, texel, 0).r;
            if (ink == 0u)
            {
                discard;
            }

            if (ink == {{HudAtlas.QuadInk}}u)
            {
                ink = inInk;
            }

            uint pixel = uint(floor((gl_FragCoord.x - layout_.patternOrigin.x) / layout_.pixelSize)) & 3u;
            vec4 colour = unpackUnorm4x8(inks[ink][pixel]);
            if (colour.a == 0.0)
            {
                discard;
            }

            outColour = vec4(colour.rgb, 1.0);
        }
        """;
}
