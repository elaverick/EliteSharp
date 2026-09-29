using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>How a pipeline uses the depth buffer.</summary>
public enum DepthMode
{
    /// <summary>No depth testing (the 2D HUD).</summary>
    None,

    /// <summary>Test against the depth buffer without writing to it (lines and particles).</summary>
    Test,

    /// <summary>Test against the depth buffer and write to it (solid objects).</summary>
    TestAndWrite,
}

/// <summary>The formats of the images that the pipelines draw into (with dynamic rendering).</summary>
public readonly record struct RenderTargetFormats(Format Colour, Format Depth);

/// <summary>The settings for a graphics pipeline.</summary>
public sealed class PipelineDescription
{
    public required ShaderModule VertexShader { get; init; }

    /// <summary>The fragment shader, or null for a pipeline that only writes depth.</summary>
    public ShaderModule? FragmentShader { get; init; }

    public required PrimitiveTopology Topology { get; init; }

    public required PipelineLayout Layout { get; init; }

    public VertexInputBindingDescription[] Bindings { get; init; } = [];

    public VertexInputAttributeDescription[] Attributes { get; init; } = [];

    public DepthMode Depth { get; init; }

    /// <summary>Whether to cull the back faces of triangles (for closed surfaces).</summary>
    public bool CullBackFaces { get; init; }

    /// <summary>
    /// The slope-scaled depth bias for polygons (0 for none), which offsets
    /// each polygon's depth in proportion to how steeply its depth changes
    /// across the screen. The depth is reversed, so a negative bias pushes
    /// polygons away from the camera.
    /// </summary>
    public float DepthBiasSlope { get; init; }
}

/// <summary>
/// Creates the graphics pipelines, for dynamic rendering into a colour image
/// and a depth buffer. Everything the renderers draw uses dynamic viewports,
/// scissors and line widths, and no blending (transparent pixels are discarded
/// instead). A pipeline with no fragment shader writes only depth.
/// </summary>
public static unsafe class PipelineFactory
{
    /// <summary>
    /// Which way round a triangle's corners go on the screen when it faces the
    /// camera. The meshes are wound anticlockwise when seen from outside, in
    /// the original's left-handed space, and Vulkan's screen space has y
    /// pointing down, which leaves them clockwise on the screen.
    /// </summary>
    private const FrontFace OutsideFacing = FrontFace.Clockwise;

    public static Pipeline Create(GpuDevice gpu, RenderTargetFormats targets, PipelineDescription description)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = description.VertexShader,
                PName = entryPoint,
            };
            uint stageCount = 1;
            if (description.FragmentShader is { } fragmentShader)
            {
                stages[stageCount++] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentShader,
                    PName = entryPoint,
                };
            }

            fixed (VertexInputBindingDescription* bindings = description.Bindings)
            fixed (VertexInputAttributeDescription* attributes = description.Attributes)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)description.Bindings.Length,
                    PVertexBindingDescriptions = bindings,
                    VertexAttributeDescriptionCount = (uint)description.Attributes.Length,
                    PVertexAttributeDescriptions = attributes,
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = description.Topology,
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = description.CullBackFaces ? CullModeFlags.BackBit : CullModeFlags.None,
                    FrontFace = OutsideFacing,
                    LineWidth = 1,
                    DepthBiasEnable = description.DepthBiasSlope != 0,
                    DepthBiasSlopeFactor = description.DepthBiasSlope,
                };

                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };

                // The depth buffer uses reversed depth (1 = near, 0 = far)
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = description.Depth != DepthMode.None,
                    DepthWriteEnable = description.Depth == DepthMode.TestAndWrite,
                    DepthCompareOp = CompareOp.GreaterOrEqual,
                };

                // A depth-only pipeline leaves the colour image alone
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = description.FragmentShader == null
                        ? 0
                        : ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = false,
                };

                var colourBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment,
                };

                var dynamicStates = stackalloc DynamicState[3] { DynamicState.Viewport, DynamicState.Scissor, DynamicState.LineWidth };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 3,
                    PDynamicStates = dynamicStates,
                };

                var colourFormat = targets.Colour;
                var rendering = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = 1,
                    PColorAttachmentFormats = &colourFormat,
                    DepthAttachmentFormat = targets.Depth,
                };

                var createInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &rendering,
                    StageCount = stageCount,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colourBlend,
                    PDynamicState = &dynamicState,
                    Layout = description.Layout,
                };

                GpuDevice.Check(gpu.Vk.CreateGraphicsPipelines(gpu.Device, default, 1, in createInfo, null, out var pipeline), "vkCreateGraphicsPipelines");
                return pipeline;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }
}
