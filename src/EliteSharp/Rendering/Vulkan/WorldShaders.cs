namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The GLSL shaders for the 3D world. All of them share the per-frame uniforms
/// (the camera's view-projection matrix and the viewport) and the per-draw push
/// constants (the model matrix, and parameters that depend on what is being
/// drawn), which are declared once here and included in each shader.
/// </summary>
internal static class WorldShaders
{
    /// <summary>The declarations shared by all the world shaders.</summary>
    private const string Common = """
        #version 450

        // Per-frame data
        layout(set = 0, binding = 0) uniform FrameUniforms
        {
            mat4 viewProjection;  // world space to clip space
            vec4 viewport;        // x, y = the viewport's top-left in framebuffer pixels,
                                  // z = the size of one of the original's pixels, w = unused
            vec4 viewportSize;    // x, y = the viewport's size in framebuffer pixels
        } frame;

        // Per-draw data
        layout(push_constant) uniform DrawConstants
        {
            mat4 model;           // model space to world space
            vec4 cameraModel;     // xyz = the camera's position in model space, w = the LOD distance (XX4)
            vec4 parameters;      // meaning depends on the shader
            uvec4 colours;        // four packed RGBA8 colours (a colour pattern, or shader-specific colours)
        } draw;
        """;

    /// <summary>
    /// The wireframe vertex shader, used for ships, the planet's circles and
    /// laser beams. Each vertex belongs to an edge of the model, and carries
    /// the normals of the two faces either side of the edge, so the shader can
    /// apply Elite's hidden line removal (LL9): an edge is drawn if either of
    /// its faces is visible and the edge is within its visibility distance.
    ///
    /// parameters.x = the scale that turns a face normal into a point on the
    /// face (2^-S, or 0 when the original ignores the normal), and
    /// parameters.y = 1 to draw every edge (for geometry that has no faces).
    /// </summary>
    public const string WireVertex = Common + """

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec4 inFaceA;         // xyz = face normal, w = visibility distance (-1 if always visible)
        layout(location = 2) in vec4 inFaceB;
        layout(location = 3) in float inEdgeVisibility;

        layout(location = 0) flat out uvec4 outColours;

        // Elite's back-face test (LL9 part 5): a face is visible if the camera is
        // in front of the plane that has the face normal as its normal and that
        // passes through the point given by the scaled normal
        bool faceVisible(vec4 face)
        {
            if (face.w < 0.0 || face.w < draw.cameraModel.w)
            {
                // The edge has no face here, or the ship is further away than
                // the face's visibility distance, so the face is always visible
                return true;
            }

            vec3 normal = face.xyz;
            return dot(normal, draw.cameraModel.xyz - normal * draw.parameters.x) > 0.0;
        }

        void main()
        {
            outColours = draw.colours;

            bool visible = draw.parameters.y != 0.0
                || (inEdgeVisibility >= draw.cameraModel.w && (faceVisible(inFaceA) || faceVisible(inFaceB)));

            if (!visible)
            {
                // Put both ends of the edge behind the near plane, so the whole
                // line is clipped away
                gl_Position = vec4(0.0, 0.0, -1.0, 1.0);
                return;
            }

            gl_Position = frame.viewProjection * (draw.model * vec4(inPosition, 1.0));
        }
        """;

    /// <summary>
    /// The fragment shader for colour patterns: the four colours repeat across
    /// the screen, one per original pixel, as the pixels of a mode 1 screen
    /// byte do. Transparent pixels are discarded.
    /// </summary>
    public const string PatternFragment = Common + """

        layout(location = 0) flat in uvec4 inColours;

        layout(location = 0) out vec4 outColour;

        void main()
        {
            uint pixel = uint(floor((gl_FragCoord.x - frame.viewport.x) / frame.viewport.z)) & 3u;
            vec4 colour = unpackUnorm4x8(inColours[pixel]);
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
    public const string ParticleVertex = Common + """

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec2 inSize;          // in original pixels
        layout(location = 2) in uvec4 inColours;

        layout(location = 0) flat out uvec4 outColours;

        const vec2 corners[6] = vec2[](
            vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
            vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

        void main()
        {
            vec4 clip = frame.viewProjection * vec4(inPosition, 1.0);
            vec2 offset = corners[gl_VertexIndex] * inSize * frame.viewport.z * 2.0 / frame.viewportSize.xy;
            clip.xy += offset * clip.w;
            gl_Position = clip;
            outColours = inColours;
        }
        """;

    /// <summary>
    /// The sun's vertex shader. The sun is drawn as a disc facing the camera
    /// (whose model matrix maps the unit disc onto the sun's outline, enlarged
    /// by parameters.x to leave room for the fringe), and this passes on the
    /// position within the disc in units of the sun's radius.
    /// </summary>
    public const string SunVertex = Common + """

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
    /// shifts by a pixel every two rows. colours.x and colours.y are the red
    /// and yellow, parameters.y is the random seed for the fringe, and
    /// parameters.w is the sun's radius in original pixels.
    /// </summary>
    public const string SunFragment = Common + """

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
            outColour = vec4(unpackUnorm4x8(red ? draw.colours.x : draw.colours.y).rgb, 1.0);
        }
        """;
}
