using EliteSharp.Rendering.Scene;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>How the 3D world is framed in the window.</summary>
public enum WorldFraming
{
    /// <summary>The world fills the full width of the window above the dashboard.</summary>
    Wide,

    /// <summary>The world is confined to the original's 4:3 space view.</summary>
    FourByThree,
}

/// <summary>
/// Draws frames with Vulkan 1.3. The renderer owns the device, the swapchain and
/// the depth buffer, and each frame it draws the 3D world (if there is one)
/// with the <see cref="WorldRenderer"/>, and then the 2D display over the top
/// with the <see cref="HudRenderer"/>, using dynamic rendering (so there are no
/// render passes or framebuffers) and synchronization2 barriers.
///
/// The 2D display is letterboxed into the window at the original's 256 x 248
/// proportions. The world is drawn into its own viewport, which covers the
/// same rows as the original's space view (so the HUD's crosshairs and text
/// line up with it) but, by default, the full width of the window.
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    private const int FramesInFlight = 2;

    private sealed class FrameResources
    {
        public CommandBuffer CommandBuffer;
        public VkSemaphore ImageAvailable;
        public Fence InFlight;
    }

    private readonly IWindow _window;
    private WorldFraming _framing;
    private bool _vsync;
    private readonly Vk _vk = Vk.GetApi();
    private Instance _instance;
    private KhrSurface _khrSurface = null!;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private CommandPool _commandPool;
    private GpuDevice _gpu = null!;
    private KhrSwapchain _khrSwapchain = null!;
    private SwapchainKHR _swapchain;
    private Format _swapchainFormat;
    private Format _depthFormat;
    private Extent2D _extent;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];
    private Image _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;
    private VkSemaphore[] _renderFinished = [];
    private HudRenderer _hud = null!;
    private WorldRenderer _world = null!;
    private readonly FrameResources[] _frames = new FrameResources[FramesInFlight];
    private int _currentFrame;
    private bool _wideLines;
    private float _maxLineWidth = 1;
    private bool _swapchainDirty;
    private bool _canCapture;
    private string? _capturePath;

    /// <summary>Ask the renderer to save the next frame it draws to a PNG file.</summary>
    public void RequestCapture(string path) => Volatile.Write(ref _capturePath, path);

    /// <summary>
    /// Create the renderer, loading the given ship models. With vsync, it draws
    /// one frame for each refresh of the display; without, it draws as many
    /// frames as it can.
    /// </summary>
    public VulkanRenderer(IWindow window, IEnumerable<string> shipModelPaths, WorldFraming framing = WorldFraming.Wide, bool vsync = true)
    {
        _window = window;
        _framing = framing;
        _vsync = vsync;
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateDevice();
        CreateCommandPool();
        _gpu = new GpuDevice(_vk, _physicalDevice, _device, _queue, _commandPool);
        _depthFormat = ChooseDepthFormat();
        CreateSwapchain();
        CreateDepthBuffer();
        CreateFrameResources();
        var targets = new RenderTargetFormats(_swapchainFormat, _depthFormat);
        _hud = new HudRenderer(_gpu, targets, FramesInFlight);
        _world = new WorldRenderer(_gpu, targets, FramesInFlight, shipModelPaths);
    }

    public void Resize() => _swapchainDirty = true;

    /// <summary>Whether the 3D world fills the width of the window, or the original's 4:3 frame (on the window's thread).</summary>
    public WorldFraming Framing
    {
        get => _framing;
        set => _framing = value;
    }

    /// <summary>Whether to draw one frame for each refresh of the display (on the window's thread).</summary>
    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync != value)
            {
                // The present mode is chosen with the swapchain
                _vsync = value;
                _swapchainDirty = true;
            }
        }
    }

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
            ApiVersion = Vk.Version13,
        };

        var extensions = _window.VkSurface!.GetRequiredExtensions(out uint extensionCount);
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = extensionCount,
            PpEnabledExtensionNames = extensions,
        };

        GpuDevice.Check(_vk.CreateInstance(in createInfo, null, out _instance), "vkCreateInstance");
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
                _vk.GetPhysicalDeviceProperties(device, out var properties);
                if (!supported || properties.ApiVersion < Vk.Version13 || !SupportsRequiredFeatures(device))
                {
                    continue;
                }

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

        _physicalDevice = best ?? throw new InvalidOperationException("No suitable Vulkan device found (Vulkan 1.3 is required)");
        _queueFamily = bestFamily;

        _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var features);
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var props);
        _wideLines = features.WideLines;
        _maxLineWidth = _wideLines ? props.Limits.LineWidthRange[1] : 1;
    }

    /// <summary>Whether a device supports dynamic rendering and synchronization2 (which Vulkan 1.3 requires, but check anyway).</summary>
    private bool SupportsRequiredFeatures(PhysicalDevice device)
    {
        var features13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features13 };
        _vk.GetPhysicalDeviceFeatures2(device, &features);
        return features13.DynamicRendering && features13.Synchronization2;
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

        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
        };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features13,
            Features = new PhysicalDeviceFeatures { WideLines = _wideLines },
        };
        var extensionName = (byte*)SilkMarshal.StringToPtr(KhrSwapchain.ExtensionName);
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = &extensionName,
        };

        GpuDevice.Check(_vk.CreateDevice(_physicalDevice, in createInfo, null, out _device), "vkCreateDevice");
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
        GpuDevice.Check(_vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool), "vkCreateCommandPool");
    }

    /// <summary>Pick a depth format, preferring 32-bit floating point, which works best with reversed depth.</summary>
    private Format ChooseDepthFormat()
    {
        foreach (var format in new[] { Format.D32Sfloat, Format.D24UnormS8Uint, Format.D16Unorm })
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
            {
                return format;
            }
        }

        throw new InvalidOperationException("No supported depth format");
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

        // FIFO waits for the display's refresh (and every device has it).
        // Without vsync, mailbox draws as fast as it can and shows the latest
        // frame at each refresh, without tearing; failing that, immediate shows
        // each frame straight away, which can tear
        var presentMode = _vsync ? PresentModeKHR.FifoKhr
            : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr
            : PresentModeKHR.FifoKhr;

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

        GpuDevice.Check(_khrSwapchain.CreateSwapchain(_device, in createInfo, null, out _swapchain), "vkCreateSwapchainKHR");
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
            _imageViews[i] = CreateImageView(_images[i], _swapchainFormat, ImageAspectFlags.ColorBit);
        }

        _renderFinished = new VkSemaphore[count];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            GpuDevice.Check(_vk.CreateSemaphore(_device, in semaphoreInfo, null, out _renderFinished[i]), "vkCreateSemaphore");
        }
    }

    private ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        GpuDevice.Check(_vk.CreateImageView(_device, in viewInfo, null, out var view), "vkCreateImageView");
        return view;
    }

    /// <summary>Create the depth buffer, which is shared by the frames in flight (a barrier orders their use of it).</summary>
    private void CreateDepthBuffer()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _depthFormat,
            Extent = new Extent3D(_extent.Width, _extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        GpuDevice.Check(_vk.CreateImage(_device, in imageInfo, null, out _depthImage), "vkCreateImage");

        _vk.GetImageMemoryRequirements(_device, _depthImage, out var requirements);
        var memoryInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _gpu.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        GpuDevice.Check(_vk.AllocateMemory(_device, in memoryInfo, null, out _depthMemory), "vkAllocateMemory");
        GpuDevice.Check(_vk.BindImageMemory(_device, _depthImage, _depthMemory, 0), "vkBindImageMemory");
        _depthView = CreateImageView(_depthImage, _depthFormat, ImageAspectFlags.DepthBit);
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
            GpuDevice.Check(_vk.AllocateCommandBuffers(_device, in allocInfo, p), "vkAllocateCommandBuffers");
        }

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        for (int i = 0; i < FramesInFlight; i++)
        {
            var frame = new FrameResources { CommandBuffer = commandBuffers[i] };
            GpuDevice.Check(_vk.CreateSemaphore(_device, in semaphoreInfo, null, out frame.ImageAvailable), "vkCreateSemaphore");
            GpuDevice.Check(_vk.CreateFence(_device, in fenceInfo, null, out frame.InFlight), "vkCreateFence");
            _frames[i] = frame;
        }
    }

    private void DestroySwapchain()
    {
        _vk.DestroyImageView(_device, _depthView, null);
        _vk.DestroyImage(_device, _depthImage, null);
        _vk.FreeMemory(_device, _depthMemory, null);

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
        CreateDepthBuffer();
        _swapchainDirty = false;
    }

    /// <summary>
    /// The viewport for the 3D world: the same rows as the original's space
    /// view (the top 192 of the 248 rows of the 2D display), and either the
    /// full width of the window or the width of the 2D display.
    /// </summary>
    private Rect2D WorldViewport(HudLayout layout)
    {
        int top = (int)layout.OriginY;
        uint height = (uint)MathF.Round(Hud.SpaceViewHeight * layout.Scale);
        return _framing == WorldFraming.Wide
            ? new Rect2D(new Offset2D(0, top), new Extent2D(_extent.Width, height))
            : new Rect2D(new Offset2D((int)layout.OriginX, top), new Extent2D((uint)MathF.Round(layout.Width), height));
    }

    /// <summary>
    /// The number of the original's pixels that fit into the space view beyond
    /// each side of the original's screen (zero unless the world fills the
    /// width of a window that is wider than 4:3).
    /// </summary>
    public float SideMargin
    {
        get
        {
            if (_framing != WorldFraming.Wide || _extent.Width == 0)
            {
                return 0;
            }

            float scale = HudLayout.For(_extent.Width, _extent.Height).Scale;
            return Math.Max(0, (_extent.Width / scale - Hud.Width) / 2);
        }
    }

    /// <summary>
    /// Draw a frame, with the given 3D world in place of the frame's own (such
    /// as one that is part of the way to the next frame, see
    /// <see cref="FrameInterpolator"/>).
    /// </summary>
    public void Draw(FrameData? frameData, SceneFrame? world = null)
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
            GpuDevice.Check(result, "vkAcquireNextImageKHR");
        }

        _vk.ResetFences(_device, 1, in frame.InFlight);

        var commandBuffer = frame.CommandBuffer;
        _vk.ResetCommandBuffer(commandBuffer, 0);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        GpuDevice.Check(_vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");

        var image = _images[imageIndex];
        BeginRendering(commandBuffer, image, _imageViews[imageIndex]);

        if (frameData != null)
        {
            var layout = HudLayout.For(_extent.Width, _extent.Height);
            float lineWidth = _wideLines ? Math.Clamp(layout.Scale * 0.75f, 1f, _maxLineWidth) : 1f;

            var worldViewport = WorldViewport(layout);
            if (frameData.HasWorld)
            {
                _world.Draw(commandBuffer, _currentFrame, world ?? frameData.World, worldViewport, layout.Scale, lineWidth, frameData.Palette);
            }

            // In the wide framing, the border (and the hangar) spans the whole
            // width of the window on every screen, not just the space view
            Rect2D? wideArea = null;
            if (_framing == WorldFraming.Wide)
            {
                wideArea = new Rect2D(
                    new Offset2D(worldViewport.Offset.X, (int)layout.OriginY),
                    new Extent2D(worldViewport.Extent.Width, (uint)MathF.Ceiling(layout.Height)));
            }

            _hud.Draw(commandBuffer, _currentFrame, frameData, layout, lineWidth, wideArea);
        }

        _vk.CmdEndRendering(commandBuffer);

        // Copy the image into a buffer if we have been asked for a screenshot,
        // and get it ready to present
        string? capturePath = _canCapture ? Interlocked.Exchange(ref _capturePath, null) : null;
        GpuBuffer? captureBuffer = null;
        ulong captureSize = (ulong)_extent.Width * _extent.Height * 4;
        if (capturePath != null)
        {
            captureBuffer = _gpu.CreateHostBuffer(captureSize, BufferUsageFlags.TransferDstBit);
            ColourBarrier(
                commandBuffer, image,
                ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
                ImageLayout.TransferSrcOptimal, PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.TransferSrcOptimal, captureBuffer.Buffer, 1, in region);
            ColourBarrier(
                commandBuffer, image,
                ImageLayout.TransferSrcOptimal, PipelineStageFlags2.CopyBit, AccessFlags2.None,
                ImageLayout.PresentSrcKhr, PipelineStageFlags2.None, AccessFlags2.None);
        }
        else
        {
            ColourBarrier(
                commandBuffer, image,
                ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
                ImageLayout.PresentSrcKhr, PipelineStageFlags2.None, AccessFlags2.None);
        }

        GpuDevice.Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var signalSemaphore = _renderFinished[imageIndex];
        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = frame.ImageAvailable,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = signalSemaphore,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var commandBufferInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = commandBuffer,
        };
        var submitInfo = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = 1,
            PWaitSemaphoreInfos = &waitInfo,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandBufferInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signalInfo,
        };
        GpuDevice.Check(_vk.QueueSubmit2(_queue, 1, in submitInfo, frame.InFlight), "vkQueueSubmit2");

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
            GpuDevice.Check(result, "vkQueuePresentKHR");
        }

        if (captureBuffer != null)
        {
            _vk.QueueWaitIdle(_queue);
            SaveCapture(capturePath!, new ReadOnlySpan<byte>(captureBuffer.Mapped, (int)captureSize));
            captureBuffer.Dispose();
        }

        _currentFrame = (_currentFrame + 1) % FramesInFlight;
    }

    private void SaveCapture(string path, ReadOnlySpan<byte> source)
    {
        var rgb = new byte[_extent.Width * _extent.Height * 3];
        bool bgr = _swapchainFormat is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
        for (int i = 0, j = 0; i < source.Length; i += 4, j += 3)
        {
            rgb[j] = bgr ? source[i + 2] : source[i];
            rgb[j + 1] = source[i + 1];
            rgb[j + 2] = bgr ? source[i] : source[i + 2];
        }

        PngWriter.Write(path, (int)_extent.Width, (int)_extent.Height, rgb);
    }

    /// <summary>
    /// Get the swapchain image and the depth buffer ready to draw into, and
    /// begin rendering into them, clearing the image to black and the depth
    /// buffer to 0 (the far distance, as the depth is reversed).
    /// </summary>
    private void BeginRendering(CommandBuffer commandBuffer, Image image, ImageView imageView)
    {
        // The image's old contents are discarded; the wait for the image to be
        // acquired is at the colour output stage, so the transition is too
        ColourBarrier(
            commandBuffer, image,
            ImageLayout.Undefined, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.None,
            ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

        // The depth buffer is shared by the frames in flight, so wait for the
        // previous frame's depth tests before clearing it
        var depthStages = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        Barrier(commandBuffer, new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = depthStages,
            SrcAccessMask = AccessFlags2.DepthStencilAttachmentWriteBit,
            DstStageMask = depthStages,
            DstAccessMask = AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.DepthAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _depthImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
        });

        var colourAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = imageView,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f)),
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depthView,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)),
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colourAttachment,
            PDepthAttachment = &depthAttachment,
        };
        _vk.CmdBeginRendering(commandBuffer, in renderingInfo);
    }

    /// <summary>Move a swapchain image from one layout to another, between the given stages.</summary>
    private void ColourBarrier(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout from,
        PipelineStageFlags2 srcStage,
        AccessFlags2 srcAccess,
        ImageLayout to,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess)
    {
        Barrier(commandBuffer, new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        });
    }

    private void Barrier(CommandBuffer commandBuffer, ImageMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _vk.CmdPipelineBarrier2(commandBuffer, in dependency);
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
        }

        _world.Dispose();
        _hud.Dispose();
        DestroySwapchain();
        _vk.DestroyCommandPool(_device, _commandPool, null);
        _vk.DestroyDevice(_device, null);
        _khrSurface.DestroySurface(_instance, _surface, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }
}
