using Godot;

namespace SkiaGameRendering.Godot.VK
{
    /// <summary>
    /// Holds the shared <c>SkiaGodotVulkanContext</c> for Godot's global <see cref="RenderingDevice"/>.
    /// Godot analog of <c>SkiaRaylibRenderer</c>/<c>SkiaStrideVulkanRenderer</c>. Most code never
    /// calls this directly - constructing a <see cref="SkiaGodotRenderTarget2D"/> auto-initializes it
    /// against <see cref="RenderingServer.GetRenderingDevice"/>. Call <see cref="Initialize"/>
    /// explicitly only to make initialization (and any failure, such as the project running on the
    /// D3D12 or Compatibility renderer) happen at a known point rather than lazily on first render
    /// target construction.
    /// </summary>
    public static class SkiaGodotRenderer
    {
        static SkiaGodotVulkanContext? _context;

        public static bool IsInitialized => _context != null;

        /// <param name="renderingDevice">
        /// The device to render on; <c>null</c> (the default) means Godot's global one,
        /// <see cref="RenderingServer.GetRenderingDevice"/>. A local device from
        /// <see cref="RenderingServer.CreateLocalRenderingDevice"/> is accepted but its textures
        /// cannot be shown in the scene tree, by Godot's own rules.
        /// </param>
        public static void Initialize(RenderingDevice? renderingDevice = null)
        {
            renderingDevice ??= RenderingServer.GetRenderingDevice()
                ?? throw new InvalidOperationException(
                    "RenderingServer.GetRenderingDevice() returned null. SkiaGameRendering.Godot.VK needs the Forward+ or Mobile " +
                    "renderer (rendering/renderer/rendering_method) on the Vulkan driver; the Compatibility (OpenGL) renderer and " +
                    "headless mode have no RenderingDevice.");

            if (_context != null)
                throw new InvalidOperationException(
                    "SkiaGodotRenderer is already initialized. Call SkiaGodotRenderer.Dispose() before initializing again.");

            var context = new SkiaGodotVulkanContext();
            try
            {
                context.Initialize(renderingDevice);
            }
            catch
            {
                context.Dispose();
                throw;
            }
            _context = context;
        }

        /// <summary>
        /// Throws unless the caller is on Godot's render thread - the main thread under the default
        /// "Safe" thread model, a dedicated thread under "Separate". Every <see cref="RenderingDevice"/>
        /// call this library makes is guarded by Godot with <c>ERR_RENDER_THREAD_GUARD</c>, which
        /// prints an error and returns a null result; checking first turns that into an exception
        /// that says what to do instead.
        /// </summary>
        internal static void RequireRenderThread(string what)
        {
            if (!RenderingServer.IsOnRenderThread())
                throw new InvalidOperationException(
                    what + " must run on Godot's render thread. Under the default 'Safe' thread model that is the main " +
                    "thread (_Ready/_Process/_Draw); under 'Separate', wrap the call in RenderingServer.CallOnRenderThread.");
        }

        /// <summary>
        /// Called by <see cref="SkiaGodotRenderTarget2D"/>'s constructor. Auto-initializes against
        /// Godot's global <see cref="RenderingDevice"/> if nothing has initialized the renderer yet.
        /// </summary>
        internal static SkiaGodotVulkanContext EnsureInitialized()
        {
            if (_context == null)
                Initialize();
            return _context!;
        }

        /// <summary>
        /// Disposes the shared context. Dispose any live <see cref="SkiaGodotRenderTarget2D"/>
        /// instances first - this does not track or dispose them for you. Under the "Separate" thread
        /// model, calling this from the main thread queues the teardown onto the render thread.
        /// </summary>
        public static void Dispose()
        {
            if (_context == null)
                return;

            var context = _context;
            _context = null;
            if (RenderingServer.IsOnRenderThread())
                context.Dispose();
            else
                RenderingServer.CallOnRenderThread(Callable.From(context.Dispose));
        }
    }
}
