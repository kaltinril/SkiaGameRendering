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

        /// <summary>
        /// Allocates a fixed-size texture. Must be called on Godot's render thread (see the class
        /// doc comment); auto-initializes <see cref="SkiaGodotRenderer"/> against Godot's global
        /// <see cref="RenderingDevice"/> on first use. The constructor stalls the GPU briefly once,
        /// to hand the new texture to Godot in a known state - create targets up front, not per frame.
        /// </summary>
        public SkiaGodotRenderTarget2D(int width, int height, SKColorType colorType = SKColorType.Rgba8888)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            SkiaGodotRenderer.RequireRenderThread("The SkiaGodotRenderTarget2D constructor");

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
        /// The underlying <see cref="RenderingDevice"/> texture RID, for <b>sampling</b> at the RD level
        /// (e.g. as a <c>SamplerWithTexture</c> uniform in your own shader). Owned by this object; do
        /// not free it. Do not copy to or from it, clear it, or bind it as a storage image through
        /// <see cref="RenderingDevice"/>: Godot's render graph would then move it out of the sampled
        /// layout this object keeps it in, and Skia's next draw would start from a wrong layout.
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
        /// Begins a render pass. With <paramref name="clear"/> false the previous contents are kept
        /// and drawn over. Throws if a previous <see cref="Begin"/> hasn't been closed with
        /// <see cref="End"/> yet, or if called off Godot's render thread.
        /// </summary>
        public void Begin(bool clear = true)
        {
            if (_target == null)
                throw new ObjectDisposedException(nameof(SkiaGodotRenderTarget2D));
            if (_hasBegun)
                throw new InvalidOperationException("Begin cannot be called again until End has been called.");
            SkiaGodotRenderer.RequireRenderThread("SkiaGodotRenderTarget2D.Begin");

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
        /// <para>
        /// Under the default "Safe" thread model this completes synchronously. Under "Separate",
        /// disposal has a main-thread half (detaching the texture from the scene) and a render-thread
        /// half (freeing GPU resources); called from either thread, this runs its own half now and
        /// hands the other to the right thread, so the GPU release finishes a frame or so later.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_target == null)
                return;
            if (_hasBegun)
                throw new InvalidOperationException("Dispose cannot be called between Begin and End; call End first.");

            var target = _target;
            _target = null;

            bool onRenderThread = RenderingServer.IsOnRenderThread();
            bool onMainThread = OS.GetThreadCallerId() == OS.GetMainThreadId();

            if (onRenderThread && onMainThread)
            {
                target.Dispose();
            }
            else if (onMainThread)
            {
                target.ReleaseSceneTexture();
                RenderingServer.CallOnRenderThread(Callable.From(target.Dispose));
            }
            else if (onRenderThread)
            {
                Callable.From(() =>
                {
                    target.ReleaseSceneTexture();
                    RenderingServer.CallOnRenderThread(Callable.From(target.Dispose));
                }).CallDeferred();
            }
            else
            {
                throw new InvalidOperationException(
                    "SkiaGodotRenderTarget2D.Dispose must be called from Godot's main thread or its render thread.");
            }
        }
    }
}
