using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The Vulkan device and the helpers that the renderers share for creating
/// buffers, textures and shader modules and uploading data to the GPU.
/// </summary>
public sealed unsafe class GpuDevice(Vk vk, PhysicalDevice physicalDevice, Device device, Queue queue, CommandPool commandPool)
{
    public Vk Vk { get; } = vk;

    public PhysicalDevice PhysicalDevice { get; } = physicalDevice;

    public Device Device { get; } = device;

    public Queue Queue { get; } = queue;

    public CommandPool CommandPool { get; } = commandPool;

    /// <summary>Find a memory type with the given properties that a resource can use.</summary>
    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
        for (int i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException("No suitable memory type");
    }

    /// <summary>Create a buffer that the CPU can write to directly, and keep it mapped.</summary>
    public GpuBuffer CreateHostBuffer(ulong size, BufferUsageFlags usage) =>
        CreateBuffer(size, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true);

    /// <summary>
    /// Create a buffer in device-local memory containing the given data, by
    /// copying it through a temporary staging buffer (used for geometry that
    /// never changes, such as the ship models).
    /// </summary>
    public GpuBuffer CreateDeviceBuffer<T>(ReadOnlySpan<T> data, BufferUsageFlags usage)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        var buffer = CreateBuffer(size, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit, map: false);
        using var staging = CreateHostBuffer(size, BufferUsageFlags.TransferSrcBit);
        data.CopyTo(new Span<T>(staging.Mapped, data.Length));

        RunOnce(commandBuffer =>
        {
            var region = new BufferCopy(0, 0, size);
            Vk.CmdCopyBuffer(commandBuffer, staging.Buffer, buffer.Buffer, 1, in region);
        });

        return buffer;
    }

    /// <summary>
    /// Create a texture of one-byte unsigned integer texels containing the
    /// given data (row by row), by copying it through a staging buffer, ready
    /// for shaders to read with texelFetch.
    /// </summary>
    public GpuTexture CreateByteTexture(int width, int height, ReadOnlySpan<byte> texels)
    {
        const Format format = Format.R8Uint;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(Vk.CreateImage(Device, in imageInfo, null, out var image), "vkCreateImage");

        Vk.GetImageMemoryRequirements(Device, image, out var requirements);
        var memoryInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(Vk.AllocateMemory(Device, in memoryInfo, null, out var memory), "vkAllocateMemory");
        Check(Vk.BindImageMemory(Device, image, memory, 0), "vkBindImageMemory");

        using var staging = CreateHostBuffer((ulong)texels.Length, BufferUsageFlags.TransferSrcBit);
        texels.CopyTo(new Span<byte>(staging.Mapped, texels.Length));

        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        RunOnce(commandBuffer =>
        {
            void Transition(ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
            {
                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = from,
                    NewLayout = to,
                    SrcAccessMask = srcAccess,
                    DstAccessMask = dstAccess,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = range,
                };
                Vk.CmdPipelineBarrier(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
            }

            Transition(ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };
            Vk.CmdCopyBufferToImage(commandBuffer, staging.Buffer, image, ImageLayout.TransferDstOptimal, 1, in region);
            Transition(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
        });

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = range,
        };
        Check(Vk.CreateImageView(Device, in viewInfo, null, out var view), "vkCreateImageView");

        // Integer textures can only be read without filtering
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        Check(Vk.CreateSampler(Device, in samplerInfo, null, out var sampler), "vkCreateSampler");
        return new GpuTexture(this, image, memory, view, sampler);
    }

    private GpuBuffer CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, bool map)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = Math.Max(size, 16),
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(Vk.CreateBuffer(Device, in bufferInfo, null, out var buffer), "vkCreateBuffer");

        Vk.GetBufferMemoryRequirements(Device, buffer, out var requirements);
        var memoryInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties),
        };
        Check(Vk.AllocateMemory(Device, in memoryInfo, null, out var memory), "vkAllocateMemory");
        Check(Vk.BindBufferMemory(Device, buffer, memory, 0), "vkBindBufferMemory");

        void* mapped = null;
        if (map)
        {
            Check(Vk.MapMemory(Device, memory, 0, bufferInfo.Size, 0, &mapped), "vkMapMemory");
        }

        return new GpuBuffer(this, buffer, memory, bufferInfo.Size, mapped);
    }

    /// <summary>Record and run a one-off command buffer, waiting for it to finish.</summary>
    public void RunOnce(Action<CommandBuffer> record)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(Vk.AllocateCommandBuffers(Device, in allocInfo, out var commandBuffer), "vkAllocateCommandBuffers");

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(Vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
        record(commandBuffer);
        Check(Vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(Vk.QueueSubmit(Queue, 1, in submitInfo, default), "vkQueueSubmit");
        Vk.QueueWaitIdle(Queue);
        Vk.FreeCommandBuffers(Device, CommandPool, 1, in commandBuffer);
    }

    public ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* p = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            Check(Vk.CreateShaderModule(Device, in createInfo, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    public static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }
}

/// <summary>A Vulkan buffer and its memory (mapped, if the CPU writes to it directly).</summary>
public sealed unsafe class GpuBuffer(GpuDevice gpu, VkBuffer buffer, DeviceMemory memory, ulong size, void* mapped) : IDisposable
{
    public VkBuffer Buffer { get; } = buffer;

    public ulong Size { get; } = size;

    /// <summary>The CPU address of the buffer's memory, or null if it isn't mapped.</summary>
    public void* Mapped { get; } = mapped;

    public void Dispose()
    {
        if (Mapped != null)
        {
            gpu.Vk.UnmapMemory(gpu.Device, memory);
        }

        gpu.Vk.DestroyBuffer(gpu.Device, Buffer, null);
        gpu.Vk.FreeMemory(gpu.Device, memory, null);
    }
}

/// <summary>A texture on the GPU: its image, memory, view and sampler.</summary>
public sealed unsafe class GpuTexture(GpuDevice gpu, Image image, DeviceMemory memory, ImageView view, Sampler sampler) : IDisposable
{
    public ImageView View { get; } = view;

    public Sampler Sampler { get; } = sampler;

    public void Dispose()
    {
        gpu.Vk.DestroySampler(gpu.Device, Sampler, null);
        gpu.Vk.DestroyImageView(gpu.Device, View, null);
        gpu.Vk.DestroyImage(gpu.Device, image, null);
        gpu.Vk.FreeMemory(gpu.Device, memory, null);
    }
}

/// <summary>
/// A host-visible buffer for data that changes every frame, which grows as
/// needed (one per frame in flight, so the CPU never writes to a buffer the
/// GPU is still reading).
/// </summary>
public sealed unsafe class DynamicBuffer(GpuDevice gpu, BufferUsageFlags usage) : IDisposable
{
    private GpuBuffer? _buffer;

    public Silk.NET.Vulkan.Buffer Buffer => _buffer!.Buffer;

    /// <summary>Copy data into the buffer, growing it first if necessary.</summary>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        if (_buffer == null || _buffer.Size < size)
        {
            // The renderer waits for this frame's previous submission to finish
            // before reusing its buffers, so the old buffer is no longer in use
            _buffer?.Dispose();
            _buffer = gpu.CreateHostBuffer(Math.Max(size * 2, 64 * 1024), usage);
        }

        data.CopyTo(new Span<T>(_buffer.Mapped, data.Length));
    }

    public void Dispose() => _buffer?.Dispose();
}
