using Godot;
using SkiaGameRendering.Core.VK;
using SkiaSharp;

namespace SkiaGameRendering.Godot.VK
{
    /// <summary>
    /// Owns the GPU resources backing one <see cref="SkiaGodotRenderTarget2D"/>: the
    /// <see cref="RenderingDevice"/> texture, the <see cref="Texture2Drd"/> that exposes it to the
    /// scene tree, and the per-frame <see cref="SKSurface"/>/<see cref="GRBackendRenderTarget"/>
    /// wrapping it. Mirrors <c>SkiaStrideVulkanTarget</c>, with two deliberate differences that both
    /// follow from Godot tracking image layouts itself (see <see cref="SkiaGodotVulkanContext"/>):
    /// <para>
    /// <b>The Skia surface is re-created every frame, not once.</b> Skia remembers the layout it last
    /// put the image in (<c>COLOR_ATTACHMENT_OPTIMAL</c>) and, on the next draw, skips the transition
    /// if it believes nothing changed. But <see cref="SkiaGodotVulkanContext.TransitionToShaderRead"/>
    /// DID change it, to the <c>SHADER_READ_ONLY_OPTIMAL</c> Godot expects. Wrapping a fresh
    /// <see cref="GRBackendRenderTarget"/> with the real current layout each <see cref="BeginFrame"/>
    /// is the only way SkiaSharp 3.119.4 offers to tell Skia where the image actually is. The cost
    /// is a small Skia-side object rebuild per frame, not a GPU allocation - the <c>VkImage</c> is
    /// Godot's and persists.
    /// </para>
    /// <para>
    /// <b>Every frame draws at least one thing.</b> <see cref="BeginFrame"/> records a 1x1
    /// <see cref="SKBlendMode.Modulate"/>-by-white rectangle - visually a no-op (dst * 1 = dst, exact
    /// in 8-bit) that Skia does not cull the way it culls <c>Dst</c>/zero-alpha paints - before the
    /// caller gets the canvas. That guarantees Skia executes a render pass (and so its
    /// <c>-> COLOR_ATTACHMENT_OPTIMAL</c> transition) even for a <c>Begin(clear: false)</c>/
    /// <c>End()</c> frame with no other work, which is what makes <see cref="EndFrame"/>'s hand-back
    /// barrier's <c>oldLayout</c> truthful. If the caller clears the whole surface afterward Skia
    /// folds the sentinel away and keeps the clear, which is a render pass too.
    /// </para>
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
        static readonly SKRect SentinelRect = SKRect.Create(0, 0, 1, 1);

        readonly SkiaGodotVulkanContext _context;
        readonly int _width;
        readonly int _height;
        readonly SKColorType _colorType;
        readonly SKPaint _sentinelPaint = new() { Color = SKColors.White, BlendMode = SKBlendMode.Modulate, IsAntialias = false };
        Rid _textureRid;
        ulong _image;
        uint _format;
        Texture2Drd? _texture;
        SKSurface? _surface;
        GRBackendRenderTarget? _renderTarget;
        bool _disposed;

        internal SkiaGodotTarget(SkiaGodotVulkanContext context, int width, int height, SKColorType colorType)
        {
            _context = context;
            _width = width;
            _height = height;
            _colorType = colorType;

            _textureRid = context.CreateTexture(width, height, ToDataFormat(colorType), ToSrgbDataFormat(colorType));
            try
            {
                (_image, _format) = context.GetImageAndFormat(_textureRid);

                // From here on the image is in SHADER_READ_ONLY_OPTIMAL, Godot knows it, and Godot
                // will never transition it again - see SkiaGodotVulkanContext.PrimeForSampling.
                context.PrimeForSampling(_textureRid);

                // Texture2Drd is the engine's own "show an RD texture in the scene tree" resource:
                // RenderingServer.texture_rd_create builds a shared VIEW of our RD texture (no copy),
                // so whatever Skia draws into the VkImage is what Sprite2D/TextureRect display.
                _texture = new Texture2Drd { TextureRdRid = _textureRid };
            }
            catch
            {
                context.RenderingDevice.FreeRid(_textureRid);
                _textureRid = default;
                _sentinelPaint.Dispose();
                throw;
            }
        }

        internal Rid TextureRid => _disposed ? throw new ObjectDisposedException(nameof(SkiaGodotTarget)) : _textureRid;

        internal Texture2Drd Texture => _texture ?? throw new ObjectDisposedException(nameof(SkiaGodotTarget));

        internal SKSurface Surface => _surface ?? throw new InvalidOperationException("No frame is in progress.");

        /// <summary>Wraps the image at its real current layout and returns the surface to draw on. See this class's doc comment.</summary>
        internal SKSurface BeginFrame()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _context.BeginDraw();
            try
            {
                DisposeSurface();
                var state = _context.CreateTextureState(_image, _format, VkConstants.ImageLayoutShaderReadOnlyOptimal);
                var result = _context.CreateSurface(state, _width, _height, _colorType);
                _surface = result.surface;
                _renderTarget = result.renderTarget;

                _surface.Canvas.DrawRect(SentinelRect, _sentinelPaint);
                return _surface;
            }
            catch
            {
                _context.EndDraw();
                throw;
            }
        }

        /// <summary>
        /// Flushes and submits Skia's work (without a CPU wait - see
        /// <see cref="SkiaGodotVulkanContext.EndDraw"/>), then hands the image back to Godot in the
        /// layout its render graph expects.
        /// </summary>
        internal void EndFrame()
        {
            try
            {
                if (_surface != null)
                {
                    // Unwind any Save()/SaveLayer() the caller left open; the surface is discarded
                    // next frame anyway, but an open SaveLayer would otherwise never be composited.
                    _surface.Canvas.RestoreToCount(1);
                    _surface.Flush();
                }
            }
            finally
            {
                _context.EndDraw();
            }

            _context.TransitionToShaderRead(_image);
        }

        void DisposeSurface()
        {
            _surface?.Dispose();
            _surface = null;
            _renderTarget?.Dispose();
            _renderTarget = null;
        }

        /// <summary>
        /// SKColorType to <see cref="RenderingDevice.DataFormat"/>. The VkFormat Skia is told comes
        /// back from Godot itself (<see cref="RenderingDevice.DriverResource.TextureDataFormat"/>), so
        /// this only has to pick a Godot format, not keep two enums in sync.
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
        /// none. See <see cref="SkiaGodotVulkanContext.CreateTexture"/> for why Godot needs it declared.
        /// </summary>
        static RenderingDevice.DataFormat? ToSrgbDataFormat(SKColorType colorType) => colorType switch
        {
            SKColorType.Bgra8888 => RenderingDevice.DataFormat.B8G8R8A8Srgb,
            SKColorType.Rgba1010102 => null,
            SKColorType.Rgba16161616 => null,
            _ => RenderingDevice.DataFormat.R8G8B8A8Srgb,
        };

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
        /// last user does.
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
        /// Must run on the RENDER thread - it frees the RD texture and the Skia surface.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            ReleaseSceneTexture();

            _context.BeginDraw();
            try
            {
                // Skia submits asynchronously (see SkiaGodotVulkanContext.EndDraw); make sure the
                // last frame's work on this image has landed before Godot destroys the VkImage.
                _context.WaitForPendingGpuWork();
                DisposeSurface();
            }
            finally
            {
                _context.EndDraw();
            }
            _sentinelPaint.Dispose();

            if (_textureRid.IsValid)
            {
                _context.RenderingDevice.FreeRid(_textureRid);
                _textureRid = default;
            }
            _image = 0;
        }
    }
}
