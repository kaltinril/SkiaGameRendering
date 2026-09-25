# SkiaGameRendering - Platform Support TODO

This document tracks which framework/platform/backend combinations have been proven to work with SkiaGameRendering.

## Platform Matrix

| Framework       | Version | Backend   | Platform | Sample Project              | Status      | Notes |
|-----------------|---------|-----------|----------|-----------------------------|-------------|-------|
| MonoGame        | 3.8.4   | DesktopGL | Desktop  | samples/Sample.MonoGame.DesktopGL/  | Working     | See `docs/desktop/quickstart.md`. Cross-platform (Windows, Linux, macOS). |
| MonoGame        | 3.8.4   | WindowsDX | Desktop  | samples/Sample.MonoGame.WindowsDX/  | Working     | See `docs/desktop/quickstart.md`. Windows only. |
| MonoGame        | 3.8.5   | WindowsDX12 | Desktop | —                           | Blocked | MG 3.8.5's new native `WindowsDX12` platform (`MonoGame.Framework.Native`, D3D12) hides its device behind an opaque native handle — no `ID3D12Device` reachable via reflection. The legacy `WindowsDX` (D3D11) project above is a separate, unaffected target. See `SkiaGameRendering-Notes.md` section 9. |
| MonoGame        | 3.8.5   | DesktopVK | Desktop  | —                           | Blocked | Same native-backend blocker as 3.8.5 WindowsDX12 (see the row above): `api_MGG.h` exposes no getter for the `VkDevice`/`VkQueue` the native library holds internally. See `SkiaGameRendering-Notes.md` section 9 and issue #67. |
| KNI             | 4.3.9001 (stock) | DesktopGL | Desktop  | samples/Sample.Kni.DesktopGL/ | Working | See `docs/desktop/quickstart.md`. Cross-platform (Windows, Linux, macOS). |
| KNI             | 4.3.9001 (stock) | WindowsDX | Desktop  | samples/Sample.Kni.WindowsDX/ | Working | See `docs/desktop/quickstart.md`. Windows only. |
| KNI             | —       | —         | Android  | —                           | Not started | |
| KNI             | 4.3.9001 (stock) | WebGL2 | Web | samples/Sample.Kni.WebGL/ | Working (Chrome, Edge); Firefox blocked | See `docs/webgl/quickstart.md`. Chrome/Edge measured Tier 1 on physical hardware; Firefox misses upload budget by 60-300x and needs the Option A (shared-context) fallback — see `docs/webgl/validated-baseline.md` and `docs/webgl/performance-results.md`. Safari (Tier 2) untested, no Mac available. |
| raylib          | 8.0.0 (Raylib-cs) | rlgl (OGL) | Desktop | samples/Sample.Raylib.OGL/ | Working (Windows + Linux) | See `docs/raylib/quickstart.md`. macOS not implemented. |
| Godot           | 4.7.2 (GodotSharp) | Vulkan (RenderingDevice) | Desktop | samples/Sample.Godot/ | Working (Windows verified, incl. clean `--gpu-validation`; Linux/macOS expected, unrun) | See `docs/godot/quickstart.md`. No reflection: `RenderingDevice.GetDriverResource` is public. Zero-copy. |
| Godot           | 4.7.2 (GodotSharp) | D3D12 (RenderingDevice) | Desktop (Windows) | samples/Sample.Godot/ | Working (Windows verified) | Same package, chosen at runtime. Not zero-copy: Godot allocates typeless D3D12 textures and Skia's D3D12 backend cannot render into those, so Skia draws into a typed resource and one GPU `CopyResource` per frame lands it in Godot's texture. See `SkiaGameRendering-Notes.md` section 11. |
| Godot           | 4.7.2   | Metal / Compatibility (GL) | Desktop | —                | Not started | Metal: Godot exposes `MTLDevice`/`MTLCommandQueue`, but this repo has no Core.Metal. Compatibility: no `RenderingDevice`; `DisplayServer.WindowGetNativeHandle(OpenglContext)` exposes the HGLRC/GLX/EGL context and `RenderingServer.TextureGetNativeHandle` the GL texture id, so a shared-context `Core.OGL` adapter like raylib's is plausible but unexplored. |

## Architecture

The library uses a backend abstraction (`SkiaBackend` base class) so each graphics API gets its
own implementation, documented per platform in `docs/`:

- [`docs/desktop/quickstart.md`](docs/desktop/quickstart.md) — MonoGame/KNI DesktopGL and
  WindowsDX (`SkiaGlBackend`, `SkiaKniGlBackend`, `SkiaAngleBackend`, `SkiaKniAngleBackend`),
  project layout, and known limitations.
- [`docs/webgl/quickstart.md`](docs/webgl/quickstart.md) — KNI WebGL/Blazor (`SkiaWebGlBackend`).
- [`docs/raylib/quickstart.md`](docs/raylib/quickstart.md) — raylib (`SkiaRaylibRenderTarget2D`),
  the first non-MonoGame engine and the first consumer of `Core.OGL` from outside the MonoGame
  family (tracked in [issue #3](https://github.com/vchelaru/SkiaGameRendering/issues/3); it does
  *not* derive from `SkiaBackend`/`SkiaTarget`, deliberately, since those are typed to MonoGame's
  `Texture2D`).

This was de-risked first as a throwaway spike (`spikes/raylib-ogl-v0/`, since removed — its
finding is preserved in issue #3's spike comment and carried into `Wgl.cs`'s doc comment).

## Known Issues / Cleanup

- **SetData workaround for lazy texture creation**: MonoGame WindowsDX's ANGLE backend forces D3D11
  resource allocation with a wasteful `SetData(new byte[w*h*4])` call (see `SkiaAngleBackend.md`).
  Need a cheaper trigger.
- **glFinish performance**: the ANGLE backends call `glFinish()` per renderable for GPU sync.
  Could potentially be relaxed to `glFlush()` if D3D11's internal synchronization is sufficient —
  unverified.

## Open Questions

- **KNI versions**: Which KNI package versions to target?

## Next Steps

1. ~~Run and archive the hardware benchmark matrix in `benchmarks/Benchmarks.WebGL/`~~ — done, closes [issue #5](https://github.com/vchelaru/SkiaGameRendering/issues/5). Follow-up: Firefox needs the Option A (shared-context) fallback to reach Tier 1 (see `docs/webgl/validated-baseline.md`); not yet tracked in its own issue.
2. Address the lazy-allocation issue above (ANGLE DLL packaging landed via issue #36).
3. Split per-graphics-API core libraries out of the per-engine adapters so a new engine (raylib) can reuse the GL/Skia interop instead of duplicating it — tracked in [issue #3](https://github.com/vchelaru/SkiaGameRendering/issues/3). The OGL split (`src/SkiaGameRendering.Core.OGL/`) landed via [#4](https://github.com/vchelaru/SkiaGameRendering/pull/4), and `src/SkiaGameRendering.Raylib.OGL/` now proves it against a real second (non-MonoGame) engine on both Windows and Linux (`Glx.cs`, verified under WSLg — tracked in [issue #9](https://github.com/vchelaru/SkiaGameRendering/issues/9)). Issue #3 is not fully closed yet: macOS raylib support is unimplemented, and step 3 (`SkiaAngleBackend` → `Core.ANGLE`, now also reused by `src/SkiaGameRendering.Kni.WindowsDX/`) has landed, leaving step 4 (`SkiaWebGlBackend` → `Core.WebGL`) as the only remaining unmigrated backend.
4. `src/SkiaGameRendering.Core.VK/` landed via [issue #23](https://github.com/vchelaru/SkiaGameRendering/issues/23) — engine-agnostic Vulkan/Skia interop, no host engine wired up yet. The Stride adapter (reflection into `Stride.Graphics.GraphicsDevice`/`Texture`'s Vulkan internals, plus queue-lock discipline) is the next step, tracked in [issue #54](https://github.com/vchelaru/SkiaGameRendering/issues/54).
6. `src/SkiaGameRendering.Godot/` landed — the first adapter needing no reflection at all (Godot exposes its device, queue and any RD texture's native handle through `RenderingDevice.GetDriverResource`), and the first with two graphics APIs behind one package, because Godot picks Vulkan or D3D12 at run time. It added `VkImageLayoutTransitioner` to `Core.VK` and `D3D12ResourceTransitioner` to `Core.D3D12` because Godot tracks every texture's layout/state itself and SkiaSharp cannot be asked which to leave a resource in (see `SkiaGameRendering-Notes.md` section 11). Follow-ups: publishing the NuGet package, Linux/macOS runs, the Compatibility (GL) renderer, and Metal once a Core.Metal exists. `tests/Tests.Godot` drives the real engine against the sample on both drivers when `GODOT_BIN` is set and skips otherwise; CI does not fetch Godot.
5. `src/SkiaGameRendering.Core.D3D12/` landed — engine-agnostic D3D12/Skia interop (Skia's Ganesh D3D12 backend, per issue #24's now-accepted deprecation risk), same "no host engine wired up yet" shape `Core.VK` started with. The MonoGame `WindowsDX12` glue project is next, but is blocked on **both** `MonoGame/MonoGame#9536` ("Exposing Native GPU Handles", device+queue access) and `MonoGame/MonoGame#9535` ("Wrapping External Texture in RenderTarget2D", handing a Skia-drawn resource back to MonoGame) merging with a locked API shape — see `SkiaGameRendering-Notes.md` section 9 and issue #67 for why MG 3.8.5's native backend has no reachable device today. `samples/Sample.MonoGame.WindowsDX12/` is a proof-of-concept against both PRs' proposed APIs, for the PR authors to test against their own branches before merge.

[Issue #2](https://github.com/vchelaru/SkiaGameRendering/issues/2) (closed) tracked the KNI upstream dependency: kniEngine/kni#2669 shipped in KNI v4.3.9001, so the KNI source patch/fork is gone (moved to stock NuGet). The one piece #2669 didn't cover — a public accessor for the current WebGL rendering context — is bridged with reflection (`WebGlCanvasUpload.cs`); pitching KNI a follow-up PR for it is tracked in [issue #13](https://github.com/vchelaru/SkiaGameRendering/issues/13). [Issue #14](https://github.com/vchelaru/SkiaGameRendering/issues/14) (closed) tracked an apparent 4.3.9001 startup crash that turned out not to be a KNI bug: `nkast.Wasm.*`'s dependency version bumped from 8.0.11 (under KNI 4.2.9001) to 10.0.3 (under 4.3.9001), and `wwwroot/index.html`'s hardcoded `<script>` version strings hadn't been updated to match — a silent 404, not a build failure. `eng/Versions.props`'s `NkastWasmCanvasVersion` must always match whatever the pinned `KniVersion` actually depends on.
