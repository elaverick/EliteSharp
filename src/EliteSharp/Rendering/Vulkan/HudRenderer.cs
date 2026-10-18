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
/// uniform buffer of the current palette's inks. A full-colour picture (see
/// <see cref="HudPicture"/>) is drawn behind the space view's rectangles and
/// lines, from a texture of its own that is uploaded the first time it is
/// shown and kept until the renderer closes.
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

    /// <summary>The picture's push constants (see HudShaders.PictureCommon).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PictureConstants
    {
        public Vector4 Area;
        public Vector4 Rectangle;
    }

    /// <summary>The most pictures that can be loaded at once.</summary>
    private const int MaxPictures = 32;

    private static readonly int InkBufferSize = Inks.Count * Palette.PatternLength * sizeof(uint);

    private readonly GpuDevice _gpu;
    private readonly GpuTexture _atlas;
    private readonly DescriptorSetLayout _descriptorSetLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _quadPipeline;
    private readonly Pipeline _linePipeline;
    private readonly FrameResources[] _frames;
    private readonly DescriptorSetLayout _pictureSetLayout;
    private readonly DescriptorPool _picturePool;
    private readonly PipelineLayout _picturePipelineLayout;
    private readonly Pipeline _picturePipeline;

    /// <summary>The pictures that have been loaded (or null for one that couldn't be), by path.</summary>
    private readonly Dictionary<string, (GpuTexture Texture, DescriptorSet Set)?> _pictures = [];

    public HudRenderer(GpuDevice gpu, RenderTargetFormats targets, int framesInFlight)
    {
        _gpu = gpu;
        var vk = gpu.Vk;
        _atlas = gpu.CreateByteTexture(HudAtlas.Width, HudAtlas.Height, HudAtlas.Texels);

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

        // The pictures' pipeline, which has a texture of its own for each picture
        var pictureBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var pictureSetInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &pictureBinding,
        };
        GpuDevice.Check(vk.CreateDescriptorSetLayout(gpu.Device, in pictureSetInfo, null, out _pictureSetLayout), "vkCreateDescriptorSetLayout");

        var picturePoolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, MaxPictures);
        var picturePoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = MaxPictures,
            PoolSizeCount = 1,
            PPoolSizes = &picturePoolSize,
        };
        GpuDevice.Check(vk.CreateDescriptorPool(gpu.Device, in picturePoolInfo, null, out _picturePool), "vkCreateDescriptorPool");

        var picturePush = new PushConstantRange(ShaderStageFlags.VertexBit, 0, (uint)sizeof(PictureConstants));
        var pictureSetLayout = _pictureSetLayout;
        var pictureLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &pictureSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &picturePush,
        };
        GpuDevice.Check(vk.CreatePipelineLayout(gpu.Device, in pictureLayoutInfo, null, out _picturePipelineLayout), "vkCreatePipelineLayout");

        var pictureVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.PictureVertex, ShaderKind.VertexShader, "hud-picture.vert"));
        var pictureFragment = gpu.CreateShaderModule(ShaderCompiler.Compile(HudShaders.PictureFragment, ShaderKind.FragmentShader, "hud-picture.frag"));
        _picturePipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = pictureVertex,
            FragmentShader = pictureFragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _picturePipelineLayout,
            Depth = DepthMode.None,
            AlphaBlend = true,
        });

        foreach (var module in new[] { quadVertex, lineVertex, fragment, pictureVertex, pictureFragment })
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
    /// tunnels) and the bottom of the border beside the dashboard are drawn
    /// across the given area if there is one (when the 3D view is wider than
    /// the HUD), or across the HUD otherwise.
    /// </summary>
    public void Draw(CommandBuffer commandBuffer, int frameIndex, FrameData frame, HudLayout layout, float lineWidth, Rect2D? wideArea)
    {
        var resources = _frames[frameIndex];
        var vk = _gpu.Vk;

        // Upload this frame's inks and instances
        frame.Palette.Patterns.CopyTo(new Span<uint>(resources.Inks.Mapped, Inks.Count * Palette.PatternLength));
        int spaceQuads = frame.SpaceQuads.Count, dashboardQuads = frame.DashboardQuads.Count, spaceLines = frame.SpaceLines.Count;
        if (spaceQuads + dashboardQuads + frame.DashboardWideQuads.Count > 0)
        {
            var quads = resources.Quads.Map<HudQuad>(spaceQuads + dashboardQuads + frame.DashboardWideQuads.Count);
            CollectionsMarshal.AsSpan(frame.SpaceQuads).CopyTo(quads);
            CollectionsMarshal.AsSpan(frame.DashboardQuads).CopyTo(quads[spaceQuads..]);
            CollectionsMarshal.AsSpan(frame.DashboardWideQuads).CopyTo(quads[(spaceQuads + dashboardQuads)..]);
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

        // The space view, clipped to its rows, with its picture behind it
        SetArea(commandBuffer, area, SpaceView(area, layout), constants);
        if (frame.Picture is { } picture)
        {
            DrawPicture(commandBuffer, picture);
            vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in descriptorSet, 0, null);
        }

        DrawQuads(commandBuffer, resources, 0, spaceQuads);
        DrawLines(commandBuffer, resources, 0, spaceLines);

        // The wide lines, stretched across the widened space view
        var wide = wideArea ?? area;
        SetArea(commandBuffer, wide, SpaceView(wide, layout), constants);
        DrawLines(commandBuffer, resources, spaceLines, frame.WideLines.Count);

        // The dashboard, over the rectangles that span the window beside it
        // (which are stretched across, but have the same rows as the dashboard).
        // Those are clipped to match the top of the border, which is a line
        // between the centres of the outermost pixels, half a pixel in from
        // the edges (a pixel whose centre is exactly on the left end is part
        // of the line, and one on the right end isn't)
        if (frame.DashboardVisible)
        {
            float end = layout.Scale / 2;
            int left = Math.Max(0, (int)MathF.Ceiling(end - 0.5f));
            int right = Math.Max(0, (int)MathF.Floor(end + 0.5f));
            var besideDashboard = new Rect2D(
                new Offset2D(wide.Offset.X + left, wide.Offset.Y),
                new Extent2D((uint)Math.Max(0, (int)wide.Extent.Width - left - right), wide.Extent.Height));
            SetArea(commandBuffer, wide, besideDashboard, constants);
            DrawQuads(commandBuffer, resources, spaceQuads + dashboardQuads, frame.DashboardWideQuads.Count);
            SetArea(commandBuffer, area, area, constants);
            DrawQuads(commandBuffer, resources, spaceQuads, dashboardQuads);
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

    /// <summary>Draw a picture (in the area that has been set), loading it if it hasn't been shown before.</summary>
    private void DrawPicture(CommandBuffer commandBuffer, HudPicture picture)
    {
        if (LoadPicture(picture.Path) is not var (_, set))
        {
            return;
        }

        var vk = _gpu.Vk;
        var constants = new PictureConstants
        {
            Area = new Vector4(Hud.Width, Hud.Height, 0, 0),
            Rectangle = new Vector4(picture.X, picture.Y, picture.Width, picture.Height),
        };
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _picturePipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _picturePipelineLayout, 0, 1, in set, 0, null);
        vk.CmdPushConstants(commandBuffer, _picturePipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(PictureConstants), &constants);
        vk.CmdDraw(commandBuffer, 6, 1, 0, 0);
    }

    /// <summary>A picture's texture and descriptor set, uploading it the first time, or null if it can't be loaded.</summary>
    private (GpuTexture Texture, DescriptorSet Set)? LoadPicture(string path)
    {
        if (_pictures.TryGetValue(path, out var loaded))
        {
            return loaded;
        }

        _pictures[path] = null;
        if (_pictures.Count > MaxPictures)
        {
            return null;
        }

        PictureImage image;
        try
        {
            image = PictureImage.Load(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The picture {path} can't be shown: {e.Message}");
            return null;
        }

        var vk = _gpu.Vk;
        var texture = _gpu.CreateColourTexture(image.Width, image.Height, image.Levels);
        var setLayout = _pictureSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _picturePool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        GpuDevice.Check(vk.AllocateDescriptorSets(_gpu.Device, in allocInfo, out var set), "vkAllocateDescriptorSets");

        var imageInfo = new DescriptorImageInfo(texture.Sampler, texture.View, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        vk.UpdateDescriptorSets(_gpu.Device, 1, in write, 0, null);

        _pictures[path] = (texture, set);
        return (texture, set);
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

        foreach (var picture in _pictures.Values)
        {
            picture?.Texture.Dispose();
        }

        vk.DestroyPipeline(_gpu.Device, _picturePipeline, null);
        vk.DestroyPipelineLayout(_gpu.Device, _picturePipelineLayout, null);
        vk.DestroyDescriptorPool(_gpu.Device, _picturePool, null);
        vk.DestroyDescriptorSetLayout(_gpu.Device, _pictureSetLayout, null);
        vk.DestroyPipeline(_gpu.Device, _quadPipeline, null);
        vk.DestroyPipeline(_gpu.Device, _linePipeline, null);
        vk.DestroyPipelineLayout(_gpu.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_gpu.Device, _descriptorPool, null);
        vk.DestroyDescriptorSetLayout(_gpu.Device, _descriptorSetLayout, null);
        _atlas.Dispose();
    }
}
