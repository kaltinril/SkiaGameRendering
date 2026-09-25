using Godot;
using SkiaSharp;

namespace SkiaGameRendering.Godot
{
    /// <summary>
    /// The per-driver half of the Godot adapter. <see cref="SkiaGodotRenderer"/> picks one from
    /// <see cref="RenderingServer.GetCurrentRenderingDriverName"/>: <see cref="VulkanGodotBackend"/> and
    /// <see cref="D3D12GodotBackend"/> for the two <see cref="RenderingDevice"/> drivers (sharing
    /// <see cref="RenderingDeviceGodotBackend"/>), <see cref="GlCompatibilityGodotBackend"/> for the
    /// Compatibility renderer, which has no RenderingDevice at all and shares a native GL context
    /// instead. The public <see cref="SkiaGodotRenderTarget2D"/> API is identical over all of them.
    ///
    /// Unlike every other engine adapter in this repo, NOTHING here is reached by reflection:
    /// <see cref="RenderingDevice.GetDriverResource"/>, <see cref="RenderingServer.TextureGetNativeHandle"/>
    /// and <see cref="DisplayServer.WindowGetNativeHandle"/> are public API that return the raw native
    /// handles (Godot 4.3+). A Godot version bump therefore breaks this adapter at compile time, not
    /// silently at runtime - there is no reflection pin test because the GodotSharp reference IS the pin.
    ///
    /// <b>Render thread only</b>, on every backend: <c>RenderingDevice::get_driver_resource</c> and every
    /// RD command method open with <c>ERR_RENDER_THREAD_GUARD</c>, and the Compatibility renderer's GL
    /// context is current on that thread. Under the default <c>rendering/driver/threads/thread_model</c>
    /// ("Safe") the render thread IS the main thread, so <c>_Process</c>/<c>_Ready</c> qualify; under
    /// "Separate" the caller must go through <see cref="RenderingServer.CallOnRenderThread"/>. Every
    /// public entry point checks <see cref="RenderingServer.IsOnRenderThread"/> and throws with that
    /// instruction. It is also what makes queue/context access safe by construction: Godot only
    /// submits from the render thread, once per frame after <c>_Process</c>, so nothing here needs a lock.
    /// </summary>
    internal abstract class SkiaGodotBackend : IDisposable
    {
        /// <summary>The <see cref="RenderingServer.GetCurrentRenderingDriverName"/> value this backend serves.</summary>
        internal abstract string DriverName { get; }

        /// <summary>Whether Skia draws straight into the texture Godot displays (no per-frame copy).</summary>
        internal abstract bool IsZeroCopy { get; }

        /// <param name="explicitDevice">A <see cref="RenderingDevice"/> the caller chose, or <c>null</c> for the global one; ignored by backends that have no RenderingDevice.</param>
        internal abstract void Initialize(RenderingDevice? explicitDevice);

        internal abstract SkiaGodotTargetResources CreateTarget(int width, int height, SKColorType colorType);

        /// <summary>Blocks until the backend's most recent GPU submission has executed (and, by queue order, everything before it).</summary>
        internal abstract void WaitForPendingGpuWork();

        public abstract void Dispose();
    }

    /// <summary>
    /// Everything behind one <see cref="SkiaGodotRenderTarget2D"/>: the Godot-side texture the scene
    /// tree displays and whatever Skia draws into on that backend. Disposal is split in two because
    /// Godot's "Separate" thread model needs it (see <see cref="SkiaGodotRenderTarget2D.Dispose"/>):
    /// <see cref="ReleaseSceneTexture"/> is the main-thread half, <see cref="Dispose"/> the render-thread half.
    /// </summary>
    internal abstract class SkiaGodotTargetResources : IDisposable
    {
        internal abstract Texture2D Texture { get; }

        /// <summary>The <see cref="RenderingDevice"/> texture RID on RD backends; an invalid RID on backends without one.</summary>
        internal abstract Rid TextureRid { get; }

        internal abstract SKSurface BeginFrame();

        internal abstract void EndFrame();

        /// <summary>Detaches the texture from whatever the scene tree still holds. Main thread. Idempotent; <see cref="Dispose"/> calls it too.</summary>
        internal abstract void ReleaseSceneTexture();

        /// <summary>Frees the GPU-side resources. Render thread.</summary>
        public abstract void Dispose();
    }
}
