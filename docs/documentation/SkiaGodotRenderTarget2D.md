# SkiaGodotRenderTarget2D

## Definition

`SkiaGodotRenderTarget2D` is a GPU texture that SkiaSharp renders directly into and Godot displays
like any other `Texture2D`. It mirrors the other engines' Begin/Canvas/End shape via Vulkan (the
interop `SkiaGameRendering.Core.VK` provides), but like the raylib and Stride adapters it is a
standalone class: it does **not** go through `SkiaBackend`/`SkiaRenderer`, which are typed to
MonoGame's `Texture2D`/`GraphicsDevice`. In Godot the scene tree draws the texture, so unlike the
other adapters there is no composite step - `End()` only flushes.

Namespace: `SkiaGameRendering.Godot.VK`

Assembly/package: `SkiaGameRendering.Godot.VK`

```csharp
public sealed class SkiaGodotRenderTarget2D : IDisposable
```

## Constructor

| Signature | Description |
| --- | --- |
| `SkiaGodotRenderTarget2D(int width, int height, SKColorType colorType = SKColorType.Rgba8888)` | Auto-initializes `SkiaGodotRenderer` against Godot's global `RenderingDevice` on first use and allocates a fixed-size RD texture. Must be called on the render thread (the main thread under the default "Safe" thread model). |

## Members

| Member | Description |
| --- | --- |
| `Texture` | The `Texture2DRD` viewing the RD texture. Assign it to a `Sprite2D`, `TextureRect`, material, etc. Updates in place; nothing needs re-assigning after `End()`. |
| `TextureRid` | The `RenderingDevice` texture RID, for RD-level use (e.g. a compute shader's uniform set). Owned by this object; do not free it. |
| `Width`, `Height` | The fixed size given to the constructor. |
| `Canvas` | The `SKCanvas` to draw on. Only valid between `Begin` and `End`. |
| `Begin(bool clear = true)` | Re-wraps the texture for Skia at its current Vulkan layout and starts a render pass. Throws off the render thread. |
| `End()` | Flushes Skia's GPU work and submits it to Godot's queue, then submits a barrier returning the image to the layout Godot expects. No composite. |
| `Dispose()` | Frees the Skia surface, the `Texture2DRD` view, and the RD texture. Must not be called between `Begin`/`End`. |
| `static CreatePremultipliedAlphaMaterial()` | A `CanvasItemMaterial` with `BlendMode = PremultAlpha`, matching Skia's premultiplied output. Assign to the displaying node when the canvas has transparent areas. |

## Related types

| Type | Role |
| --- | --- |
| `SkiaGodotRenderer` | Static holder for the shared `SkiaGodotVulkanContext`, mirroring `SkiaRaylibRenderer`/`SkiaStrideVulkanRenderer`. `Initialize(RenderingDevice? = null)` is optional - call it to fail fast if the project is not on the Vulkan `RenderingDevice`. `Dispose()` releases it. |
| `SkiaGameRendering.Core.VK.VkImageLayoutTransitioner` | New in Core.VK for this adapter: records and submits one `vkCmdPipelineBarrier` layout transition on the host's queue, resolving its entry points through `vkGetDeviceProcAddr`. Any Vulkan host whose engine tracks image layouts needs it. |

## Example

```csharp
using SkiaGameRendering.Godot.VK;
using SkiaSharp;

var skia = new SkiaGodotRenderTarget2D(512, 512);
AddChild(new Sprite2D { Texture = skia.Texture, Centered = false });

// every frame:
skia.Begin();
skia.Canvas.DrawCircle(256, 256, 200, paint);
skia.End();
```

## Remarks

- **No reflection.** Every handle comes from public API: `RenderingServer.GetRenderingDevice()`
  and `RenderingDevice.GetDriverResource(DriverResource.TopmostObject | PhysicalDevice |
  LogicalDevice | CommandQueue | QueueFamily, ...)` for the `VkInstance`/`VkPhysicalDevice`/
  `VkDevice`/`VkQueue`/queue family, and `DriverResource.Texture`/`TextureDataFormat` for an RD
  texture's `VkImage`/`VkFormat`. A Godot version bump breaks this at compile time, not at
  runtime, so there is no reflection pin test.
- **Godot tracks image layouts itself, and this adapter keeps it truthful.** Godot's
  `RenderingDeviceGraph` derives a texture's `VkImageLayout` from the last usage it recorded. A
  texture Godot only samples gets one `UNDEFINED -> SHADER_READ_ONLY_OPTIMAL` barrier the first
  frame it is drawn and then no barriers ever again. Skia leaves a wrapped render target in
  `COLOR_ATTACHMENT_OPTIMAL`, and SkiaSharp 3.119.4 has no way to request another post-flush
  layout. So `End()` submits an explicit `COLOR_ATTACHMENT_OPTIMAL -> SHADER_READ_ONLY_OPTIMAL`
  barrier after Skia's synchronous flush, and `Begin()` re-creates the `GRBackendRenderTarget`/
  `SKSurface` each frame with that layout as Skia's starting point (Skia caches the last layout it
  set and would otherwise skip its own transition). Confirmed clean under Godot's
  `--gpu-validation` with the Khronos validation layer. The only spec-permitted loose end is
  Godot's first barrier transitioning from `UNDEFINED`, which lets a driver discard contents; per-
  frame redraw hides it and no such driver has been observed. See `SkiaGodotVulkanContext`'s doc
  comment for the source-level trail.
- **Texture creation.** The RD texture is created with `SamplingBit | ColorAttachmentBit |
  CanCopyFromBit | CanCopyToBit` (the last two map to the `TRANSFER_SRC`/`TRANSFER_DST` usage
  Skia's Vulkan backend insists on) and declares its UNORM and sRGB formats as shareable, because
  `Texture2DRD` creates an sRGB view of the image, which is only legal on an image created with
  `VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT` (found via `--gpu-validation`, VUID 01762).
- **Threading.** Under the default "Safe" thread model Godot renders on the main thread and
  submits once per frame after `_Process`, so Skia's submits from `_Process` are serialized with
  Godot's by construction; no queue lock is needed. Under "Separate", use
  `RenderingServer.CallOnRenderThread`.
- **Color.** The texture is plain UNORM with no Skia color-space tag: Godot's default gamma-space
  2D pipeline displays Skia's sRGB bytes 1:1 (the sample's pure red and CornflowerBlue read back
  exactly). HDR 2D projects are not compensated for.
- See also the [Godot quick start](../godot/quickstart.md).
