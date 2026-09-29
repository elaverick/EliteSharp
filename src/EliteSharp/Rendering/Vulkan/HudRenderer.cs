using System.Runtime.InteropServices;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The position and size of the 2D display (the original's 256 x 248 screen)
/// in the window, letterboxed to keep square pixels.
/// </summary>
public readonly record struct HudLayout(float OriginX, float OriginY, float Scale)
{
    public float Width => Screen.Width * Scale;

    public float Height => Screen.Height * Scale;

    public static HudLayout For(uint windowWidth, uint windowHeight)
    {
        float scale = Math.Min(windowWidth / (float)Screen.Width, windowHeight / (float)Screen.Height);
        return new HudLayout(
            MathF.Floor((windowWidth - Screen.Width * scale) / 2),
            MathF.Floor((windowHeight - Screen.Height * scale) / 2),
            scale);
    }
}

/// <summary>
/// Draws the 2D parts of the display (the HUD): text, the dashboard, charts,
/// crosshairs and so on, in the original's screen layout, decoding the BBC's
/// colour bytes and palettes in the fragment shader.
/// </summary>
public sealed unsafe class HudRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public float OriginX, OriginY;
        public float ScaleX, ScaleY;
        public uint SpacePalette0, SpacePalette1;
        public uint DashPalette0, DashPalette1;
        public uint Options;
    }

    private readonly GpuDevice _gpu;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _linePipeline;
    private readonly Pipeline _trianglePipeline;
    private readonly DynamicBuffer[] _vertexBuffers;

    public HudRenderer(GpuDevice gpu, RenderPass renderPass, int framesInFlight)
    {
        _gpu = gpu;
        var pushRange = new PushConstantRange(ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        GpuDevice.Check(gpu.Vk.CreatePipelineLayout(gpu.Device, in layoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        var vertexShader = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.VertexSource, ShaderKind.VertexShader, "hud.vert"));
        var fragmentShader = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.FragmentSource, ShaderKind.FragmentShader, "hud.frag"));

        PipelineDescription Description(PrimitiveTopology topology) => new()
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = topology,
            Layout = _pipelineLayout,
            Bindings = [new(0, Vertex.SizeInBytes, VertexInputRate.Vertex)],
            Attributes =
            [
                new(0, 0, Format.R32G32Sfloat, 0),
                new(1, 0, Format.R32Uint, 8),
                new(2, 0, Format.R32Uint, 12),
            ],
            Depth = DepthMode.None,
        };

        _linePipeline = PipelineFactory.Create(gpu, renderPass, Description(PrimitiveTopology.LineList));
        _trianglePipeline = PipelineFactory.Create(gpu, renderPass, Description(PrimitiveTopology.TriangleList));
        gpu.Vk.DestroyShaderModule(gpu.Device, vertexShader, null);
        gpu.Vk.DestroyShaderModule(gpu.Device, fragmentShader, null);

        _vertexBuffers = new DynamicBuffer[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _vertexBuffers[i] = new DynamicBuffer(gpu, BufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>
    /// Record the commands to draw the 2D display. The wide lines (the border
    /// and the hangar) are drawn across the given area if there is one (when
    /// the 3D view is wider
    /// than the 2D display), stretching it horizontally to fit, or around the
    /// 2D display's space view otherwise.
    /// </summary>
    public void Draw(CommandBuffer commandBuffer, int frameIndex, FrameData frame, HudLayout layout, float lineWidth, Rect2D? wideArea)
    {
        int triangleCount = frame.TriangleVertexCount;
        int lineCount = frame.LineVertexCount;
        int wideCount = frame.WideLineVertexCount;
        if (triangleCount + lineCount + wideCount == 0)
        {
            return;
        }

        // Upload the vertices: triangles first, then lines, then the wide lines
        var vertices = new Vertex[triangleCount + lineCount + wideCount];
        frame.Triangles.AsSpan(0, triangleCount).CopyTo(vertices);
        frame.Lines.AsSpan(0, lineCount).CopyTo(vertices.AsSpan(triangleCount));
        frame.WideLines.AsSpan(0, wideCount).CopyTo(vertices.AsSpan(triangleCount + lineCount));
        var vertexBuffer = _vertexBuffers[frameIndex];
        vertexBuffer.Write<Vertex>(vertices);

        var vk = _gpu.Vk;
        vk.CmdSetLineWidth(commandBuffer, lineWidth);
        ulong offset = 0;
        var buffer = vertexBuffer.Buffer;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);

        var area = new Rect2D(
            new Offset2D((int)layout.OriginX, (int)layout.OriginY),
            new Extent2D((uint)MathF.Ceiling(layout.Width), (uint)MathF.Ceiling(layout.Height)));
        SetArea(commandBuffer, frame, area, layout.Scale, layout.Scale);

        if (triangleCount > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _trianglePipeline);
            vk.CmdDraw(commandBuffer, (uint)triangleCount, 1, 0, 0);
        }

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _linePipeline);
        if (lineCount > 0)
        {
            vk.CmdDraw(commandBuffer, (uint)lineCount, 1, (uint)triangleCount, 0);
        }

        if (wideCount > 0)
        {
            if (wideArea is { } stretched)
            {
                SetArea(commandBuffer, frame, stretched, stretched.Extent.Width / (float)Screen.Width, layout.Scale);
            }

            vk.CmdDraw(commandBuffer, (uint)wideCount, 1, (uint)(triangleCount + lineCount), 0);
        }
    }

    /// <summary>
    /// Map the 2D display's 256 x 248 logical pixels onto an area of the
    /// window, with the given horizontal and vertical scales.
    /// </summary>
    private void SetArea(CommandBuffer commandBuffer, FrameData frame, Rect2D area, float scaleX, float scaleY)
    {
        var vk = _gpu.Vk;
        var viewport = new Viewport(area.Offset.X, area.Offset.Y, area.Extent.Width, area.Extent.Height, 0, 1);
        vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, in area);

        var push = new PushConstants
        {
            OriginX = area.Offset.X,
            OriginY = area.Offset.Y,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Options = (frame.HyperspaceColours ? 1u : 0u) | (frame.DashboardVisible ? 2u : 0u),
        };
        PackPalette(frame.SpacePalette, out push.SpacePalette0, out push.SpacePalette1);
        PackPalette(frame.DashboardPalette, out push.DashPalette0, out push.DashPalette1);
        vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
    }

    private static void PackPalette(int[] palette, out uint low, out uint high)
    {
        low = 0;
        high = 0;
        for (int i = 0; i < 8; i++)
        {
            low |= (uint)(palette[i] & 15) << (i * 4);
            high |= (uint)(palette[i + 8] & 15) << (i * 4);
        }
    }

    public void Dispose()
    {
        foreach (var buffer in _vertexBuffers)
        {
            buffer.Dispose();
        }

        _gpu.Vk.DestroyPipeline(_gpu.Device, _linePipeline, null);
        _gpu.Vk.DestroyPipeline(_gpu.Device, _trianglePipeline, null);
        _gpu.Vk.DestroyPipelineLayout(_gpu.Device, _pipelineLayout, null);
    }
}
