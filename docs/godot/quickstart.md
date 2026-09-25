# Godot quick start

Godot 4 is the first engine this library supports whose graphics device is reachable entirely
through public API: `RenderingDevice.GetDriverResource` hands out the raw Vulkan handles, so
`SkiaGameRendering.Godot.VK` uses no reflection at all. Like the raylib and Stride adapters it does
not go through `SkiaBackend`/`SkiaRenderer` - see
[SkiaGodotRenderTarget2D](../documentation/SkiaGodotRenderTarget2D.md) for how it is structured.

## Prerequisites

- Godot 4.7 or newer, the .NET build (this package references `GodotSharp` 4.7.2 as a floor; a
  newer editor's own `Godot.NET.Sdk` unifies it upward)
- A C# Godot project targeting .NET 8 or newer
- The **Forward+** or **Mobile** renderer on the **Vulkan** driver. Windows, Linux, or macOS with a
  Vulkan-capable driver. The Compatibility (OpenGL) renderer has no `RenderingDevice`; D3D12 and
  Metal are not supported by this package yet (see "Known limitations").

## Add the package

```powershell
dotnet add package SkiaGameRendering.Godot.VK
```

Not on nuget.org yet. Until it is published, clone this repo and reference the project from your
game's `.csproj` instead; the package's dependencies (`SkiaSharp`, `GodotSharp`) restore normally:

```xml
<ItemGroup>
  <ProjectReference Include="path\to\SkiaGameRendering\src\SkiaGameRendering.Godot.VK\SkiaGameRendering.Godot.VK.csproj" />
</ItemGroup>
```

## Pin the project to Vulkan

Godot 4.6+ configures **new** Windows projects to use the `d3d12` driver and macOS to use `metal`.
This package needs Vulkan, so set these in `project.godot` (or in the editor under
Project Settings > Rendering > Rendering Device > Driver, with "Advanced Settings" on):

```ini
[rendering]

renderer/rendering_method="forward_plus"
rendering_device/driver="vulkan"
rendering_device/driver.windows="vulkan"
rendering_device/driver.macos="vulkan"
```

`SkiaGodotRenderer.Initialize` throws with this instruction if the running driver is anything else.
`--rendering-driver vulkan` on the command line works too, for trying it out.

## Initialize and render

Godot has no user-owned `Draw()` loop. A node draws Skia content into a `SkiaGodotRenderTarget2D`
in `_Process`, and the scene tree displays its `Texture` (a stock `Texture2DRD`) through any node
that takes a `Texture2D` - a `Sprite2D`, `TextureRect`, material, and so on:

```cs
using Godot;
using SkiaGameRendering.Godot.VK;
using SkiaSharp;

public partial class SkiaOverlay : Node2D
{
    SkiaGodotRenderTarget2D? _canvas;
    readonly SKPaint _paint = new() { Color = SKColors.Crimson, IsAntialias = true };

    public override void _Ready()
    {
        // Optional - the render target auto-initializes on first use. Calling this explicitly
        // fails fast if the project is not on the Vulkan RenderingDevice.
        SkiaGodotRenderer.Initialize();

        var size = GetViewportRect().Size;
        _canvas = new SkiaGodotRenderTarget2D((int)size.X, (int)size.Y);

        AddChild(new Sprite2D
        {
            Texture = _canvas.Texture,
            Centered = false,
            // Skia's output is premultiplied; Godot's default Mix blend expects straight alpha.
            // Only matters when the canvas has transparent areas.
            Material = SkiaGodotRenderTarget2D.CreatePremultipliedAlphaMaterial(),
        });
    }

    public override void _Process(double delta)
    {
        _canvas!.Begin();                       // clears to transparent by default
        _canvas.Canvas.DrawCircle(100, 100, 80, _paint);
        _canvas.End();                          // flushes; the Sprite2D shows the result this frame
    }

    public override void _ExitTree()
    {
        _canvas?.Dispose();          // dispose render targets before...
        SkiaGodotRenderer.Dispose(); // ...tearing down the shared Vulkan interop
        _paint.Dispose();
    }
}
```

`Begin`/`End`, the constructor, and `SkiaGodotRenderer.Initialize` must run on Godot's render
thread. Under the default `rendering/driver/threads/thread_model` ("Safe") that is the main thread,
so `_Ready`, `_Process` and `_Draw` all qualify. Under "Separate" (experimental in Godot), wrap them
in `RenderingServer.CallOnRenderThread`; they throw with that instruction otherwise. `Dispose` can
be called from either thread.

Full member list and other remarks are documented on
[SkiaGodotRenderTarget2D](../documentation/SkiaGodotRenderTarget2D.md).

## Build and run the sample in this repo

`samples/Sample.Godot.VK` is a complete Godot project (`project.godot`, `Main.tscn`, `Main.cs`).
Build it with the .NET SDK like any other sample, then open or run it with a Godot 4.7 .NET editor
binary - this repo does not ship or download Godot:

```powershell
dotnet build samples\Sample.Godot.VK\Sample.Godot.VK.csproj -c Debug
<path-to>\Godot_v4.7.x-stable_mono_win64.exe --path samples\Sample.Godot.VK
```

Debug configuration matters: Godot loads the project assembly from `.godot/mono/temp/bin/Debug/`
when running a project outside an export. Passing `-- --screenshot out.png` makes the sample save
its fifth frame and quit, which is what `tests/Tests.Godot.VK` uses (set `GODOT_BIN` to the Godot
executable to enable that test; it skips otherwise).

## Known limitations

- **Vulkan only.** Godot's D3D12 (Windows default for new projects since 4.6) and Metal (macOS
  default) drivers expose their handles through the same `GetDriverResource` API, so
  `SkiaGameRendering.Godot.D3D12` on `Core.D3D12` is a natural follow-up; Metal has no Core
  library yet. The Compatibility renderer (OpenGL) has no `RenderingDevice`.
- **HDR 2D** (`rendering/viewport/hdr_2d`) is not compensated for: the texture is a plain UNORM
  format holding Skia's sRGB-encoded bytes, which is exactly right for Godot's default gamma-space
  2D pipeline.
- **One extra queue submission per `End()`, and a one-time GPU stall per constructor.** Godot
  tracks each texture's Vulkan image layout itself and SkiaSharp cannot be told which layout to
  leave an image in, so `End()` submits a small barrier to hand the texture back in the layout
  Godot expects, `Begin()` re-wraps the Skia surface each frame, and the constructor runs a tiny
  compute pass and waits for it so Godot records the texture as sampled before Skia's first draw.
  Create targets up front, not per frame. Per-frame work never waits on the GPU. See the
  documentation page for the full mechanism.
- **`TextureRid` is for sampling only.** Binding it in your own shader is fine; copying to or from
  it, clearing it, or using it as a storage image through `RenderingDevice` moves it out of the
  layout this library keeps it in.
- Verified on Windows with an NVIDIA GPU under Godot 4.7.2, including a clean run under Godot's
  `--gpu-validation` (Khronos validation layer). Linux and macOS are expected to work identically
  (same public API, same Skia Vulkan path Stride uses there) but have not been run.
