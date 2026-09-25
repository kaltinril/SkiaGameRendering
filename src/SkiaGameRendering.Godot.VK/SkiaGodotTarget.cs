using Godot;
using SkiaGameRendering.Core.VK;
using SkiaSharp;

namespace SkiaGameRendering.Godot.VK
{
    /// <summary>
    /// Owns the GPU resources backing one <see cref="SkiaGodotRenderTarget2D"/>: the
    /// <see cref="RenderingDevice"/> texture, the <see cref="Texture2Drd"/> that exposes it to the
    /// scene tree, and the per-frame <see cref="SKSurface"/>/<see cref="GRBackendRenderTarget"/>
    /// wrapping it. Mirrors <c>SkiaStrideVulkanTarget</c>, with one deliberate difference:
    /// <para>
    /// <b>The Skia surface is re-created every frame, not once.</b> Skia remembers the layout it last
    /// put the image in (<c>COLOR_ATTACHMENT_OPTIMAL</c>) and, on the next draw, skips the transition
    /// if it believes nothing changed. But <see cref="SkiaGodotVulkanContext.TransitionToShaderRead"/>
    /// DID change it, to the <c>SHADER_READ_ONLY_OPTIMAL</c> Godot expects (see that class's doc
    /// comment for why). Wrapping a fresh <see cref="GRBackendRenderTarget"/> with the real current
    /// layout each <see cref="BeginFrame"/> is the only way SkiaSharp 3.119.4 offers to tell Skia
    /// where the image actually is, so Skia's own first barrier of the frame
    /// (<c>SHADER_READ_ONLY -> COLOR_ATTACHMENT</c>) is correct too. The cost is a small Skia-side
    /// object rebuild per frame, not a GPU allocation - the <c>VkImage</c> is Godot's and persists.
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
        readonly SkiaGodotVulkanContext _context;
        readonly int _width;
        readonly int _height;
        readonly SKColorType _colorType;
        Rid _textureRid;
        ulong _image;
        Texture2Drd? _texture;
        SKSurface? _surface;
        GRBackendRenderTarget? _renderTarget;
        uint _currentLayout = VkConstants.ImageLayoutUndefined;
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
                _image = context.GetImage(_textureRid);
                if (_image == 0)
                    throw new InvalidOperationException("Godot RenderingDevice returned a null VkImage for the Skia texture (DriverResource.Texture).");

                // Texture2Drd is the engine's own "show an RD texture in the scene tree" resource:
                // RenderingServer.texture_rd_create builds a shared VIEW of our RD texture (no copy),
                // so whatever Skia draws into the VkImage is what Sprite2D/TextureRect display.
                _texture = new Texture2Drd { TextureRdRid = _textureRid };
            }
            catch
            {
                context.RenderingDevice.FreeRid(_textureRid);
                _textureRid = default;
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
                var state = _context.CreateTextureState(_textureRid, _currentLayout);
                var result = _context.CreateSurface(state, _width, _height, _colorType);
                _surface = result.surface;
                _renderTarget = result.renderTarget;
                return _surface;
            }
            catch
            {
                _context.EndDraw();
                throw;
            }
        }

        /// <summary>
        /// Flushes and submits Skia's work (synchronously - see <see cref="VkSkiaSurfaceFactory.EndDraw"/>),
        /// then hands the image back to Godot in the layout its render graph expects.
        /// </summary>
        internal void EndFrame()
        {
            try
            {
                _surface?.Flush();
            }
            finally
            {
                _context.EndDraw();
            }

            _context.TransitionToShaderRead(_image);
            _currentLayout = VkConstants.ImageLayoutShaderReadOnlyOptimal;
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

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _context.BeginDraw();
            try
            {
                DisposeSurface();
            }
            finally
            {
                _context.EndDraw();
            }

            // Order matters: the Texture2Drd (and its RenderingServer view) must let go of the RD
            // texture before the RD texture itself is freed - freeing the RD RID first leaves the
            // view dangling, which Godot reports as an error. Clearing TextureRdRid frees the RS
            // view immediately, regardless of who else still references the Texture2Drd resource.
            if (_texture != null)
            {
                _texture.TextureRdRid = default;
                _texture.Dispose();
                _texture = null;
            }
            if (_textureRid.IsValid)
            {
                _context.RenderingDevice.FreeRid(_textureRid);
                _textureRid = default;
            }
            _image = 0;
        }
    }
}
