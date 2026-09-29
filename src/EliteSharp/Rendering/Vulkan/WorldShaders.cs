namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The GLSL shaders for the 3D world. All of them share the per-frame uniforms
/// (the camera's view-projection matrix, the viewport, and the colours of the
/// inks in the current palette) and the per-draw push constants (the model
/// matrix, the ink, and parameters that depend on what is being drawn), which
/// are declared once here and included in each shader.
/// </summary>
internal static class WorldShaders
{
    /// <summary>The declarations shared by all the world shaders.</summary>
    private static readonly string Common = $$"""
        #version 450

        // Per-frame data
        layout(set = 0, binding = 0) uniform FrameUniforms
        {
            mat4 viewProjection;  // world space to clip space
            vec4 viewport;        // x, y = the viewport's top-left in framebuffer pixels,
                                  // z = the size of one of the original's pixels, w = unused
            vec4 viewportSize;    // x, y = the viewport's size in framebuffer pixels
            uvec4 inks[{{Inks.Count}}];     // the colours of each ink, as a pattern of four
                                  // packed RGBA8 colours (see Palette)
        } frame;

        // Per-draw data
        layout(push_constant) uniform DrawConstants
        {
            mat4 model;           // model space to world space
            vec4 parameters;      // meaning depends on the shader
            uvec4 inks;           // x = the ink to draw in (the sun uses x and y)
        } draw;

        // The colour of an ink at a position on the screen: the ink's pattern
        // repeats across the screen, one colour per pixel of the original's
        vec4 inkColour(uint ink, vec2 fragCoord)
        {
            uint pixel = uint(floor((fragCoord.x - frame.viewport.x) / frame.viewport.z)) & 3u;
            return unpackUnorm4x8(frame.inks[ink][pixel]);
        }
        """;

    /// <summary>
    /// The wireframe vertex shader, used for ships, the planet's circles and
    /// laser beams, which are line lists. Which parts of the lines are hidden
    /// is left to the depth test.
    /// </summary>
    public static readonly string WireVertex = Common + """

        layout(location = 0) in vec3 inPosition;

        layout(location = 0) flat out uint outInk;

        void main()
        {
            outInk = draw.inks.x;
            gl_Position = frame.viewProjection * (draw.model * vec4(inPosition, 1.0));
        }
        """;

    /// <summary>
    /// The vertex shader for solid surfaces (the ships' hulls and the planet's
    /// sphere), which are drawn into the depth buffer so they hide whatever is
    /// behind them. Each vertex is pushed back along the line of sight from the
    /// camera (which is at the origin of world space) by parameters.x, which
    /// moves the surface away from the camera by that distance without
    /// changing its outline, so the lines drawn on the surface (and details
    /// drawn just inside it) pass the depth test.
    /// </summary>
    public static readonly string SurfaceVertex = Common + """

        layout(location = 0) in vec3 inPosition;

        layout(location = 0) flat out uint outInk;

        void main()
        {
            outInk = draw.inks.x;
            vec3 world = (draw.model * vec4(inPosition, 1.0)).xyz;
            float distance = length(world);
            if (distance > 0.0)
            {
                world += world / distance * draw.parameters.x;
            }

            gl_Position = frame.viewProjection * vec4(world, 1.0);
        }
        """;

    /// <summary>
    /// The fragment shader for inks, whose patterns repeat across the screen,
    /// one colour per original pixel. Transparent pixels are discarded.
    /// </summary>
    public static readonly string InkFragment = Common + """

        layout(location = 0) flat in uint inInk;

        layout(location = 0) out vec4 outColour;

        void main()
        {
            vec4 colour = inkColour(inInk, gl_FragCoord.xy);
            if (colour.a == 0.0)
            {
                discard;
            }

            outColour = vec4(colour.rgb, 1.0);
        }
        """;

    /// <summary>
    /// The particle vertex shader, which draws each particle as a rectangle of
    /// a fixed size in original pixels, with its top-left corner at the
    /// particle's projected position (as the original plots its dots). Each
    /// particle is an instance, and each instance is six vertices.
    /// </summary>
    public static readonly string ParticleVertex = Common + """

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec2 inSize;          // in original pixels
        layout(location = 2) in uint inInk;

        layout(location = 0) flat out uint outInk;

        const vec2 corners[6] = vec2[](
            vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
            vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

        void main()
        {
            vec4 clip = frame.viewProjection * vec4(inPosition, 1.0);
            vec2 offset = corners[gl_VertexIndex] * inSize * frame.viewport.z * 2.0 / frame.viewportSize.xy;
            clip.xy += offset * clip.w;
            gl_Position = clip;
            outInk = inInk;
        }
        """;

    /// <summary>
    /// The sun's vertex shader. The sun is drawn as a disc facing the camera
    /// (whose model matrix maps the unit disc onto the sun's outline, enlarged
    /// by parameters.x to leave room for the fringe), and this passes on the
    /// position within the disc in units of the sun's radius.
    /// </summary>
    public static readonly string SunVertex = Common + """

        layout(location = 0) in vec3 inPosition;

        layout(location = 0) out vec2 outDisc;

        void main()
        {
            outDisc = inPosition.xy * draw.parameters.x;
            gl_Position = frame.viewProjection * (draw.model * vec4(inPosition, 1.0));
        }
        """;

    /// <summary>
    /// The sun's fragment shader, which recreates the look of SUN: the disc is
    /// drawn in rows of original pixels, each row extended by a random amount
    /// (up to parameters.z pixels) to give the flickering fringe, and filled
    /// with the original's pattern of alternating red and yellow pixels, which
    /// shifts by a pixel every two rows. inks.x and inks.y are the red and
    /// yellow inks, parameters.y is the random seed for the fringe, and
    /// parameters.w is the sun's radius in original pixels.
    /// </summary>
    public static readonly string SunFragment = Common + """

        layout(location = 0) in vec2 inDisc;

        layout(location = 0) out vec4 outColour;

        uint hash(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        void main()
        {
            float pixel = frame.viewport.z;
            uint row = uint(floor((gl_FragCoord.y - frame.viewport.y) / pixel));
            uint column = uint(floor((gl_FragCoord.x - frame.viewport.x) / pixel));

            if (abs(inDisc.y) > 1.0)
            {
                discard;
            }

            float halfWidth = sqrt(1.0 - inDisc.y * inDisc.y);
            uint fringe = hash(row * 7919u + uint(draw.parameters.y)) & uint(draw.parameters.z);
            if (abs(inDisc.x) > halfWidth + float(fringe) / max(draw.parameters.w, 1.0))
            {
                discard;
            }

            bool red = ((column + (row >> 1)) & 1u) == 0u;
            outColour = vec4(unpackUnorm4x8(frame.inks[red ? draw.inks.x : draw.inks.y][0]).rgb, 1.0);
        }
        """;
}
