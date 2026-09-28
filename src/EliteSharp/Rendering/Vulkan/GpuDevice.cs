using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// The Vulkan device and the helpers that the renderers share for creating
/// buffers and shader modules and uploading data to the GPU.
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
