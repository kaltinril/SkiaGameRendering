using Godot;
using SkiaSharp;

namespace SkiaGameRendering.Godot
{
    /// <summary>
    /// Owns what one <see cref="SkiaGodotRenderTarget2D"/> holds on the Godot side - the
    /// <see cref="RenderingDevice"/> texture and the <see cref="Texture2Drd"/> that exposes it to the
    /// scene tree - plus the backend's per-target resources (<see cref="SkiaGodotTargetResources"/>).
    /// <para>
    /// <b>Alpha.</b> A GPU-backed Skia surface is always premultiplied; Godot's default CanvasItem
    /// blend mode (Mix) expects straight alpha. Opaque content is unaffected; anti-aliased edges over
    /// a transparent clear come out slightly dark unless the displaying node uses
    /// <see cref="CanvasItemMaterial.BlendModeEnum.PremultAlpha"/> -
    /// <see cref="SkiaGodotRenderTarget2D.CreatePremultipliedAlphaMaterial"/> hands one back.
    /// </para>
    /// <para>
    /// <b>Color space.</b> The RD texture is a plain UNORM format and the Skia surface carries no
    /// color-space tag: Godot's 2D pipeline with the default <c>rendering/viewport/hdr_2d = false</c>
    /// samples textures in gamma space, so Skia's sRGB-encoded bytes display 1:1, with none of the
    /// linear-pipeline compensation the Stride adapter needs. HDR 2D projects would need the
    /// same <c>SKColorSpace.CreateSrgbLinear()</c> treatment; not wired up.
    /// </para>
    /// </summary>
    internal sealed class SkiaGodotTarget : IDisposable
    {
        readonly SkiaGodotBackend _backend;
        Rid _textureRid;
        Texture2Drd? _texture;
        SkiaGodotTargetResources? _resources;
        SKSurface? _surface;
        bool _disposed;

        internal SkiaGodotTarget(SkiaGodotBackend backend, int width, int height, SKColorType colorType)
        {
            _backend = backend;

            _textureRid = backend.CreateTexture(width, height, ToDataFormat(colorType), ToSrgbDataFormat(colorType));
            try
            {
                // From here on the texture is in the sampled layout/state, Godot knows it, and Godot
                // will never transition it again on its own - see SkiaGodotBackend.PrimeForSampling.
                backend.PrimeForSampling(_textureRid);

                _resources = backend.CreateResources(_textureRid, width, height, colorType);

                // Texture2Drd is the engine's own "show an RD texture in the scene tree" resource:
                // RenderingServer.texture_rd_create builds a shared VIEW of our RD texture (no copy),
                // so whatever ends up in the RD texture is what Sprite2D/TextureRect display.
                _texture = new Texture2Drd { TextureRdRid = _textureRid };
            }
            catch
            {
                _resources?.Dispose();
                _resources = null;
                backend.RenderingDevice.FreeRid(_textureRid);
                _textureRid = default;
                throw;
            }
        }

        internal Rid TextureRid => _disposed ? throw new ObjectDisposedException(nameof(SkiaGodotTarget)) : _textureRid;

        internal Texture2D Texture => _texture ?? throw new ObjectDisposedException(nameof(SkiaGodotTarget));

        internal SKSurface Surface => _surface ?? throw new InvalidOperationException("No frame is in progress.");

        internal SKSurface BeginFrame()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _surface = _resources!.BeginFrame();
            return _surface;
        }

        internal void EndFrame()
        {
            _surface = null;
            _resources!.EndFrame();
        }

        /// <summary>
        /// The scene-tree half of disposal: detaches the <see cref="Texture2Drd"/> from the RD
        /// texture. Must run on the MAIN thread, because clearing <c>TextureRdRid</c> emits the
        /// resource's <c>changed</c> signal and any CanvasItem showing it calls <c>queue_redraw</c>,
        /// which Godot only allows from the node's own thread. Idempotent; <see cref="Dispose"/>
        /// calls it too for the usual case where main and render thread are the same.
        /// <para>
        /// Order matters: the Texture2Drd (and its RenderingServer view) must let go of the RD
        /// texture before the RD texture itself is freed - freeing the RD RID first leaves the view
        /// dangling, which Godot reports as an error. The Texture2Drd itself is a refcounted Godot
        /// resource, so it is NOT Dispose()d: a Sprite2D the caller left it on still holds it, and
        /// disposing the managed wrapper would make that reference throw ObjectDisposedException
        /// instead of showing an empty texture. Dropping our reference lets Godot free it when the
        /// last user does. (Godot keeps reporting the old <c>TextureRdRid</c> afterward - only the
        /// view is freed and the size zeroed - which is Godot's behavior, not a leak.)
        /// </para>
        /// </summary>
        internal void ReleaseSceneTexture()
        {
            if (_texture == null)
                return;
            _texture.TextureRdRid = default;
            _texture = null;
        }

        /// <summary>
        /// The GPU half of disposal (plus <see cref="ReleaseSceneTexture"/> if it has not run yet).
        /// Must run on the RENDER thread - it frees the RD texture and the backend's resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            ReleaseSceneTexture();

            // Skia submits asynchronously; make sure the last frame's work on this texture has landed
            // before anything backing it is destroyed.
            _backend.WaitForPendingGpuWork();
            _resources?.Dispose();
            _resources = null;

            if (_textureRid.IsValid)
            {
                _backend.RenderingDevice.FreeRid(_textureRid);
                _textureRid = default;
            }
        }

        /// <summary>
        /// SKColorType to <see cref="RenderingDevice.DataFormat"/>. On Vulkan the VkFormat Skia is told
        /// comes back from Godot itself (<see cref="RenderingDevice.DriverResource.TextureDataFormat"/>);
        /// on D3D12 the backend picks the typed twin of the same family.
        /// </summary>
        static RenderingDevice.DataFormat ToDataFormat(SKColorType colorType) => colorType switch
        {
            SKColorType.Bgra8888 => RenderingDevice.DataFormat.B8G8R8A8Unorm,
            SKColorType.Rgba1010102 => RenderingDevice.DataFormat.A2B10G10R10UnormPack32,
            SKColorType.Rgba16161616 => RenderingDevice.DataFormat.R16G16B16A16Unorm,
            _ => RenderingDevice.DataFormat.R8G8B8A8Unorm,
        };

        /// <summary>
        /// The sRGB twin of <see cref="ToDataFormat"/>'s result, or <c>null</c> when the format has
        /// none. See <see cref="SkiaGodotBackend.CreateTexture"/> for why Godot needs it declared.
        /// </summary>
        static RenderingDevice.DataFormat? ToSrgbDataFormat(SKColorType colorType) => colorType switch
        {
            SKColorType.Bgra8888 => RenderingDevice.DataFormat.B8G8R8A8Srgb,
            SKColorType.Rgba1010102 => null,
            SKColorType.Rgba16161616 => null,
            _ => RenderingDevice.DataFormat.R8G8B8A8Srgb,
        };
    }
}
