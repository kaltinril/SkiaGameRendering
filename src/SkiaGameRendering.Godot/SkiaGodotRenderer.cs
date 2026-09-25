using Godot;

namespace SkiaGameRendering.Godot
{
    /// <summary>
    /// Holds the shared backend (<see cref="VulkanGodotBackend"/>, <see cref="D3D12GodotBackend"/> or
    /// <see cref="GlCompatibilityGodotBackend"/>, chosen from
    /// <see cref="RenderingServer.GetCurrentRenderingDriverName"/>). Godot analog of <c>SkiaRaylibRenderer</c>/<c>SkiaStrideVulkanRenderer</c>.
    /// Most code never calls this directly - constructing a <see cref="SkiaGodotRenderTarget2D"/>
    /// auto-initializes it. Call <see cref="Initialize"/> explicitly only to make initialization (and
    /// any failure, such as the project running on the Metal driver) happen at a known point rather
    /// than lazily on first render target construction.
    /// </summary>
    public static class SkiaGodotRenderer
    {
        static SkiaGodotBackend? _backend;

        public static bool IsInitialized => _backend != null;

        /// <summary>
        /// The Godot rendering driver the initialized backend runs on - <c>"vulkan"</c>, <c>"d3d12"</c>
        /// or <c>"opengl3"</c> - or <c>null</c> before <see cref="Initialize"/>. Same values as
        /// <see cref="RenderingServer.GetCurrentRenderingDriverName"/>.
        /// </summary>
        public static string? Driver => _backend?.DriverName;

        /// <summary>
        /// <c>true</c> when Skia draws straight into Godot's texture (Vulkan, Compatibility); <c>false</c>
        /// when the backend copies Skia's own resource into it each frame (D3D12 - see
        /// <see cref="D3D12GodotBackend"/> for why). <c>null</c> before <see cref="Initialize"/>.
        /// </summary>
        public static bool? IsZeroCopy => _backend?.IsZeroCopy;

        /// <summary>
        /// On D3D12, whether Godot's device runs with enhanced barriers (which changes how Godot
        /// tracks texture state and therefore which state this library hands textures back in - see
        /// <see cref="D3D12GodotBackend"/>). <c>null</c> before <see cref="Initialize"/> or on Vulkan.
        /// Worth including in a bug report.
        /// </summary>
        public static bool? D3D12UsesEnhancedBarriers => (_backend as D3D12GodotBackend)?.EnhancedBarriers;

        /// <param name="renderingDevice">
        /// The device to render on; <c>null</c> (the default) means Godot's global one,
        /// <see cref="RenderingServer.GetRenderingDevice"/>. A local device from
        /// <see cref="RenderingServer.CreateLocalRenderingDevice"/> is accepted but its textures
        /// cannot be shown in the scene tree, by Godot's own rules.
        /// </param>
        public static void Initialize(RenderingDevice? renderingDevice = null)
        {
            if (_backend != null)
                throw new InvalidOperationException(
                    "SkiaGodotRenderer is already initialized. Call SkiaGodotRenderer.Dispose() before initializing again.");

            RequireRenderThread("SkiaGodotRenderer.Initialize");

            var driverName = RenderingServer.GetCurrentRenderingDriverName();
            SkiaGodotBackend backend = driverName switch
            {
                "vulkan" => new VulkanGodotBackend(),
                "d3d12" => new D3D12GodotBackend(),
                "opengl3" => new GlCompatibilityGodotBackend(),
                "metal" => throw new NotSupportedException(
                    "SkiaGameRendering.Godot does not support Godot's Metal driver yet (no Metal interop in this library). " +
                    "Set the project setting rendering/rendering_device/driver.macos to \"vulkan\" (MoltenVK), or run with " +
                    "--rendering-driver vulkan."),
                "opengl3_angle" or "opengl3_es" => throw new NotSupportedException(
                    $"SkiaGameRendering.Godot supports Godot's Compatibility renderer only on the native 'opengl3' driver (Windows WGL, " +
                    $"Linux X11/GLX), not '{driverName}' (an EGL context). Use the Forward+ or Mobile renderer, or set " +
                    "rendering/gl_compatibility/driver to opengl3."),
                "dummy" => throw new InvalidOperationException(
                    "Godot is running headless (the dummy rendering driver); there is no GPU device for Skia to share."),
                _ => throw new NotSupportedException(
                    $"SkiaGameRendering.Godot does not support Godot's '{driverName}' rendering driver. Supported: vulkan, d3d12, opengl3 " +
                    "(project settings rendering/rendering_device/driver and rendering/gl_compatibility/driver, with their per-platform overrides)."),
            };

            try
            {
                backend.Initialize(renderingDevice);
            }
            catch
            {
                backend.Dispose();
                throw;
            }
            _backend = backend;
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
        internal static SkiaGodotBackend EnsureInitialized()
        {
            if (_backend == null)
                Initialize();
            return _backend!;
        }

        /// <summary>
        /// Disposes the shared backend. Dispose any live <see cref="SkiaGodotRenderTarget2D"/>
        /// instances first - this does not track or dispose them for you. Under the "Separate" thread
        /// model, calling this from the main thread queues the teardown onto the render thread.
        /// </summary>
        public static void Dispose()
        {
            if (_backend == null)
                return;

            var backend = _backend;
            _backend = null;
            if (RenderingServer.IsOnRenderThread())
                backend.Dispose();
            else
                RenderingServer.CallOnRenderThread(Callable.From(backend.Dispose));
        }
    }
}
