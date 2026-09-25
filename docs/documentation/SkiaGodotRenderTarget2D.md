# SkiaGodotRenderTarget2D

## Definition

`SkiaGodotRenderTarget2D` is a GPU texture that SkiaSharp renders into and Godot displays like any
other `Texture2D`. It mirrors the other engines' Begin/Canvas/End shape, over `SkiaGameRendering.Core.VK`
when Godot runs on Vulkan and `SkiaGameRendering.Core.D3D12` when it runs on D3D12 (the backend is
chosen at run time from `RenderingServer.GetCurrentRenderingDriverName()`). Like the raylib and
Stride adapters it is a standalone class: it does **not** go through `SkiaBackend`/`SkiaRenderer`,
which are typed to MonoGame's `Texture2D`/`GraphicsDevice`. In Godot the scene tree draws the
texture, so unlike the other adapters there is no composite step - `End()` only submits.

Namespace: `SkiaGameRendering.Godot`

Assembly/package: `SkiaGameRendering.Godot`

```csharp
public sealed class SkiaGodotRenderTarget2D : IDisposable
```

## Constructor

| Signature | Description |
| --- | --- |
| `SkiaGodotRenderTarget2D(int width, int height, SKColorType colorType = SKColorType.Rgba8888)` | Auto-initializes `SkiaGodotRenderer` against Godot's global `RenderingDevice` on first use and allocates a fixed-size RD texture. Must be called on the render thread (the main thread under the default "Safe" thread model). Stalls the GPU once, briefly, to hand the texture to Godot in a known state - create targets up front. |

## Members

| Member | Description |
| --- | --- |
| `Texture` | A `Texture2D` (a `Texture2DRD` viewing the RD texture) to assign to a `Sprite2D`, `TextureRect`, material, etc. Updates in place; nothing needs re-assigning after `End()`. |
| `TextureRid` | The `RenderingDevice` texture RID, for **fragment-shader sampling** at the RD level (e.g. a `SamplerWithTexture` uniform in your own shader). Owned by this object; do not free it, copy to/from it, clear it, or bind it as a storage image - Godot would move it out of the sampled state this object keeps it in. |
| `Width`, `Height` | The fixed size given to the constructor. |
| `Canvas` | The `SKCanvas` to draw on. Only valid between `Begin` and `End`. |
| `Begin(bool clear = true)` | Starts a render pass; with `clear` false the previous contents are kept. Throws off the render thread. |
| `End()` | Submits Skia's GPU work to Godot's queue and hands the texture back in the state Godot expects (Vulkan: a layout barrier; D3D12: a `CopyResource` into Godot's texture with barriers around it), without waiting on the GPU. No composite. |
| `Dispose()` | Frees Skia's resources, detaches the `Texture2DRD` view, and frees the RD texture. Must not be called between `Begin`/`End`. Under the "Separate" thread model the GPU half is handed to the render thread and completes a frame or so later. |
| `static CreatePremultipliedAlphaMaterial()` | A `CanvasItemMaterial` with `BlendMode = PremultAlpha`, matching Skia's premultiplied output. Assign to the displaying node when the canvas has transparent areas. |

## Related types

| Type | Role |
| --- | --- |
| `SkiaGodotRenderer` | Static holder for the shared backend, mirroring `SkiaRaylibRenderer`/`SkiaStrideVulkanRenderer`. `Initialize(RenderingDevice? = null)` is optional - call it to fail fast on an unsupported driver. `Driver` reports `"vulkan"` or `"d3d12"`, `IsZeroCopy` whether Skia draws straight into Godot's texture, `D3D12UsesEnhancedBarriers` which state-tracking mode Godot's D3D12 device runs in. `Dispose()` releases the backend. |
| `SkiaGameRendering.Core.VK.VkImageLayoutTransitioner` | Added for this adapter: queues `vkCmdPipelineBarrier` layout transitions on the host's queue through a ring of command buffers, resolving its entry points through `vkGetDeviceProcAddr`. Any Vulkan host whose engine tracks image layouts needs it. |
| `SkiaGameRendering.Core.D3D12.D3D12ResourceTransitioner` | Its D3D12 twin: queues resource-state transitions and a `CopyResource` bracketed by transitions through a ring of command lists, over raw COM vtables. |

## Example

```csharp
using SkiaGameRendering.Godot;
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
  and `RenderingDevice.GetDriverResource(...)` - `TopmostObject`/`PhysicalDevice`/`LogicalDevice`/
  `CommandQueue`/`QueueFamily` for the `VkInstance`/`VkPhysicalDevice`/`VkDevice`/`VkQueue`/queue
  family on Vulkan, `PhysicalDevice`/`LogicalDevice`/`CommandQueue` for the `IDXGIAdapter1`/
  `ID3D12Device`/`ID3D12CommandQueue` on D3D12, and `Texture`/`TextureDataFormat` for an RD texture's
  native handle and format. A Godot version bump breaks this at compile time, not at runtime, so
  there is no reflection pin test.
- **Godot tracks image layouts and resource states itself, and this adapter keeps both sides
  truthful.** Godot's render graph derives a texture's layout from the last usage it recorded and
  emits exactly one barrier for a texture it only samples, then no more. SkiaSharp 3.119.4 cannot
  be asked which layout or state to leave a resource in. So the constructor runs a one-triangle
  fragment-shader pass that samples the texture and flushes the render graph, so Godot's single
  transition (whose old layout is `UNDEFINED`, which the spec allows to discard contents) happens
  before the texture holds anything, and after every `End()` the backend returns the texture to
  exactly that state. On Vulkan, `End()` queues a `COLOR_ATTACHMENT_OPTIMAL -> SHADER_READ_ONLY_OPTIMAL`
  barrier, `Begin()` re-wraps the `SKSurface` each frame with that layout as Skia's starting point
  (Skia caches the last layout it set and would otherwise skip its own transition), and `Begin()`
  records a no-op 1x1 draw so even a frame with no other Skia work executes a render pass. On
  D3D12, Skia's own resource stays in `RENDER_TARGET` and the per-frame copy transitions Godot's
  texture from and back to the state Godot believes: `PIXEL_SHADER_RESOURCE` on Godot's legacy
  state-tracking path, `ALL_SHADER_RESOURCE` (the legacy equivalent of `D3D12_BARRIER_LAYOUT_SHADER_RESOURCE`)
  when Godot runs with enhanced barriers, decided the same way Godot decides it
  (`D3D12_FEATURE_D3D12_OPTIONS12.EnhancedBarriersSupported`). Confirmed clean under Godot's
  `--gpu-validation` on both drivers. See `SkiaGodotBackend` and its two implementations for the
  source-level trail.
- **Why D3D12 copies.** Godot's D3D12 driver allocates every texture with its typeless family
  format so it can create UNORM and sRGB views of it, and Skia's D3D12 backend creates its
  render-target view with a null descriptor, which D3D12 rejects for a typeless resource. Skia
  therefore renders into a typed resource this library allocates and one `CopyResource` per frame
  (same typeless family, GPU-to-GPU) lands it in Godot's texture.
- **Texture creation.** The RD texture is created with `SamplingBit | CanCopyFromBit | CanCopyToBit`
  (plus `ColorAttachmentBit` on Vulkan, where Skia renders into it; the copy bits map to the
  `TRANSFER_SRC`/`TRANSFER_DST` usage Skia's Vulkan backend insists on) and declares its UNORM and
  sRGB formats as shareable, because `Texture2DRD` creates an sRGB view of the image, which on
  Vulkan is only legal on an image created with `VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT` (found via
  `--gpu-validation`, VUID 01762).
- **Threading.** Under the default "Safe" thread model Godot renders on the main thread and
  submits once per frame after `_Process`, so Skia's submits from `_Process` are serialized with
  Godot's by construction; no queue lock is needed. Under "Separate" (experimental in Godot),
  construct, `Begin`/`End` and `SkiaGodotRenderer.Initialize` inside
  `RenderingServer.CallOnRenderThread`; `Dispose` may be called from either thread and splits
  itself across both. Verified against `--render-thread separate`.
- **No CPU stall per frame.** `End()` submits without waiting for the GPU
  (`EndDraw(synchronous: false)` on either Core factory): Godot samples the texture in its own frame
  submit on the same queue, which is queue-ordered behind Skia's work, so no fence wait is needed for
  correctness, and the hand-back submissions go through a ring of eight command buffers. The one
  place that does wait is `Dispose`, before Godot frees the texture.
- **Color.** The texture is plain UNORM with no Skia color-space tag: Godot's default gamma-space
  2D pipeline displays Skia's sRGB bytes 1:1 (the sample's pure red and CornflowerBlue read back
  exactly on both drivers). HDR 2D projects are not compensated for.
- See also the [Godot quick start](../godot/quickstart.md).
