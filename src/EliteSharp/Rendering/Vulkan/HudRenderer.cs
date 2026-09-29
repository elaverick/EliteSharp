using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The position and size of the HUD (the original's 256 x 248 pixel screen) in
/// the window, letterboxed to keep square pixels.
/// </summary>
public readonly record struct HudLayout(float OriginX, float OriginY, float Scale)
{
    public float Width => Hud.Width * Scale;

    public float Height => Hud.Height * Scale;

    public static HudLayout For(uint windowWidth, uint windowHeight)
    {
        float scale = Math.Min(windowWidth / (float)Hud.Width, windowHeight / (float)Hud.Height);
        return new HudLayout(
            MathF.Floor((windowWidth - Hud.Width * scale) / 2),
            MathF.Floor((windowHeight - Hud.Height * scale) / 2),
            scale);
    }
}

/// <summary>
/// Draws the HUD: text, the dashboard, the charts, the crosshairs and so on.
/// Everything is an instance of a rectangle or a line, so each part of the HUD
/// is one instanced draw from buffers that are written once per frame. The
/// images (the font, the dashboard and its bulbs) are in one texture that is
/// uploaded when the renderer starts, and the colours come from a small
/// uniform buffer of the current palette's inks.
/// </summary>
public sealed unsafe class HudRenderer : IDisposable
{
    /// <summary>The push constants (see HudShaders.Common).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LayoutConstants
    {
        public Vector2 Size;
        public Vector2 PatternOrigin;
        public float PixelSize;
    }

    /// <summary>The per-frame resources for each frame in flight.</summary>
    private sealed class FrameResources(GpuDevice gpu)
    {
        public GpuBuffer Inks = null!;
        public DescriptorSet DescriptorSet;
        public DynamicBuffer Quads = new(gpu, BufferUsageFlags.VertexBufferBit);
        public DynamicBuffer Lines = new(gpu, BufferUsageFlags.VertexBufferBit);
    }

    private static readonly int InkBufferSize = Inks.Count * Palette.PatternLength * sizeof(uint);

    private readonly GpuDevice _gpu;
    private readonly GpuTexture _atlas;
    private readonly DescriptorSetLayout _descriptorSetLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _quadPipeline;
    private readonly Pipeline _linePipeline;
    private readonly FrameResources[] _frames;

    public HudRenderer(GpuDevice gpu, RenderTargetFormats targets, int framesInFlight)
    {
        _gpu = gpu;
        var vk = gpu.Vk;
        _atlas = gpu.CreateByteTexture(HudAtlas.Width, HudAtlas.Height, HudAtlas.Build());

        // The inks (binding 0) and the atlas (binding 1)
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        GpuDevice.Check(vk.CreateDescriptorSetLayout(gpu.Device, in setLayoutInfo, null, out _descriptorSetLayout), "vkCreateDescriptorSetLayout");

        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, (uint)framesInFlight);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)framesInFlight);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)framesInFlight,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        GpuDevice.Check(vk.CreateDescriptorPool(gpu.Device, in poolInfo, null, out _descriptorPool), "vkCreateDescriptorPool");

        var pushRange = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(LayoutConstants));
        var descriptorSetLayout = _descriptorSetLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        GpuDevice.Check(vk.CreatePipelineLayout(gpu.Device, in layoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        // The pipelines, whose vertex data is one instance per rectangle or line
        var quadVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.QuadVertex, ShaderKind.VertexShader, "hud-quad.vert"));
        var lineVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.LineVertex, ShaderKind.VertexShader, "hud-line.vert"));
        var fragment = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.Fragment, ShaderKind.FragmentShader, "hud.frag"));

        _quadPipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = quadVertex,
            FragmentShader = fragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = [new(0, (uint)sizeof(HudQuad), VertexInputRate.Instance)],
            Attributes =
            [
                new(0, 0, Format.R32G32B32A32Sfloat, 0),
                new(1, 0, Format.R32G32B32A32Sfloat, 16),
                new(2, 0, Format.R32Uint, 32),
            ],
            Depth = DepthMode.None,
        });

        _linePipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = lineVertex,
            FragmentShader = fragment,
            Topology = PrimitiveTopology.LineList,
            Layout = _pipelineLayout,
            Bindings = [new(0, (uint)sizeof(HudLine), VertexInputRate.Instance)],
            Attributes =
            [
                new(0, 0, Format.R32G32B32A32Sfloat, 0),
                new(1, 0, Format.R32Uint, 16),
            ],
            Depth = DepthMode.None,
        });

        foreach (var module in new[] { quadVertex, lineVertex, fragment })
        {
            vk.DestroyShaderModule(gpu.Device, module, null);
        }

        // The per-frame resources
        var writes = stackalloc WriteDescriptorSet[2];
        _frames = new FrameResources[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            var frame = new FrameResources(gpu)
            {
                Inks = gpu.CreateHostBuffer((ulong)InkBufferSize, BufferUsageFlags.UniformBufferBit),
            };

            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &descriptorSetLayout,
            };
            GpuDevice.Check(vk.AllocateDescriptorSets(gpu.Device, in allocInfo, out frame.DescriptorSet), "vkAllocateDescriptorSets");

            var bufferInfo = new DescriptorBufferInfo(frame.Inks.Buffer, 0, (ulong)InkBufferSize);
            var imageInfo = new DescriptorImageInfo(_atlas.Sampler, _atlas.View, ImageLayout.ShaderReadOnlyOptimal);
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.DescriptorSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.DescriptorSet,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo,
            };
            vk.UpdateDescriptorSets(gpu.Device, 2, writes, 0, null);
            _frames[i] = frame;
        }
    }

    /// <summary>
    /// Record the commands to draw the HUD. The wide lines (the border and the
    /// tunnels) are drawn across the given area if there is one (when the 3D
    /// view is wider than the HUD), or across the HUD's space view otherwise.
    /// </summary>
    public void Draw(CommandBuffer commandBuffer, int frameIndex, FrameData frame, HudLayout layout, float lineWidth, Rect2D? wideArea)
    {
        var resources = _frames[frameIndex];
        var vk = _gpu.Vk;

        // Upload this frame's inks and instances
        frame.Palette.Patterns.CopyTo(new Span<uint>(resources.Inks.Mapped, Inks.Count * Palette.PatternLength));
        int spaceQuads = frame.SpaceQuads.Count, spaceLines = frame.SpaceLines.Count;
        if (spaceQuads + frame.DashboardQuads.Count > 0)
        {
            var quads = resources.Quads.Map<HudQuad>(spaceQuads + frame.DashboardQuads.Count);
            CollectionsMarshal.AsSpan(frame.SpaceQuads).CopyTo(quads);
            CollectionsMarshal.AsSpan(frame.DashboardQuads).CopyTo(quads[spaceQuads..]);
        }

        if (spaceLines + frame.WideLines.Count > 0)
        {
            var lines = resources.Lines.Map<HudLine>(spaceLines + frame.WideLines.Count);
            CollectionsMarshal.AsSpan(frame.SpaceLines).CopyTo(lines);
            CollectionsMarshal.AsSpan(frame.WideLines).CopyTo(lines[spaceLines..]);
        }

        var descriptorSet = resources.DescriptorSet;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in descriptorSet, 0, null);
        vk.CmdSetLineWidth(commandBuffer, lineWidth);

        var area = new Rect2D(
            new Offset2D((int)layout.OriginX, (int)layout.OriginY),
            new Extent2D((uint)MathF.Ceiling(layout.Width), (uint)MathF.Ceiling(layout.Height)));
        var constants = new LayoutConstants
        {
            Size = new Vector2(Hud.Width, Hud.Height),
            PatternOrigin = new Vector2(layout.OriginX, layout.OriginY),
            PixelSize = layout.Scale,
        };

        // The space view, clipped to its rows
        SetArea(commandBuffer, area, SpaceView(area, layout), constants);
        DrawQuads(commandBuffer, resources, 0, spaceQuads);
        DrawLines(commandBuffer, resources, 0, spaceLines);

        // The wide lines, stretched across the widened space view
        var wide = wideArea ?? area;
        SetArea(commandBuffer, wide, SpaceView(wide, layout), constants);
        DrawLines(commandBuffer, resources, spaceLines, frame.WideLines.Count);

        // The dashboard
        if (frame.DashboardVisible)
        {
            SetArea(commandBuffer, area, area, constants);
            DrawQuads(commandBuffer, resources, spaceQuads, frame.DashboardQuads.Count);
        }
    }

    /// <summary>The space view's rows of an area (the rows above the dashboard).</summary>
    private static Rect2D SpaceView(Rect2D area, HudLayout layout) =>
        new(area.Offset, new Extent2D(area.Extent.Width, (uint)MathF.Ceiling(Hud.SpaceViewHeight * layout.Scale)));

    /// <summary>Map the HUD's layout onto an area of the window, clipped to the given rectangle.</summary>
    private void SetArea(CommandBuffer commandBuffer, Rect2D area, Rect2D clip, LayoutConstants constants)
    {
        var vk = _gpu.Vk;
        var viewport = new Viewport(area.Offset.X, area.Offset.Y, area.Extent.Width, area.Extent.Height, 0, 1);
        vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, in clip);
        vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(LayoutConstants), &constants);
    }

    private void DrawQuads(CommandBuffer commandBuffer, FrameResources resources, int first, int count)
    {
        if (count == 0)
        {
            return;
        }

        var vk = _gpu.Vk;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _quadPipeline);
        ulong offset = 0;
        var buffer = resources.Quads.Buffer;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
        vk.CmdDraw(commandBuffer, 6, (uint)count, 0, (uint)first);
    }

    private void DrawLines(CommandBuffer commandBuffer, FrameResources resources, int first, int count)
    {
        if (count == 0)
        {
            return;
        }

        var vk = _gpu.Vk;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _linePipeline);
        ulong offset = 0;
        var buffer = resources.Lines.Buffer;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
        vk.CmdDraw(commandBuffer, 2, (uint)count, 0, (uint)first);
    }

    public void Dispose()
    {
        var vk = _gpu.Vk;
        foreach (var frame in _frames)
        {
            frame.Inks.Dispose();
            frame.Quads.Dispose();
            frame.Lines.Dispose();
        }

        vk.DestroyPipeline(_gpu.Device, _quadPipeline, null);
        vk.DestroyPipeline(_gpu.Device, _linePipeline, null);
        vk.DestroyPipelineLayout(_gpu.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_gpu.Device, _descriptorPool, null);
        vk.DestroyDescriptorSetLayout(_gpu.Device, _descriptorSetLayout, null);
        _atlas.Dispose();
    }
}
