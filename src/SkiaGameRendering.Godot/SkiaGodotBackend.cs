using Godot;
using Godot.Collections;
using SkiaSharp;

namespace SkiaGameRendering.Godot
{
    /// <summary>
    /// The per-driver half of the Godot adapter. Everything Godot exposes the same way on every
    /// <see cref="RenderingDevice"/> driver lives here (creating the RD texture, priming it, the
    /// render-thread rules); what differs per graphics API - how Skia gets at the device and the
    /// texture, and how the texture is handed back to Godot after a draw - is left to
    /// <see cref="VulkanGodotBackend"/> and <see cref="D3D12GodotBackend"/>. <see cref="SkiaGodotRenderer"/>
    /// picks one from <see cref="RenderingServer.GetCurrentRenderingDriverName"/>.
    ///
    /// Unlike every other engine adapter in this repo, NOTHING here is reached by reflection:
    /// <see cref="RenderingDevice.GetDriverResource"/> is public API that returns the raw native
    /// handle for each <see cref="RenderingDevice.DriverResource"/> (Godot 4.3+). A Godot version bump
    /// therefore breaks this adapter at compile time, not silently at runtime - there is no reflection
    /// pin test because the GodotSharp reference IS the pin.
    ///
    /// MAINTENANCE NOTES shared by both backends (read from Godot 4.7.2's source, not assumed):
    /// <list type="bullet">
    /// <item>
    /// <b>Render thread only.</b> <c>RenderingDevice::get_driver_resource</c> and every RD command
    /// method open with <c>ERR_RENDER_THREAD_GUARD</c>. Under the default
    /// <c>rendering/driver/threads/thread_model</c> ("Safe") the render thread IS the main thread,
    /// so <c>_Process</c>/<c>_Ready</c> qualify; under "Separate" the caller must go through
    /// <see cref="RenderingServer.CallOnRenderThread"/>. Every public entry point checks
    /// <see cref="RenderingServer.IsOnRenderThread"/> and throws with that instruction.
    /// </item>
    /// <item>
    /// <b>Queue synchronization is by construction, not by lock.</b> Godot only submits from the
    /// render thread (once per frame, in <c>RS::draw</c>, after <c>_Process</c>), and the rule above
    /// puts Skia's submits on that same thread, so neither Core factory gets an
    /// <c>acquireQueueLock</c>. (Stride's adapters need one because Stride submits from several
    /// threads.) Nothing per frame waits on the GPU: Skia submits asynchronously and Godot's own use
    /// of the texture is queue-ordered behind it.
    /// </item>
    /// <item>
    /// <b>Godot tracks each texture's layout/state itself.</b> Its <c>RenderingDeviceGraph</c> derives
    /// a texture's Vulkan layout (or, with D3D12 enhanced barriers, its <c>D3D12_BARRIER_LAYOUT</c>)
    /// from the last usage it recorded and only emits a barrier when that usage changes; the D3D12
    /// driver without enhanced barriers keeps legacy per-subresource states the same way. Either
    /// way a texture Godot merely samples is transitioned once and then assumed to stay put. So
    /// <see cref="PrimeForSampling"/> makes that one transition happen - by really sampling the texture
    /// in a fragment shader, then flushing and stalling the graph - before Skia ever touches it, and
    /// each backend returns the texture to exactly that state after every draw. Sampling from a
    /// fragment shader specifically, rather than compute, is what the D3D12 driver's legacy path
    /// narrows to <c>PIXEL_SHADER_RESOURCE</c>, the state Godot's 2D canvas wants later, so the
    /// tracked state never moves again.
    /// </item>
    /// <item>
    /// <b>Metal and the Compatibility renderer are not this package.</b> Metal has no Core library
    /// in this repo yet; the Compatibility (OpenGL) renderer has no <see cref="RenderingDevice"/> at
    /// all. Both fail in <see cref="SkiaGodotRenderer.Initialize"/> with a message naming the setting.
    /// </item>
    /// </list>
    /// </summary>
    internal abstract class SkiaGodotBackend : IDisposable
    {
        /// <summary>
        /// Full-screen triangle from <c>gl_VertexIndex</c> plus a fragment that samples the texture -
        /// see the priming discussion in this class's doc comment. Writing the sampled value to the
        /// color output keeps the compiler from optimizing the sampler binding away (Godot's
        /// uniform-set creation would then reject a binding the shader no longer declares).
        /// </summary>
        const string PrimeVertexSource = """
            #version 450
            void main() {
                vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
                gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
            }
            """;

        const string PrimeFragmentSource = """
            #version 450
            layout(set = 0, binding = 0) uniform sampler2D skia_texture;
            layout(location = 0) out vec4 color;
            void main() { color = textureLod(skia_texture, vec2(0.5), 0.0); }
            """;

        RenderingDevice _renderingDevice = null!;
        Rid _primeShader;
        Rid _primePipeline;
        Rid _primeSampler;
        Rid _primeScratchTexture;
        Rid _primeFramebuffer;

        internal RenderingDevice RenderingDevice => _renderingDevice;

        /// <summary>The <see cref="RenderingServer.GetCurrentRenderingDriverName"/> value this backend serves.</summary>
        internal abstract string DriverName { get; }

        /// <summary>
        /// Whether Skia draws straight into the RD texture (Vulkan) or into a resource of its own
        /// that is copied into the RD texture per frame (D3D12). Decides the texture's usage bits.
        /// </summary>
        internal abstract bool RendersIntoGodotTexture { get; }

        internal void Initialize(RenderingDevice renderingDevice)
        {
            _renderingDevice = renderingDevice;
            InitializeCore();
        }

        protected abstract void InitializeCore();

        /// <summary>Creates the per-target GPU resources; <paramref name="texture"/> has already been primed.</summary>
        internal abstract SkiaGodotTargetResources CreateResources(Rid texture, int width, int height, SKColorType colorType);

        /// <summary>Blocks until the backend's most recent hand-back submission has executed (and, by queue order, everything before it).</summary>
        internal abstract void WaitForPendingGpuWork();

        protected abstract void DisposeCore();

        /// <summary>
        /// Creates the RD texture Godot will sample. <c>CanCopyFromBit</c>/<c>CanCopyToBit</c> map to the
        /// Vulkan transfer usage Skia insists on for a wrapped image (and let the D3D12 backend and
        /// tests copy into/out of it); <c>ColorAttachmentBit</c> only when Skia renders into the
        /// texture itself.
        /// <para>
        /// <paramref name="srgbFormat"/> matters even though nothing here ever samples the texture as
        /// sRGB: <c>RenderingServer.texture_rd_create</c> (what <see cref="Texture2Drd"/> calls) builds
        /// a second, sRGB-format shared view of any RD texture whose format has an sRGB twin, and on
        /// Vulkan a <c>vkCreateImageView</c> with a different format is only legal on an image created
        /// with <c>VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT</c> - which Godot sets exactly when
        /// <see cref="RDTextureFormat"/> lists shareable formats. Found by running the sample under
        /// <c>--gpu-validation</c> (<c>VUID-VkImageViewCreateInfo-image-01762</c>).
        /// </para>
        /// </summary>
        internal Rid CreateTexture(int width, int height, RenderingDevice.DataFormat format, RenderingDevice.DataFormat? srgbFormat)
        {
            var usage =
                RenderingDevice.TextureUsageBits.SamplingBit |
                RenderingDevice.TextureUsageBits.CanCopyFromBit |
                RenderingDevice.TextureUsageBits.CanCopyToBit;
            if (RendersIntoGodotTexture)
                usage |= RenderingDevice.TextureUsageBits.ColorAttachmentBit;

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
                UsageBits = usage,
            };
            textureFormat.AddShareableFormat(format);
            if (srgbFormat is { } srgb)
                textureFormat.AddShareableFormat(srgb);

            var rid = _renderingDevice.TextureCreate(textureFormat, new RDTextureView());
            if (!rid.IsValid)
                throw new InvalidOperationException($"RenderingDevice.TextureCreate failed for a {width}x{height} {format} texture.");
            return rid;
        }

        /// <summary>
        /// Makes Godot record <paramref name="texture"/> as a fragment-sampled texture, and execute that
        /// transition, before Skia touches it - see this class's doc comment. On return the texture
        /// really is in the sampled layout/state, Godot believes exactly that, and it will never move
        /// it again on its own as long as it is only sampled.
        /// <para>
        /// The flush is <see cref="RenderingDevice.TextureGetData"/> on the priming pass's own 1x1
        /// color attachment: Godot implements it as "record the copy, flush and stall for all
        /// frames, read back", which executes everything recorded before it - the draw included - and
        /// waits. Done on that scratch texture rather than <paramref name="texture"/> itself so the
        /// latter's recorded usage stays "sampled" instead of ending on "copied from".
        /// </para>
        /// </summary>
        internal void PrimeForSampling(Rid texture)
        {
            EnsurePrimeResources();

            var textureUniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
            textureUniform.AddId(_primeSampler);
            textureUniform.AddId(texture);

            var uniformSet = _renderingDevice.UniformSetCreate([textureUniform], _primeShader, 0);
            if (!uniformSet.IsValid)
                throw new InvalidOperationException("RenderingDevice.UniformSetCreate failed while priming the Skia texture for sampling.");
            try
            {
                var drawList = _renderingDevice.DrawListBegin(_primeFramebuffer);
                _renderingDevice.DrawListBindRenderPipeline(drawList, _primePipeline);
                _renderingDevice.DrawListBindUniformSet(drawList, uniformSet, 0);
                _renderingDevice.DrawListDraw(drawList, false, 1, 3);
                _renderingDevice.DrawListEnd();

                _renderingDevice.TextureGetData(_primeScratchTexture, 0);
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

            var source = new RDShaderSource
            {
                Language = RenderingDevice.ShaderLanguage.Glsl,
                SourceVertex = PrimeVertexSource,
                SourceFragment = PrimeFragmentSource,
            };
            var spirv = _renderingDevice.ShaderCompileSpirVFromSource(source, allowCache: false);
            var compileError = spirv.CompileErrorVertex + spirv.CompileErrorFragment;
            if (!string.IsNullOrEmpty(compileError))
                throw new InvalidOperationException("Failed to compile the texture-priming shader: " + compileError);

            _primeShader = _renderingDevice.ShaderCreateFromSpirV(spirv, "SkiaGameRendering.Godot prime");
            if (!_primeShader.IsValid)
                throw new InvalidOperationException("RenderingDevice.ShaderCreateFromSpirV failed for the texture-priming shader.");

            var scratchFormat = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = 1,
                Height = 1,
                Depth = 1,
                ArrayLayers = 1,
                Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2D,
                Samples = RenderingDevice.TextureSamples.Samples1,
                UsageBits = RenderingDevice.TextureUsageBits.ColorAttachmentBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };
            _primeScratchTexture = _renderingDevice.TextureCreate(scratchFormat, new RDTextureView());
            _primeFramebuffer = _renderingDevice.FramebufferCreate([_primeScratchTexture]);
            _primeSampler = _renderingDevice.SamplerCreate(new RDSamplerState());
            if (!_primeScratchTexture.IsValid || !_primeFramebuffer.IsValid || !_primeSampler.IsValid)
                throw new InvalidOperationException("RenderingDevice failed to create the texture-priming helper resources.");

            var blend = new RDPipelineColorBlendState
            {
                Attachments = new Array<RDPipelineColorBlendStateAttachment> { new() },
            };
            _primePipeline = _renderingDevice.RenderPipelineCreate(
                _primeShader,
                _renderingDevice.FramebufferGetFormat(_primeFramebuffer),
                RenderingDevice.InvalidFormatId,
                RenderingDevice.RenderPrimitive.Triangles,
                new RDPipelineRasterizationState(),
                new RDPipelineMultisampleState(),
                new RDPipelineDepthStencilState(),
                blend);
            if (!_primePipeline.IsValid)
                throw new InvalidOperationException("RenderingDevice.RenderPipelineCreate failed for the texture-priming shader.");
        }

        public void Dispose()
        {
            DisposeCore();

            if (_renderingDevice != null)
            {
                FreeIfValid(ref _primePipeline);
                FreeIfValid(ref _primeFramebuffer);
                FreeIfValid(ref _primeScratchTexture);
                FreeIfValid(ref _primeShader);
                FreeIfValid(ref _primeSampler);
            }
        }

        void FreeIfValid(ref Rid rid)
        {
            if (rid.IsValid)
                _renderingDevice.FreeRid(rid);
            rid = default;
        }
    }

    /// <summary>
    /// The per-target, per-backend GPU state behind one <see cref="SkiaGodotRenderTarget2D"/>: whatever
    /// wraps (or stands in for) the RD texture on Skia's side. <see cref="BeginFrame"/> returns the
    /// surface to draw on; <see cref="EndFrame"/> submits the draw and hands the RD texture back to
    /// Godot in the state it expects.
    /// </summary>
    internal abstract class SkiaGodotTargetResources : IDisposable
    {
        internal abstract SKSurface BeginFrame();
        internal abstract void EndFrame();
        public abstract void Dispose();
    }
}
