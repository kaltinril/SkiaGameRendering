using System.Runtime.InteropServices;

namespace SkiaGameRendering.Core.VK
{
    /// <summary>
    /// Raw <c>VkImageLayout</c>, pipeline-stage, access, usage and aspect values host adapters need
    /// when describing a wrapped image to <see cref="VkSkiaSurfaceFactory.CreateTextureState"/> or
    /// driving <see cref="VkImageLayoutTransitioner"/>. Named here so adapters do not each redeclare
    /// the same magic numbers; Core.VK itself still speaks raw <c>uint</c>s (see
    /// <see cref="VulkanNative"/> for why there is no Vulkan binding on either side).
    /// </summary>
    public static class VkConstants
    {
        public const uint ImageLayoutUndefined = 0;
        public const uint ImageLayoutGeneral = 1;
        public const uint ImageLayoutColorAttachmentOptimal = 2;
        public const uint ImageLayoutShaderReadOnlyOptimal = 5;
        public const uint ImageLayoutTransferSrcOptimal = 6;
        public const uint ImageLayoutTransferDstOptimal = 7;

        public const uint PipelineStageTopOfPipe = 0x1;
        public const uint PipelineStageFragmentShader = 0x80;
        public const uint PipelineStageColorAttachmentOutput = 0x400;
        public const uint PipelineStageTransfer = 0x1000;
        public const uint PipelineStageBottomOfPipe = 0x2000;

        public const uint AccessShaderRead = 0x20;
        public const uint AccessColorAttachmentWrite = 0x100;
        public const uint AccessTransferRead = 0x800;
        public const uint AccessTransferWrite = 0x1000;

        public const uint ImageAspectColor = 0x1;

        public const uint ImageUsageTransferSrc = 0x1;
        public const uint ImageUsageTransferDst = 0x2;
        public const uint ImageUsageSampled = 0x4;
        public const uint ImageUsageColorAttachment = 0x10;

        public const uint QueueFamilyIgnored = 0xFFFFFFFF;

        /// <summary><c>VK_MAKE_API_VERSION(0, major, minor, 0)</c>.</summary>
        public static uint MakeApiVersion(uint major, uint minor) => (major << 22) | (minor << 12);
    }

    /// <summary>
    /// Records and submits single <c>vkCmdPipelineBarrier</c> image-layout transitions on the host's
    /// queue - the "host needing certainty must insert its own barrier" fallback
    /// <see cref="VkSkiaSurfaceFactory.EndDraw"/>'s doc comment describes, packaged so an adapter
    /// does not have to own Vulkan command-pool plumbing itself.
    ///
    /// Why an adapter needs this at all: Skia leaves a wrapped render target in
    /// <c>VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL</c> after a draw and SkiaSharp 3.119.4 exposes no
    /// way to ask it for a different post-flush layout (no <c>GrBackendSurfaceMutableState</c>
    /// binding - see <see cref="VkSkiaSurfaceFactory"/>'s maintenance notes). A host engine that
    /// tracks image layouts itself (Godot's <c>RenderingDevice</c> render graph does, and expects a
    /// sampled texture to sit in <c>SHADER_READ_ONLY_OPTIMAL</c>) will then record barriers whose
    /// <c>oldLayout</c> no longer matches reality. Transitioning the image back to the layout the
    /// host believes it is in, right after Skia's flush, keeps both sides' bookkeeping truthful - at
    /// the cost of one small extra queue submission per draw.
    ///
    /// Submissions are asynchronous: <see cref="Transition"/> returns as soon as the barrier is
    /// queued. A ring of <c>slots</c> command buffers (each with its own pool and fence) lets that
    /// many transitions be in flight before a call has to wait for the oldest one; with the default
    /// of eight, an adapter drawing a handful of targets per frame never stalls the CPU on the GPU in
    /// steady state. <see cref="WaitForCompletion"/> drains everything, for teardown.
    ///
    /// Every Vulkan entry point is resolved through <c>vkGetDeviceProcAddr</c> (see
    /// <see cref="VulkanNative"/>), never P/Invoked by name, and the command pools are created on the
    /// caller's queue family so the recorded barrier is valid to submit on that queue. Not thread-safe;
    /// one instance per device/queue, driven from whatever thread the host lets submit.
    /// </summary>
    public sealed unsafe class VkImageLayoutTransitioner : IDisposable
    {
        const uint VK_STRUCTURE_TYPE_SUBMIT_INFO = 4;
        const uint VK_STRUCTURE_TYPE_FENCE_CREATE_INFO = 8;
        const uint VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39;
        const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 40;
        const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 42;
        const uint VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER = 45;
        const uint VK_COMMAND_BUFFER_LEVEL_PRIMARY = 0;
        const uint VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT = 0x1;
        const uint VK_COMMAND_POOL_CREATE_TRANSIENT_BIT = 0x1;
        const int VK_SUCCESS = 0;

        readonly IntPtr _device;
        readonly IntPtr _queue;
        readonly Func<IDisposable>? _acquireQueueLock;

        readonly delegate* unmanaged<IntPtr, ulong, IntPtr, void> _vkDestroyCommandPool;
        readonly delegate* unmanaged<IntPtr, ulong, uint, int> _vkResetCommandPool;
        readonly delegate* unmanaged<IntPtr, VkCommandBufferBeginInfo*, int> _vkBeginCommandBuffer;
        readonly delegate* unmanaged<IntPtr, int> _vkEndCommandBuffer;
        readonly delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint, VkImageMemoryBarrier*, void> _vkCmdPipelineBarrier;
        readonly delegate* unmanaged<IntPtr, uint, VkSubmitInfo*, ulong, int> _vkQueueSubmit;
        readonly delegate* unmanaged<IntPtr, ulong, IntPtr, void> _vkDestroyFence;
        readonly delegate* unmanaged<IntPtr, uint, ulong*, int> _vkResetFences;
        readonly delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int> _vkWaitForFences;

        readonly Slot[] _slots;
        int _nextSlot;
        bool _disposed;

        struct Slot
        {
            public ulong CommandPool;
            public IntPtr CommandBuffer;
            public ulong Fence;
            public bool Pending;
        }

        /// <param name="device">The host's <c>VkDevice</c>.</param>
        /// <param name="queue">
        /// The host's <c>VkQueue</c> to submit the barrier on - the same queue Skia and the host
        /// render with, so the transition is ordered against both.
        /// </param>
        /// <param name="queueFamilyIndex">The queue family <paramref name="queue"/> belongs to.</param>
        /// <param name="acquireQueueLock">
        /// The same optional queue-lock hook <see cref="VkSkiaSurfaceFactory.InitializeFromNative"/>
        /// takes; bracketed around this class's <c>vkQueueSubmit</c> calls.
        /// </param>
        /// <param name="slots">
        /// How many transitions may be in flight before <see cref="Transition"/> waits for the oldest.
        /// </param>
        public VkImageLayoutTransitioner(IntPtr device, IntPtr queue, uint queueFamilyIndex, Func<IDisposable>? acquireQueueLock = null, int slots = 8)
        {
            if (device == IntPtr.Zero)
                throw new ArgumentException("Vulkan device native pointer is null.", nameof(device));
            if (queue == IntPtr.Zero)
                throw new ArgumentException("Vulkan queue native pointer is null.", nameof(queue));
            if (slots < 1)
                throw new ArgumentOutOfRangeException(nameof(slots));

            _device = device;
            _queue = queue;
            _acquireQueueLock = acquireQueueLock;

            var vkCreateCommandPool = (delegate* unmanaged<IntPtr, VkCommandPoolCreateInfo*, IntPtr, ulong*, int>)VulkanNative.RequireDeviceProc(device, "vkCreateCommandPool");
            var vkAllocateCommandBuffers = (delegate* unmanaged<IntPtr, VkCommandBufferAllocateInfo*, IntPtr*, int>)VulkanNative.RequireDeviceProc(device, "vkAllocateCommandBuffers");
            var vkCreateFence = (delegate* unmanaged<IntPtr, VkFenceCreateInfo*, IntPtr, ulong*, int>)VulkanNative.RequireDeviceProc(device, "vkCreateFence");
            _vkDestroyCommandPool = (delegate* unmanaged<IntPtr, ulong, IntPtr, void>)VulkanNative.RequireDeviceProc(device, "vkDestroyCommandPool");
            _vkResetCommandPool = (delegate* unmanaged<IntPtr, ulong, uint, int>)VulkanNative.RequireDeviceProc(device, "vkResetCommandPool");
            _vkBeginCommandBuffer = (delegate* unmanaged<IntPtr, VkCommandBufferBeginInfo*, int>)VulkanNative.RequireDeviceProc(device, "vkBeginCommandBuffer");
            _vkEndCommandBuffer = (delegate* unmanaged<IntPtr, int>)VulkanNative.RequireDeviceProc(device, "vkEndCommandBuffer");
            _vkCmdPipelineBarrier = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint, VkImageMemoryBarrier*, void>)VulkanNative.RequireDeviceProc(device, "vkCmdPipelineBarrier");
            _vkQueueSubmit = (delegate* unmanaged<IntPtr, uint, VkSubmitInfo*, ulong, int>)VulkanNative.RequireDeviceProc(device, "vkQueueSubmit");
            _vkDestroyFence = (delegate* unmanaged<IntPtr, ulong, IntPtr, void>)VulkanNative.RequireDeviceProc(device, "vkDestroyFence");
            _vkResetFences = (delegate* unmanaged<IntPtr, uint, ulong*, int>)VulkanNative.RequireDeviceProc(device, "vkResetFences");
            _vkWaitForFences = (delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int>)VulkanNative.RequireDeviceProc(device, "vkWaitForFences");

            _slots = new Slot[slots];
            try
            {
                for (int i = 0; i < slots; i++)
                {
                    var poolInfo = new VkCommandPoolCreateInfo
                    {
                        sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                        flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT,
                        queueFamilyIndex = queueFamilyIndex,
                    };
                    ulong pool;
                    Check(vkCreateCommandPool(device, &poolInfo, IntPtr.Zero, &pool), "vkCreateCommandPool");
                    _slots[i].CommandPool = pool;

                    var allocateInfo = new VkCommandBufferAllocateInfo
                    {
                        sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                        commandPool = pool,
                        level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                        commandBufferCount = 1,
                    };
                    IntPtr commandBuffer;
                    Check(vkAllocateCommandBuffers(device, &allocateInfo, &commandBuffer), "vkAllocateCommandBuffers");
                    _slots[i].CommandBuffer = commandBuffer;

                    var fenceInfo = new VkFenceCreateInfo { sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
                    ulong fence;
                    Check(vkCreateFence(device, &fenceInfo, IntPtr.Zero, &fence), "vkCreateFence");
                    _slots[i].Fence = fence;
                }
            }
            catch
            {
                DestroySlots();
                throw;
            }
        }

        /// <summary>
        /// Queues one <c>vkCmdPipelineBarrier</c> moving <paramref name="image"/> from
        /// <paramref name="oldLayout"/> to <paramref name="newLayout"/> on the host's queue and
        /// returns without waiting for it, unless every slot is still in flight - then it waits for
        /// the oldest first. Call <see cref="WaitForCompletion"/> if the caller needs the GPU to have
        /// finished before continuing.
        /// </summary>
        public void Transition(
            ulong image, uint oldLayout, uint newLayout,
            uint srcStageMask, uint srcAccessMask, uint dstStageMask, uint dstAccessMask,
            uint aspectMask = VkConstants.ImageAspectColor, uint levelCount = 1, uint layerCount = 1)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (image == 0)
                throw new ArgumentException("VkImage handle is null (0).", nameof(image));

            ref var slot = ref _slots[_nextSlot];
            _nextSlot = (_nextSlot + 1) % _slots.Length;

            WaitForSlot(ref slot);

            Check(_vkResetCommandPool(_device, slot.CommandPool, 0), "vkResetCommandPool");

            var beginInfo = new VkCommandBufferBeginInfo
            {
                sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
            };
            Check(_vkBeginCommandBuffer(slot.CommandBuffer, &beginInfo), "vkBeginCommandBuffer");

            var barrier = new VkImageMemoryBarrier
            {
                sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                srcAccessMask = srcAccessMask,
                dstAccessMask = dstAccessMask,
                oldLayout = oldLayout,
                newLayout = newLayout,
                srcQueueFamilyIndex = VkConstants.QueueFamilyIgnored,
                dstQueueFamilyIndex = VkConstants.QueueFamilyIgnored,
                image = image,
                subresourceRange = new VkImageSubresourceRange
                {
                    aspectMask = aspectMask,
                    baseMipLevel = 0,
                    levelCount = levelCount,
                    baseArrayLayer = 0,
                    layerCount = layerCount,
                },
            };
            _vkCmdPipelineBarrier(slot.CommandBuffer, srcStageMask, dstStageMask, 0, 0, null, 0, null, 1, &barrier);

            Check(_vkEndCommandBuffer(slot.CommandBuffer), "vkEndCommandBuffer");

            var commandBuffer = slot.CommandBuffer;
            var submitInfo = new VkSubmitInfo
            {
                sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
                commandBufferCount = 1,
                pCommandBuffers = &commandBuffer,
            };

            using (_acquireQueueLock?.Invoke())
            {
                Check(_vkQueueSubmit(_queue, 1, &submitInfo, slot.Fence), "vkQueueSubmit");
            }
            slot.Pending = true;
        }

        /// <summary>Blocks until every queued <see cref="Transition"/> has executed on the GPU (no-op if none is pending).</summary>
        public void WaitForCompletion()
        {
            for (int i = 0; i < _slots.Length; i++)
                WaitForSlot(ref _slots[i]);
        }

        void WaitForSlot(ref Slot slot)
        {
            if (!slot.Pending)
                return;

            var fence = slot.Fence;
            Check(_vkWaitForFences(_device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
            Check(_vkResetFences(_device, 1, &fence), "vkResetFences");
            slot.Pending = false;
        }

        static void Check(int result, string call)
        {
            if (result != VK_SUCCESS)
                throw new InvalidOperationException($"{call} failed. VkResult: {result}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                WaitForCompletion();
            }
            finally
            {
                DestroySlots();
            }
        }

        void DestroySlots()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var slot = ref _slots[i];
                if (slot.Fence != 0)
                    _vkDestroyFence(_device, slot.Fence, IntPtr.Zero);
                // Destroying the pool frees the command buffer allocated from it.
                if (slot.CommandPool != 0)
                    _vkDestroyCommandPool(_device, slot.CommandPool, IntPtr.Zero);
                slot = default;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkCommandPoolCreateInfo
        {
            public uint sType;
            public IntPtr pNext;
            public uint flags;
            public uint queueFamilyIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkCommandBufferAllocateInfo
        {
            public uint sType;
            public IntPtr pNext;
            public ulong commandPool;
            public uint level;
            public uint commandBufferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkCommandBufferBeginInfo
        {
            public uint sType;
            public IntPtr pNext;
            public uint flags;
            public IntPtr pInheritanceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkImageSubresourceRange
        {
            public uint aspectMask;
            public uint baseMipLevel;
            public uint levelCount;
            public uint baseArrayLayer;
            public uint layerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkImageMemoryBarrier
        {
            public uint sType;
            public IntPtr pNext;
            public uint srcAccessMask;
            public uint dstAccessMask;
            public uint oldLayout;
            public uint newLayout;
            public uint srcQueueFamilyIndex;
            public uint dstQueueFamilyIndex;
            public ulong image;
            public VkImageSubresourceRange subresourceRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkSubmitInfo
        {
            public uint sType;
            public IntPtr pNext;
            public uint waitSemaphoreCount;
            public IntPtr pWaitSemaphores;
            public IntPtr pWaitDstStageMask;
            public uint commandBufferCount;
            public IntPtr* pCommandBuffers;
            public uint signalSemaphoreCount;
            public IntPtr pSignalSemaphores;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkFenceCreateInfo
        {
            public uint sType;
            public IntPtr pNext;
            public uint flags;
        }
    }
}
