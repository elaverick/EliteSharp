using System.Runtime.InteropServices;
using Silk.NET.Shaderc;

namespace EliteSharp.Rendering;

/// <summary>
/// The GLSL shaders, compiled to SPIR-V at startup using shaderc.
/// </summary>
internal static class Shaders
{
    /// <summary>
    /// The vertex shader. 2D vertices are in logical BBC pixels (256 x 248, with
    /// the origin at the top-left). 3D vertices are points in space relative to
    /// our ship, which are projected using Elite's own perspective, where screen
    /// x = 128 + 256 * x / z and screen y = 96 - 256 * y / z (as in LL9), and
    /// the GPU clips lines against the view instead of the LL145 routine.
    /// </summary>
    public const string VertexSource = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in uint inColour;
        layout(location = 2) in uint inFlags;

        layout(location = 0) flat out uint outColour;
        layout(location = 1) flat out uint outFlags;

        void main()
        {
            if ((inFlags & 2u) != 0u)
            {
                // NDC x = sx / 128 - 1 = 2x / z
                // NDC y = sy / 124 - 1 = (96 - 256y / z) / 124 - 1
                // so in clip space (with w = z):
                float z = inPosition.z;
                gl_Position = vec4(2.0 * inPosition.x,
                                   (96.0 / 124.0 - 1.0) * z - (256.0 / 124.0) * inPosition.y,
                                   0.5 * z,
                                   z);
            }
            else
            {
                gl_Position = vec4(inPosition.x / 128.0 - 1.0, inPosition.y / 124.0 - 1.0, 0.5, 1.0);
            }

            outColour = inColour;
            outFlags = inFlags;
        }
        """;

    /// <summary>
    /// The fragment shader. The colour is a BBC screen byte, which is decoded
    /// into a ULA palette index exactly as the video ULA does for the pixel's
    /// position within the byte, in mode 1 (four pixels per byte) for the space
    /// view or mode 2 (two pixels per byte) for the dashboard, and the index is
    /// then looked up in the relevant 16-entry palette. Pixels of logical colour
    /// 0 are transparent, as they don't change screen memory when EOR'd.
    /// </summary>
    public const string FragmentSource = """
        #version 450

        layout(location = 0) flat in uint inColour;
        layout(location = 1) flat in uint inFlags;

        layout(location = 0) out vec4 outColour;

        layout(push_constant) uniform PushConstants
        {
            vec2 origin;          // the top-left of the logical screen in framebuffer pixels
            vec2 scale;           // framebuffer pixels per logical pixel
            uvec2 spacePalette;   // 16 x 4-bit physical colours for the space view
            uvec2 dashPalette;    // 16 x 4-bit physical colours for the dashboard
            uint options;         // bit 0 = hyperspace effect, bit 1 = dashboard visible
        } pc;

        uint paletteEntry(uvec2 palette, uint index)
        {
            uint word = index < 8u ? palette.x : palette.y;
            return (word >> ((index & 7u) * 4u)) & 15u;
        }

        void main()
        {
            vec2 logical = (gl_FragCoord.xy - pc.origin) / pc.scale;
            bool dashboard = (inFlags & 4u) != 0u;

            if (!dashboard && logical.y >= 192.0)
            {
                discard;
            }

            if (logical.x < 0.0 || logical.x >= 256.0 || logical.y < 0.0 || logical.y >= 248.0)
            {
                discard;
            }

            uint x = uint(logical.x);
            bool mode2 = dashboard || (inFlags & 1u) != 0u || (pc.options & 1u) != 0u;
            uint shift = mode2 ? ((x >> 1) & 1u) : (x & 3u);

            // The ULA forms the palette index from bits 7, 5, 3 and 1 of the byte,
            // shifting the byte left (with 1s coming in) for each pixel
            uint value = ((inColour << shift) | ((1u << shift) - 1u)) & 255u;
            uint index = ((value >> 4u) & 8u) | ((value >> 3u) & 4u) | ((value >> 2u) & 2u) | ((value >> 1u) & 1u);

            // Work out whether this pixel has any bits set in screen memory
            uint own;
            if (mode2)
            {
                own = shift == 0u ? (inColour & 0xAAu) : (inColour & 0x55u);
            }
            else
            {
                own = inColour & (0x88u >> shift);
            }

            if (own == 0u)
            {
                discard;
            }

            uint physical = (dashboard || (pc.options & 1u) != 0u)
                ? paletteEntry(pc.dashPalette, index)
                : paletteEntry(pc.spacePalette, index);

            outColour = vec4(float(physical & 1u), float((physical >> 1u) & 1u), float((physical >> 2u) & 1u), 1.0);
        }
        """;

    public static unsafe byte[] Compile(string source, ShaderKind kind, string name)
    {
        var shaderc = Shaderc.GetApi();
        var compiler = shaderc.CompilerInitialize();
        var options = shaderc.CompileOptionsInitialize();
        try
        {
            shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            var result = shaderc.CompileIntoSpv(compiler, source, (nuint)System.Text.Encoding.UTF8.GetByteCount(source), kind, name, "main", options);
            try
            {
                if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    string message = Marshal.PtrToStringUTF8((nint)shaderc.ResultGetErrorMessage(result)) ?? "unknown error";
                    throw new InvalidOperationException($"Failed to compile shader {name}: {message}");
                }

                var length = (int)shaderc.ResultGetLength(result);
                var bytes = new byte[length];
                new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), length).CopyTo(bytes);
                return bytes;
            }
            finally
            {
                shaderc.ResultRelease(result);
            }
        }
        finally
        {
            shaderc.CompileOptionsRelease(options);
            shaderc.CompilerRelease(compiler);
        }
    }
}
