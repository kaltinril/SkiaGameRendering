using Godot;
using SkiaGameRendering.Core.VK;
using SkiaSharp;

namespace SkiaGameRendering.Godot.VK
{
    /// <summary>
    /// Godot-specific adapter over <see cref="VkSkiaSurfaceFactory"/> (see that class for how the
    /// Vulkan interop itself works). This class's only job is pulling Godot's Vulkan
    /// instance/physical-device/device/queue handles out of its <see cref="RenderingDevice"/>, handing
    /// them to the shared factory, and wrapping the <c>VkImage</c> behind an RD texture for Skia.
    ///
    /// Unlike every other engine adapter in this repo, NOTHING here is reached by reflection:
    /// <see cref="RenderingDevice.GetDriverResource"/> is public API that returns the raw native
    /// handle for each <see cref="RenderingDevice.DriverResource"/> (Godot 4.3+; the un-prefixed enum
    /// names replaced the deprecated <c>Vulkan*</c> ones there). A Godot version bump therefore breaks
    /// this adapter at compile time, not silently at runtime - there is no reflection pin test
    /// because the GodotSharp reference IS the pin.
    ///
    /// MAINTENANCE NOTES (everything below was read from Godot 4.7.2's source, not assumed):
    /// <list type="bullet">
    /// <item>
    /// <b>Render thread only.</b> <c>RenderingDevice::get_driver_resource</c> and every RD command
    /// method open with <c>ERR_RENDER_THREAD_GUARD</c>. Under the default
    /// <c>rendering/driver/threads/thread_model</c> ("Safe") the render thread IS the main thread,
    /// so <c>_Process</c>/<c>_Ready</c> qualify; under "Separate" the caller must go through
    /// <see cref="RenderingServer.CallOnRenderThread"/>. Every public entry point
    /// (<see cref="SkiaGodotRenderer.Initialize"/>, the <see cref="SkiaGodotRenderTarget2D"/>
    /// constructor, <see cref="SkiaGodotRenderTarget2D.Begin"/>) checks
    /// <see cref="RenderingServer.IsOnRenderThread"/> and throws with that instruction.
    /// </item>
    /// <item>
    /// <b>Queue synchronization is by construction, not by lock.</b> Godot's own <c>vkQueueSubmit</c>
    /// sits behind a per-queue <c>submit_mutex</c> inside the Vulkan driver that C# cannot reach, but
    /// Godot only submits from the render thread (once per frame, in <c>RS::draw</c>, after
    /// <c>_Process</c>), and the render-thread requirement above puts Skia's submits on that same
    /// thread. So <see cref="VkSkiaSurfaceFactory.InitializeFromNative"/> gets no
    /// <c>acquireQueueLock</c>: there is no second thread to lock against. (Stride's adapter needs
    /// one because Stride submits from multiple threads.)
    /// </item>
    /// <item>
    /// <b>Godot tracks each texture's VkImageLayout itself, so both sides' bookkeeping must be kept
    /// truthful every frame.</b> Godot's <c>RenderingDeviceGraph</c> derives the layout purely from
    /// the last <c>ResourceUsage</c> it recorded for a texture and only emits a barrier when that
    /// usage changes: a texture Godot merely samples gets ONE barrier into
    /// <c>SHADER_READ_ONLY_OPTIMAL</c> and then never another. Skia, meanwhile, leaves a wrapped
    /// render target in <c>COLOR_ATTACHMENT_OPTIMAL</c> and SkiaSharp 3.119.4 cannot be asked for
    /// anything else (see <see cref="VkSkiaSurfaceFactory.EndDraw"/>). Three pieces keep the two in
    /// agreement, all verified clean under Godot's <c>--gpu-validation</c> (Khronos validation layer):
    /// <list type="number">
    /// <item><see cref="PrimeForSampling"/> runs, at texture creation and before Skia's first draw, a
    /// one-dispatch compute pass that samples the texture and then flushes and stalls the render
    /// graph. That makes Godot's ONLY layout transition for the texture happen while it still holds
    /// nothing - the first barrier's <c>oldLayout</c> is <c>UNDEFINED</c>, which the spec allows to
    /// discard contents, and on tiled/mobile drivers it does. Without priming, a "draw once in
    /// <c>_Ready</c>" texture would come up blank there.</item>
    /// <item><see cref="TransitionToShaderRead"/> submits an explicit
    /// <c>COLOR_ATTACHMENT_OPTIMAL -> SHADER_READ_ONLY_OPTIMAL</c> barrier after every Skia flush
    /// (Core.VK's <see cref="VkImageLayoutTransitioner"/>), and <c>SkiaGodotTarget</c> re-wraps the
    /// surface each frame with that layout as Skia's starting point, so Skia's own first barrier of
    /// the frame is correct too.</item>
    /// <item><c>SkiaGodotTarget.BeginFrame</c> records a no-op 1x1 draw before the caller's, so a
    /// frame with no other Skia work (a <c>Begin(clear: false)</c>/<c>End()</c> early-out) still
    /// executes a render pass and really does leave the image in <c>COLOR_ATTACHMENT_OPTIMAL</c> -
    /// otherwise the hand-back barrier's <c>oldLayout</c> would be a lie.</item>
    /// </list>
    /// </item>
    /// <item>
    /// <b>The transfer-bit landmine Core.VK warns about is satisfied through RD usage bits.</b>
    /// Skia requires <c>VK_IMAGE_USAGE_TRANSFER_SRC_BIT | TRANSFER_DST_BIT</c> on any wrapped image;
    /// Godot's Vulkan driver maps <see cref="RenderingDevice.TextureUsageBits.CanCopyFromBit"/> to
    /// <c>TRANSFER_SRC</c> and <see cref="RenderingDevice.TextureUsageBits.CanCopyToBit"/> to
    /// <c>TRANSFER_DST</c> (<c>rendering_device_driver_vulkan.cpp</c>, <c>texture_create</c>), so
    /// <see cref="CreateTexture"/> requests both alongside <c>SamplingBit</c> (what Godot needs to
    /// display it) and <c>ColorAttachmentBit</c> (what Skia needs to draw into it).
    /// <see cref="ImageUsageFlags"/> mirrors that mapping back to Skia, since RD does not expose the
    /// <c>VkImageUsageFlags</c> it actually used.
    /// </item>
    /// <item>
    /// <b>API version.</b> Godot creates its instance against <c>VK_API_VERSION_1_2</c> whenever the
    /// loader is newer than 1.0 (<c>rendering_context_driver_vulkan.cpp</c>,
    /// <c>application_api_version</c>) and does not expose that number, so this clamps
    /// <see cref="VkSkiaSurfaceFactory.QueryApiVersion"/>'s loader/device answer to 1.2 rather than
    /// let Skia assume 1.3 core entry points Godot never declared.
    /// </item>
    /// <item>
    /// <b>Device features.</b> <c>VkPhysicalDeviceFeatures2</c> is left null so Skia queries what the
    /// physical device supports. Godot enables every core <c>VkPhysicalDeviceFeatures</c> member it
    /// finds supported that Skia cares about (<c>dualSrcBlend</c>, <c>sampleRateShading</c>,
    /// <c>geometryShader</c>, <c>independentBlend</c>, ... - the <c>VK_DEVICEFEATURE_ENABLE_IF</c>
    /// block in <c>rendering_device_driver_vulkan.cpp</c>), so "supported" and "enabled" coincide
    /// for those, the same argument the Stride adapter makes. Extension lists are passed empty, as
    /// there too: Skia's 2D draw path never presents, so it needs none of Godot's swapchain
    /// extensions.
    /// </item>
    /// <item>
    /// <b>D3D12 and Metal are NOT this package.</b> Godot 4.6+ defaults NEW Windows projects to the
    /// <c>d3d12</c> driver and macOS to <c>metal</c>; this adapter checks
    /// <see cref="RenderingServer.GetCurrentRenderingDriverName"/> is <c>"vulkan"</c> and tells the
    /// user which project setting to flip otherwise. The same <c>GetDriverResource</c> calls return
    /// <c>ID3D12Device</c>/<c>IDXGIAdapter</c>/<c>ID3D12CommandQueue</c>/<c>ID3D12Resource</c> under
    /// D3D12, so a sibling <c>SkiaGameRendering.Godot.D3D12</c> on <c>Core.D3D12</c> is the obvious
    /// follow-up (tracked in TODO.md).
    /// </item>
    /// </list>
    /// </summary>
    internal sealed class SkiaGodotVulkanContext : IDisposable
    {
        const uint ImageUsageFlags =
            VkConstants.ImageUsageTransferSrc | VkConstants.ImageUsageTransferDst |
            VkConstants.ImageUsageSampled | VkConstants.ImageUsageColorAttachment;

        /// <summary>
        /// The compute shader <see cref="PrimeForSampling"/> dispatches once per texture. It samples
        /// the texture and writes the result to a storage buffer purely so the compiler cannot
        /// optimize the sampler binding away (Godot's uniform-set creation would then reject a
        /// binding the shader no longer declares). Compiled at runtime through Godot's own glslang,
        /// the same path the engine's compute-shader tutorial uses in exported projects.
        /// </summary>
        const string PrimeShaderSource = """
            #version 450
            layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
            layout(set = 0, binding = 0) uniform sampler2D skia_texture;
            layout(set = 0, binding = 1, std430) restrict buffer Sink { vec4 value; } sink;
            void main() { sink.value = textureLod(skia_texture, vec2(0.5), 0.0); }
            """;

        readonly VkSkiaSurfaceFactory _factory = new();
        VkImageLayoutTransitioner? _transitioner;
        RenderingDevice _renderingDevice = null!;

        Rid _primeShader;
        Rid _primePipeline;
        Rid _primeSampler;
        Rid _primeBuffer;
        Rid _primeFlushTexture;

        internal RenderingDevice RenderingDevice => _renderingDevice;

        internal GRContext GRContext => _factory.GRContext;

        internal void Initialize(RenderingDevice renderingDevice)
        {
            var driverName = RenderingServer.GetCurrentRenderingDriverName();
            if (driverName != "vulkan")
                throw new InvalidOperationException(
                    $"SkiaGameRendering.Godot.VK needs Godot's Vulkan rendering driver, but the current driver is '{driverName}'. " +
                    "Set the project setting rendering/rendering_device/driver (and its .windows/.macos overrides) to \"vulkan\" - " +
                    "Godot 4.6+ defaults new Windows projects to d3d12 and macOS to metal - or run with --rendering-driver vulkan.");

            SkiaGodotRenderer.RequireRenderThread("SkiaGodotRenderer.Initialize");

            _renderingDevice = renderingDevice;

            var instance = (IntPtr)renderingDevice.GetDriverResource(RenderingDevice.DriverResource.TopmostObject, default, 0);
            var physicalDevice = (IntPtr)renderingDevice.GetDriverResource(RenderingDevice.DriverResource.PhysicalDevice, default, 0);
            var device = (IntPtr)renderingDevice.GetDriverResource(RenderingDevice.DriverResource.LogicalDevice, default, 0);
            var queue = (IntPtr)renderingDevice.GetDriverResource(RenderingDevice.DriverResource.CommandQueue, default, 0);
            var queueFamilyIndex = (uint)renderingDevice.GetDriverResource(RenderingDevice.DriverResource.QueueFamily, default, 0);

            if (instance == IntPtr.Zero)
                throw new InvalidOperationException("Godot RenderingDevice returned a null VkInstance (DriverResource.TopmostObject).");
            if (physicalDevice == IntPtr.Zero)
                throw new InvalidOperationException("Godot RenderingDevice returned a null VkPhysicalDevice (DriverResource.PhysicalDevice).");
            if (device == IntPtr.Zero)
                throw new InvalidOperationException("Godot RenderingDevice returned a null VkDevice (DriverResource.LogicalDevice).");
            if (queue == IntPtr.Zero)
                throw new InvalidOperationException("Godot RenderingDevice returned a null VkQueue (DriverResource.CommandQueue).");

            var apiVersion = Math.Min(
                VkSkiaSurfaceFactory.QueryApiVersion(instance, physicalDevice),
                VkConstants.MakeApiVersion(1, 2));

            _factory.InitializeFromNative(
                instance, physicalDevice, device, queue,
                graphicsQueueFamilyIndex: queueFamilyIndex,
                apiVersion: apiVersion,
                instanceExtensions: [],
                deviceExtensions: [],
                acquireQueueLock: null);

            _transitioner = new VkImageLayoutTransitioner(device, queue, queueFamilyIndex);
        }

        /// <summary>
        /// Creates the RD texture Skia will draw into and Godot will sample. See this class's doc
        /// comment for why exactly these four usage bits.
        /// <para>
        /// <paramref name="srgbFormat"/> matters even though nothing here ever samples the texture as
        /// sRGB: <c>RenderingServer.texture_rd_create</c> (what <see cref="Texture2Drd"/> calls) builds
        /// a second, sRGB-format shared view of any RD texture whose format has an sRGB twin, and a
        /// <c>vkCreateImageView</c> with a different format is only legal on an image created with
        /// <c>VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT</c> - which Godot's Vulkan driver sets exactly when
        /// <see cref="RDTextureFormat"/> lists shareable formats. Found by running the sample under
        /// <c>--gpu-validation</c> (<c>VUID-VkImageViewCreateInfo-image-01762</c>), not by reading
        /// docs; without it the view is created anyway and happens to work on NVIDIA.
        /// </para>
        /// </summary>
        internal Rid CreateTexture(int width, int height, RenderingDevice.DataFormat format, RenderingDevice.DataFormat? srgbFormat)
        {
            var textureFormat = new RDTextureFormat
            {
                Format = format,
                Width = (uint)width,
                Height = (uint)height,
                Depth = 1,
                ArrayLayers = 1,
                Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2D,
                Samples = RenderingDevice.TextureSamples.Samples1,
                UsageBits =
                    RenderingDevice.TextureUsageBits.SamplingBit |
                    RenderingDevice.TextureUsageBits.ColorAttachmentBit |
                    RenderingDevice.TextureUsageBits.CanCopyFromBit |
                    RenderingDevice.TextureUsageBits.CanCopyToBit,
            };
            textureFormat.AddShareableFormat(format);
            if (srgbFormat is { } srgb)
                textureFormat.AddShareableFormat(srgb);

            var rid = _renderingDevice.TextureCreate(textureFormat, new RDTextureView());
            if (!rid.IsValid)
                throw new InvalidOperationException($"RenderingDevice.TextureCreate failed for a {width}x{height} {format} texture.");
            return rid;
        }

        /// <summary>The texture's <c>VkImage</c> and <c>VkFormat</c>, straight from Godot's Vulkan driver.</summary>
        internal (ulong image, uint format) GetImageAndFormat(Rid texture)
        {
            var image = _renderingDevice.GetDriverResource(RenderingDevice.DriverResource.Texture, texture, 0);
            var format = (uint)_renderingDevice.GetDriverResource(RenderingDevice.DriverResource.TextureDataFormat, texture, 0);
            if (image == 0)
                throw new InvalidOperationException("Godot RenderingDevice returned a null VkImage for the Skia texture (DriverResource.Texture).");
            return (image, format);
        }

        /// <summary>
        /// Makes Godot's render graph record <paramref name="texture"/> as a sampled texture, and
        /// execute that transition, before Skia touches it - see item 1 of the layout discussion in
        /// this class's doc comment. On return the image really is in
        /// <c>SHADER_READ_ONLY_OPTIMAL</c>, Godot believes exactly that, and it will never move it
        /// again on its own; the caller wraps it for Skia with that layout.
        /// <para>
        /// The flush is <see cref="RenderingDevice.TextureGetData"/> on a private 1x1 scratch texture:
        /// Godot implements it as "record the copy, flush and stall for all frames, read back", which
        /// executes everything recorded before it - the compute dispatch included - and waits. Done
        /// on the scratch texture rather than <paramref name="texture"/> itself so the latter's
        /// recorded usage stays TEXTURE_SAMPLE instead of ending on COPY_FROM.
        /// </para>
        /// </summary>
        internal void PrimeForSampling(Rid texture)
        {
            EnsurePrimeResources();

            var textureUniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
            textureUniform.AddId(_primeSampler);
            textureUniform.AddId(texture);
            var sinkUniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
            sinkUniform.AddId(_primeBuffer);

            var uniformSet = _renderingDevice.UniformSetCreate([textureUniform, sinkUniform], _primeShader, 0);
            if (!uniformSet.IsValid)
                throw new InvalidOperationException("RenderingDevice.UniformSetCreate failed while priming the Skia texture for sampling.");
            try
            {
                var computeList = _renderingDevice.ComputeListBegin();
                _renderingDevice.ComputeListBindComputePipeline(computeList, _primePipeline);
                _renderingDevice.ComputeListBindUniformSet(computeList, uniformSet, 0);
                _renderingDevice.ComputeListDispatch(computeList, 1, 1, 1);
                _renderingDevice.ComputeListEnd();

                _renderingDevice.TextureGetData(_primeFlushTexture, 0);
            }
            finally
            {
                _renderingDevice.FreeRid(uniformSet);
            }
        }

        void EnsurePrimeResources()
        {
            if (_primePipeline.IsValid)
                return;

            var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = PrimeShaderSource };
            var spirv = _renderingDevice.ShaderCompileSpirVFromSource(source, allowCache: false);
            if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
                throw new InvalidOperationException("Failed to compile the texture-priming compute shader: " + spirv.CompileErrorCompute);

            _primeShader = _renderingDevice.ShaderCreateFromSpirV(spirv, "SkiaGameRendering.Godot.VK prime");
            if (!_primeShader.IsValid)
                throw new InvalidOperationException("RenderingDevice.ShaderCreateFromSpirV failed for the texture-priming compute shader.");

            _primePipeline = _renderingDevice.ComputePipelineCreate(_primeShader);
            if (!_primePipeline.IsValid)
                throw new InvalidOperationException("RenderingDevice.ComputePipelineCreate failed for the texture-priming compute shader.");

            _primeSampler = _renderingDevice.SamplerCreate(new RDSamplerState());
            _primeBuffer = _renderingDevice.StorageBufferCreate(16);

            var scratch = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = 1,
                Height = 1,
                Depth = 1,
                ArrayLayers = 1,
                Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2D,
                Samples = RenderingDevice.TextureSamples.Samples1,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };
            _primeFlushTexture = _renderingDevice.TextureCreate(scratch, new RDTextureView());

            if (!_primeSampler.IsValid || !_primeBuffer.IsValid || !_primeFlushTexture.IsValid)
                throw new InvalidOperationException("RenderingDevice failed to create the texture-priming helper resources.");
        }

        /// <param name="currentLayout">
        /// The <c>VkImageLayout</c> the image is in right now - always
        /// <see cref="VkConstants.ImageLayoutShaderReadOnlyOptimal"/> for a texture this class manages,
        /// both after <see cref="PrimeForSampling"/> and after <see cref="TransitionToShaderRead"/>.
        /// </param>
        internal VkTextureState CreateTextureState(ulong image, uint format, uint currentLayout) =>
            _factory.CreateTextureState(
                image, format, currentLayout, ImageUsageFlags,
                imageTiling: 0 /* VK_IMAGE_TILING_OPTIMAL - Godot's texture_create always uses optimal tiling. */);

        internal (SKSurface surface, GRBackendRenderTarget renderTarget) CreateSurface(
            VkTextureState state, int width, int height, SKColorType colorType) =>
            _factory.CreateSurface(state, width, height, colorType);

        internal void BeginDraw() => _factory.BeginDraw();

        /// <summary>
        /// Submits Skia's work WITHOUT waiting for the GPU (see <see cref="VkSkiaSurfaceFactory.EndDraw"/>):
        /// Godot's own use of the image is its frame submit on the same queue, later in the frame, so
        /// queue order alone guarantees it sees the finished draw. Measured on the stress scenario,
        /// the synchronous variant cost most of a 60Hz frame in CPU stalls for three targets.
        /// </summary>
        internal void EndDraw() => _factory.EndDraw(synchronous: false);

        /// <summary>
        /// Blocks until the most recent hand-back barrier (and therefore, by queue order, every Skia
        /// submission before it) has executed. Used before releasing a texture, so nothing in flight
        /// still references the <c>VkImage</c> when Godot frees it.
        /// </summary>
        internal void WaitForPendingGpuWork() => _transitioner?.WaitForCompletion();

        /// <summary>
        /// Moves <paramref name="image"/> from the <c>COLOR_ATTACHMENT_OPTIMAL</c> Skia leaves it in
        /// back to the <c>SHADER_READ_ONLY_OPTIMAL</c> Godot's render graph expects a sampled texture
        /// to be in. Called right after <see cref="EndDraw"/>'s synchronous flush, so the Skia work
        /// it orders after has already completed; it does not itself wait, since Godot's own sampling
        /// of the image is queue-ordered behind it on the same <c>VkQueue</c>.
        /// </summary>
        internal void TransitionToShaderRead(ulong image)
        {
            _transitioner!.Transition(
                image,
                oldLayout: VkConstants.ImageLayoutColorAttachmentOptimal,
                newLayout: VkConstants.ImageLayoutShaderReadOnlyOptimal,
                srcStageMask: VkConstants.PipelineStageColorAttachmentOutput,
                srcAccessMask: VkConstants.AccessColorAttachmentWrite,
                dstStageMask: VkConstants.PipelineStageFragmentShader,
                dstAccessMask: VkConstants.AccessShaderRead);
        }

        public void Dispose()
        {
            _transitioner?.Dispose();
            _transitioner = null;
            _factory.Dispose();

            if (_renderingDevice != null)
            {
                FreeIfValid(ref _primePipeline);
                FreeIfValid(ref _primeShader);
                FreeIfValid(ref _primeSampler);
                FreeIfValid(ref _primeBuffer);
                FreeIfValid(ref _primeFlushTexture);
            }
        }

        void FreeIfValid(ref Rid rid)
        {
            if (rid.IsValid)
                _renderingDevice.FreeRid(rid);
            rid = default;
        }
    }
}
