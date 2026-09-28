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

/// <summary>The settings for a graphics pipeline.</summary>
public sealed class PipelineDescription
{
    public required ShaderModule VertexShader { get; init; }

    public required ShaderModule FragmentShader { get; init; }

    public required PrimitiveTopology Topology { get; init; }

    public required PipelineLayout Layout { get; init; }

    public VertexInputBindingDescription[] Bindings { get; init; } = [];

    public VertexInputAttributeDescription[] Attributes { get; init; } = [];

    public DepthMode Depth { get; init; }
}

/// <summary>
/// Creates the graphics pipelines. Everything the renderers draw uses dynamic
/// viewports, scissors and line widths, no face culling (the wireframes have
/// their own hidden line removal) and no blending (transparent pixels are
/// discarded instead).
/// </summary>
public static unsafe class PipelineFactory
{
    public static Pipeline Create(GpuDevice gpu, RenderPass renderPass, PipelineDescription description)
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
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = description.FragmentShader,
                PName = entryPoint,
            };

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
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.Clockwise,
                    LineWidth = 1,
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

                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
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

                var createInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
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
                    RenderPass = renderPass,
                    Subpass = 0,
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
