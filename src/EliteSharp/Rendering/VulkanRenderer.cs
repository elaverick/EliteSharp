using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace EliteSharp.Rendering;

/// <summary>
/// Draws frames of Elite's display with Vulkan. Everything is drawn with two
/// pipelines: one for line lists (the wireframe ships, planets, charts and so
/// on) and one for triangle lists (text, the dashboard and filled shapes).
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    private const int FramesInFlight = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public float OriginX, OriginY;
        public float ScaleX, ScaleY;
        public uint SpacePalette0, SpacePalette1;
        public uint DashPalette0, DashPalette1;
        public uint Options;
    }

    private sealed class FrameResources
    {
        public CommandBuffer CommandBuffer;
        public VkSemaphore ImageAvailable;
        public Fence InFlight;
        public VkBuffer VertexBuffer;
        public DeviceMemory VertexMemory;
        public void* Mapped;
        public ulong Capacity;
    }

    private readonly IWindow _window;
    private readonly Vk _vk = Vk.GetApi();
    private Instance _instance;
    private KhrSurface _khrSurface = null!;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private KhrSwapchain _khrSwapchain = null!;
    private SwapchainKHR _swapchain;
    private Format _swapchainFormat;
    private Extent2D _extent;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];
    private Framebuffer[] _framebuffers = [];
    private VkSemaphore[] _renderFinished = [];
    private RenderPass _renderPass;
    private PipelineLayout _pipelineLayout;
    private Pipeline _linePipeline;
    private Pipeline _trianglePipeline;
    private CommandPool _commandPool;
    private readonly FrameResources[] _frames = new FrameResources[FramesInFlight];
    private int _currentFrame;
    private bool _wideLines;
    private float _maxLineWidth = 1;
    private bool _swapchainDirty;
    private bool _canCapture;
    private string? _capturePath;

    /// <summary>Ask the renderer to save the next frame it draws to a PNG file.</summary>
    public void RequestCapture(string path) => Volatile.Write(ref _capturePath, path);

    public VulkanRenderer(IWindow window)
    {
        _window = window;
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateDevice();
        CreateCommandPool();
        CreateSwapchain();
        CreateRenderPass();
        CreatePipelines();
        CreateFramebuffers();
        CreateFrameResources();
    }

    public void Resize() => _swapchainDirty = true;

    private void CreateInstance()
    {
        var appName = (byte*)SilkMarshal.StringToPtr("EliteSharp");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = appName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version11,
        };

        var extensions = _window.VkSurface!.GetRequiredExtensions(out uint extensionCount);
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = extensionCount,
            PpEnabledExtensionNames = extensions,
        };

        Check(_vk.CreateInstance(in createInfo, null, out _instance), "vkCreateInstance");
        SilkMarshal.Free((nint)appName);

        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
        {
            throw new InvalidOperationException("VK_KHR_surface is not available");
        }
    }

    private void CreateSurface()
    {
        _surface = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0)
        {
            throw new InvalidOperationException("No Vulkan devices found");
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            _vk.EnumeratePhysicalDevices(_instance, ref count, p);
        }

        // Prefer a discrete GPU, but take anything with a graphics queue that
        // can present to our surface
        PhysicalDevice? best = null;
        uint bestFamily = 0;
        int bestScore = -1;
        foreach (var device in devices)
        {
            uint familyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* p = families)
            {
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, p);
            }

            for (uint i = 0; i < familyCount; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
                {
                    continue;
                }

                _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out var supported);
                if (!supported)
                {
                    continue;
                }

                _vk.GetPhysicalDeviceProperties(device, out var properties);
                int score = properties.DeviceType switch
                {
                    PhysicalDeviceType.DiscreteGpu => 3,
                    PhysicalDeviceType.IntegratedGpu => 2,
                    _ => 1,
                };

                if (score > bestScore)
                {
                    best = device;
                    bestFamily = i;
                    bestScore = score;
                }

                break;
            }
        }

        _physicalDevice = best ?? throw new InvalidOperationException("No suitable Vulkan device found");
        _queueFamily = bestFamily;

        _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var features);
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var props);
        _wideLines = features.WideLines;
        _maxLineWidth = _wideLines ? props.Limits.LineWidthRange[1] : 1;
    }

    private void CreateDevice()
    {
        float priority = 1;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var features = new PhysicalDeviceFeatures { WideLines = _wideLines };
        var extensionName = (byte*)SilkMarshal.StringToPtr(KhrSwapchain.ExtensionName);
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            PEnabledFeatures = &features,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = &extensionName,
        };

        Check(_vk.CreateDevice(_physicalDevice, in createInfo, null, out _device), "vkCreateDevice");
        SilkMarshal.Free((nint)extensionName);

        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
        {
            throw new InvalidOperationException("VK_KHR_swapchain is not available");
        }
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(_vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool), "vkCreateCommandPool");
    }

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* p = formats)
        {
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref formatCount, p);
        }

        // The BBC's colours are pure primaries, so a UNORM format gives the most
        // faithful result, though any format will do
        var format = formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm, formats[0]);

        uint modeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref modeCount, null);
        var modes = new PresentModeKHR[modeCount];
        fixed (PresentModeKHR* p = modes)
        {
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref modeCount, p);
        }

        var presentMode = modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;

        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            _extent = capabilities.CurrentExtent;
        }
        else
        {
            var size = _window.FramebufferSize;
            _extent = new Extent2D(
                Math.Clamp((uint)size.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp((uint)size.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        }

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit
                         | (capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit),
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
        };

        Check(_khrSwapchain.CreateSwapchain(_device, in createInfo, null, out _swapchain), "vkCreateSwapchainKHR");
        _swapchainFormat = format.Format;
        _canCapture = (capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;

        uint count = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref count, null);
        _images = new Image[count];
        fixed (Image* p = _images)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref count, p);
        }

        _imageViews = new ImageView[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            Check(_vk.CreateImageView(_device, in viewInfo, null, out _imageViews[i]), "vkCreateImageView");
        }

        _renderFinished = new VkSemaphore[count];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            Check(_vk.CreateSemaphore(_device, in semaphoreInfo, null, out _renderFinished[i]), "vkCreateSemaphore");
        }
    }

    private void CreateRenderPass()
    {
        var attachment = new AttachmentDescription
        {
            Format = _swapchainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        var reference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &reference,
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        var createInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        Check(_vk.CreateRenderPass(_device, in createInfo, null, out _renderPass), "vkCreateRenderPass");
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* p = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            Check(_vk.CreateShaderModule(_device, in createInfo, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    private void CreatePipelines()
    {
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstants),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Check(_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        var vertexModule = CreateShaderModule(Shaders.Compile(Shaders.VertexSource, ShaderKind.VertexShader, "elite.vert"));
        var fragmentModule = CreateShaderModule(Shaders.Compile(Shaders.FragmentSource, ShaderKind.FragmentShader, "elite.frag"));

        _linePipeline = CreatePipeline(vertexModule, fragmentModule, PrimitiveTopology.LineList);
        _trianglePipeline = CreatePipeline(vertexModule, fragmentModule, PrimitiveTopology.TriangleList);

        _vk.DestroyShaderModule(_device, vertexModule, null);
        _vk.DestroyShaderModule(_device, fragmentModule, null);
    }

    private Pipeline CreatePipeline(ShaderModule vertexModule, ShaderModule fragmentModule, PrimitiveTopology topology)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexModule,
            PName = entryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragmentModule,
            PName = entryPoint,
        };

        var binding = new VertexInputBindingDescription(0, Vertex.SizeInBytes, VertexInputRate.Vertex);
        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
        attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32Uint, 12);
        attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32Uint, 16);

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attributes,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = topology,
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
            PColorBlendState = &colourBlend,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = _renderPass,
            Subpass = 0,
        };

        Check(_vk.CreateGraphicsPipelines(_device, default, 1, in createInfo, null, out var pipeline), "vkCreateGraphicsPipelines");
        SilkMarshal.Free((nint)entryPoint);
        return pipeline;
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_imageViews.Length];
        for (int i = 0; i < _imageViews.Length; i++)
        {
            var view = _imageViews[i];
            var createInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1,
            };
            Check(_vk.CreateFramebuffer(_device, in createInfo, null, out _framebuffers[i]), "vkCreateFramebuffer");
        }
    }

    private void CreateFrameResources()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = FramesInFlight,
        };

        var commandBuffers = new CommandBuffer[FramesInFlight];
        fixed (CommandBuffer* p = commandBuffers)
        {
            Check(_vk.AllocateCommandBuffers(_device, in allocInfo, p), "vkAllocateCommandBuffers");
        }

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        for (int i = 0; i < FramesInFlight; i++)
        {
            var frame = new FrameResources { CommandBuffer = commandBuffers[i] };
            Check(_vk.CreateSemaphore(_device, in semaphoreInfo, null, out frame.ImageAvailable), "vkCreateSemaphore");
            Check(_vk.CreateFence(_device, in fenceInfo, null, out frame.InFlight), "vkCreateFence");
            _frames[i] = frame;
            EnsureVertexCapacity(frame, 64 * 1024);
        }
    }

    private void EnsureVertexCapacity(FrameResources frame, ulong bytes)
    {
        if (frame.Capacity >= bytes)
        {
            return;
        }

        ulong capacity = Math.Max(bytes, frame.Capacity * 2);
        if (frame.Capacity > 0)
        {
            _vk.UnmapMemory(_device, frame.VertexMemory);
            _vk.DestroyBuffer(_device, frame.VertexBuffer, null);
            _vk.FreeMemory(_device, frame.VertexMemory, null);
        }

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = capacity,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, in bufferInfo, null, out frame.VertexBuffer), "vkCreateBuffer");

        _vk.GetBufferMemoryRequirements(_device, frame.VertexBuffer, out var requirements);
        var memoryInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        Check(_vk.AllocateMemory(_device, in memoryInfo, null, out frame.VertexMemory), "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, frame.VertexBuffer, frame.VertexMemory, 0), "vkBindBufferMemory");

        void* mapped;
        Check(_vk.MapMemory(_device, frame.VertexMemory, 0, capacity, 0, &mapped), "vkMapMemory");
        frame.Mapped = mapped;
        frame.Capacity = capacity;
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memory);
        for (int i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException("No suitable memory type");
    }

    private void DestroySwapchain()
    {
        foreach (var framebuffer in _framebuffers)
        {
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        }

        foreach (var view in _imageViews)
        {
            _vk.DestroyImageView(_device, view, null);
        }

        foreach (var semaphore in _renderFinished)
        {
            _vk.DestroySemaphore(_device, semaphore, null);
        }

        _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
    }

    private void RecreateSwapchain()
    {
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0)
        {
            return;
        }

        _vk.DeviceWaitIdle(_device);
        DestroySwapchain();
        CreateSwapchain();
        CreateFramebuffers();
        _swapchainDirty = false;
    }

    /// <summary>Draw a frame.</summary>
    public void Draw(FrameData? frameData)
    {
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0)
        {
            return;
        }

        if (_swapchainDirty)
        {
            RecreateSwapchain();
        }

        var frame = _frames[_currentFrame];
        _vk.WaitForFences(_device, 1, in frame.InFlight, true, ulong.MaxValue);

        uint imageIndex = 0;
        var result = _khrSwapchain.AcquireNextImage(_device, _swapchain, ulong.MaxValue, frame.ImageAvailable, default, ref imageIndex);
        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }

        if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            Check(result, "vkAcquireNextImageKHR");
        }

        _vk.ResetFences(_device, 1, in frame.InFlight);

        // Upload the vertices: triangles first, then lines
        int triangleCount = frameData?.TriangleVertexCount ?? 0;
        int lineCount = frameData?.LineVertexCount ?? 0;
        ulong bytes = (ulong)(triangleCount + lineCount) * Vertex.SizeInBytes;
        EnsureVertexCapacity(frame, Math.Max(bytes, 1));
        if (frameData != null)
        {
            var destination = new Span<Vertex>(frame.Mapped, triangleCount + lineCount);
            frameData.Triangles.AsSpan(0, triangleCount).CopyTo(destination);
            frameData.Lines.AsSpan(0, lineCount).CopyTo(destination[triangleCount..]);
        }

        var commandBuffer = frame.CommandBuffer;
        _vk.ResetCommandBuffer(commandBuffer, 0);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");

        var clear = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f));
        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[imageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        _vk.CmdBeginRenderPass(commandBuffer, in renderPassInfo, SubpassContents.Inline);

        if (frameData != null && triangleCount + lineCount > 0)
        {
            // Letterbox the 256 x 248 logical screen into the window, keeping
            // square pixels
            float scale = Math.Min(_extent.Width / 256f, _extent.Height / 248f);
            float width = 256 * scale;
            float height = 248 * scale;
            float originX = MathF.Floor((_extent.Width - width) / 2);
            float originY = MathF.Floor((_extent.Height - height) / 2);

            var viewport = new Viewport(originX, originY, width, height, 0, 1);
            _vk.CmdSetViewport(commandBuffer, 0, 1, in viewport);
            var scissor = new Rect2D(new Offset2D((int)originX, (int)originY), new Extent2D((uint)MathF.Ceiling(width), (uint)MathF.Ceiling(height)));
            _vk.CmdSetScissor(commandBuffer, 0, 1, in scissor);
            _vk.CmdSetLineWidth(commandBuffer, _wideLines ? Math.Clamp(scale * 0.75f, 1f, _maxLineWidth) : 1f);

            var push = new PushConstants
            {
                OriginX = originX,
                OriginY = originY,
                ScaleX = scale,
                ScaleY = scale,
                Options = (frameData.HyperspaceColours ? 1u : 0u) | (frameData.DashboardVisible ? 2u : 0u),
            };
            PackPalette(frameData.SpacePalette, out push.SpacePalette0, out push.SpacePalette1);
            PackPalette(frameData.DashboardPalette, out push.DashPalette0, out push.DashPalette1);
            _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);

            ulong offset = 0;
            var buffer = frame.VertexBuffer;
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);

            if (triangleCount > 0)
            {
                _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _trianglePipeline);
                _vk.CmdDraw(commandBuffer, (uint)triangleCount, 1, 0, 0);
            }

            if (lineCount > 0)
            {
                _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _linePipeline);
                _vk.CmdDraw(commandBuffer, (uint)lineCount, 1, (uint)triangleCount, 0);
            }
        }

        _vk.CmdEndRenderPass(commandBuffer);

        // Copy the image into a buffer if we have been asked for a screenshot
        string? capturePath = _canCapture ? Interlocked.Exchange(ref _capturePath, null) : null;
        VkBuffer captureBuffer = default;
        DeviceMemory captureMemory = default;
        ulong captureSize = (ulong)_extent.Width * _extent.Height * 4;
        if (capturePath != null)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = captureSize,
                Usage = BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, in bufferInfo, null, out captureBuffer), "vkCreateBuffer");
            _vk.GetBufferMemoryRequirements(_device, captureBuffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
            };
            Check(_vk.AllocateMemory(_device, in memoryInfo, null, out captureMemory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, captureBuffer, captureMemory, 0), "vkBindBufferMemory");

            TransitionImage(commandBuffer, _images[imageIndex], ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyImageToBuffer(commandBuffer, _images[imageIndex], ImageLayout.TransferSrcOptimal, captureBuffer, 1, in region);
            TransitionImage(commandBuffer, _images[imageIndex], ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr);
        }

        Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var waitSemaphore = frame.ImageAvailable;
        var signalSemaphore = _renderFinished[imageIndex];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submitInfo, frame.InFlight), "vkQueueSubmit");

        var swapchain = _swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        result = _khrSwapchain.QueuePresent(_queue, in presentInfo);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
        {
            _swapchainDirty = true;
        }
        else if (result != Result.Success)
        {
            Check(result, "vkQueuePresentKHR");
        }

        if (capturePath != null)
        {
            _vk.QueueWaitIdle(_queue);
            void* mapped;
            _vk.MapMemory(_device, captureMemory, 0, captureSize, 0, &mapped);
            var source = new ReadOnlySpan<byte>(mapped, (int)captureSize);
            var rgb = new byte[_extent.Width * _extent.Height * 3];
            bool bgr = _swapchainFormat is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
            for (int i = 0, j = 0; i < source.Length; i += 4, j += 3)
            {
                rgb[j] = bgr ? source[i + 2] : source[i];
                rgb[j + 1] = source[i + 1];
                rgb[j + 2] = bgr ? source[i] : source[i + 2];
            }

            _vk.UnmapMemory(_device, captureMemory);
            _vk.DestroyBuffer(_device, captureBuffer, null);
            _vk.FreeMemory(_device, captureMemory, null);
            PngWriter.Write(capturePath, (int)_extent.Width, (int)_extent.Height, rgb);
        }

        _currentFrame = (_currentFrame + 1) % FramesInFlight;
    }

    private void TransitionImage(CommandBuffer commandBuffer, Image image, ImageLayout from, ImageLayout to)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = from == ImageLayout.TransferSrcOptimal ? AccessFlags.TransferReadBit : AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = to == ImageLayout.TransferSrcOptimal ? AccessFlags.TransferReadBit : 0,
        };
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, in barrier);
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

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }

    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);

        foreach (var frame in _frames)
        {
            if (frame == null)
            {
                continue;
            }

            _vk.DestroySemaphore(_device, frame.ImageAvailable, null);
            _vk.DestroyFence(_device, frame.InFlight, null);
            if (frame.Capacity > 0)
            {
                _vk.UnmapMemory(_device, frame.VertexMemory);
                _vk.DestroyBuffer(_device, frame.VertexBuffer, null);
                _vk.FreeMemory(_device, frame.VertexMemory, null);
            }
        }

        DestroySwapchain();
        _vk.DestroyPipeline(_device, _linePipeline, null);
        _vk.DestroyPipeline(_device, _trianglePipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);
        _vk.DestroyDevice(_device, null);
        _khrSurface.DestroySurface(_instance, _surface, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }
}
