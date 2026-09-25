using Godot;
using SkiaSharp;

namespace SkiaGameRendering.Godot.VK
{
    /// <summary>
    /// A GPU texture that SkiaSharp renders directly into and Godot displays like any other
    /// <see cref="Texture2D"/>: assign <see cref="Texture"/> to a <see cref="Sprite2D"/>,
    /// <see cref="TextureRect"/>, material, or anything else that takes one. Same Begin/Canvas/End
    /// shape as the other engines' render targets in this repo, minus a composite step -
    /// in Godot the scene tree draws the texture, so <see cref="End"/> only flushes.
    /// <code>
    /// var skia = new SkiaGodotRenderTarget2D(512, 512);
    /// AddChild(new Sprite2D { Texture = skia.Texture, Centered = false });
    ///
    /// // every frame, e.g. in _Process:
    /// skia.Begin();
    /// skia.Canvas.DrawCircle(256, 256, 200, paint);
    /// skia.End();
    /// </code>
    /// Requires the Forward+ or Mobile renderer on the Vulkan driver, and must be used on the render
    /// thread - the main thread under Godot's default "Safe" thread model, or inside
    /// <see cref="RenderingServer.CallOnRenderThread"/> under "Separate". See
    /// <c>SkiaGodotVulkanContext</c> for what happens underneath.
    /// </summary>
    public sealed class SkiaGodotRenderTarget2D : IDisposable
    {
        SkiaGodotTarget? _target;
        bool _hasBegun;

        public SkiaGodotRenderTarget2D(int width, int height, SKColorType colorType = SKColorType.Rgba8888)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            var context = SkiaGodotRenderer.EnsureInitialized();
            _target = new SkiaGodotTarget(context, width, height, colorType);
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>
        /// The engine-side <see cref="Texture2Drd"/> viewing Skia's texture - assign it wherever Godot
        /// takes a <see cref="Texture2D"/>. Its contents update in place; nothing needs re-assigning
        /// after each <see cref="End"/>.
        /// </summary>
        public Texture2Drd Texture =>
            (_target ?? throw new ObjectDisposedException(nameof(SkiaGodotRenderTarget2D))).Texture;

        /// <summary>
        /// The underlying <see cref="RenderingDevice"/> texture RID, for callers working at the RD
        /// level (e.g. binding it in a compute shader's uniform set). Owned by this object; do not
        /// free it.
        /// </summary>
        public Rid TextureRid =>
            (_target ?? throw new ObjectDisposedException(nameof(SkiaGodotRenderTarget2D))).TextureRid;

        /// <summary>
        /// The canvas to draw on. Only valid between <see cref="Begin"/> and <see cref="End"/>;
        /// accessing it outside that window throws.
        /// </summary>
        public SKCanvas Canvas => _hasBegun
            ? _target!.Surface.Canvas
            : throw new InvalidOperationException("Begin must be called before accessing Canvas.");

        /// <summary>
        /// Begins a render pass. Throws if a previous <see cref="Begin"/> hasn't been closed with
        /// <see cref="End"/> yet, or if called off Godot's render thread.
        /// </summary>
        public void Begin(bool clear = true)
        {
            if (_target == null)
                throw new ObjectDisposedException(nameof(SkiaGodotRenderTarget2D));
            if (_hasBegun)
                throw new InvalidOperationException("Begin cannot be called again until End has been called.");
            if (!RenderingServer.IsOnRenderThread())
                throw new InvalidOperationException(
                    "SkiaGodotRenderTarget2D must be used on Godot's render thread. Under the default 'Safe' thread model " +
                    "that is the main thread (_Process/_Draw); under 'Separate', wrap the Begin/End block in RenderingServer.CallOnRenderThread.");

            var surface = _target.BeginFrame();
            _hasBegun = true;

            if (clear)
                surface.Canvas.Clear();
        }

        /// <summary>
        /// Ends the render pass started by <see cref="Begin"/>: flushes Skia's queued GPU work,
        /// submits it to Godot's queue, and hands the texture back in the layout Godot expects.
        /// Nothing is composited - <see cref="Texture"/> is already in the scene tree wherever you
        /// put it. Throws if <see cref="Begin"/> wasn't called first.
        /// </summary>
        public void End()
        {
            if (!_hasBegun)
                throw new InvalidOperationException("Begin must be called before calling End.");

            try
            {
                _target!.EndFrame();
            }
            finally
            {
                _hasBegun = false;
            }
        }

        /// <summary>
        /// A <see cref="CanvasItemMaterial"/> whose blend mode matches Skia's premultiplied-alpha
        /// output. Assign it to the node displaying <see cref="Texture"/> whenever the canvas has
        /// transparent areas (e.g. after <c>Clear()</c> to transparent); opaque content needs no
        /// material.
        /// </summary>
        public static CanvasItemMaterial CreatePremultipliedAlphaMaterial() =>
            new() { BlendMode = CanvasItemMaterial.BlendModeEnum.PremultAlpha };

        /// <summary>
        /// Releases the RD texture and Skia surface. Throws if called between <see cref="Begin"/>
        /// and <see cref="End"/>. Nodes still displaying <see cref="Texture"/> show nothing afterward.
        /// </summary>
        public void Dispose()
        {
            if (_target == null)
                return;
            if (_hasBegun)
                throw new InvalidOperationException("Dispose cannot be called between Begin and End; call End first.");

            _target.Dispose();
            _target = null;
        }
    }
}
