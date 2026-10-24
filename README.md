<div align="center">
  <img src="milkdropsharp.png" width="140" alt="MilkDropSharp" />
  <h1>MilkDropSharp</h1>
  <p>.NET managed ports of two C++ music visualizers: <b>projectM 4.1.8</b> and <b>MilkDrop3</b> (Windows x64)</p>
  <p><sub>Vibe-coded with AI · for learning and tinkering only, not for production</sub></p>
  <p><b>English</b> · <a href="README.cn.md">简体中文</a></p>
</div>

---

> [!WARNING]
> **This project is a "vibe-coded", AI-generated artifact.**
>
> Almost all of the code was produced by a large language model from plain-language wishes
> ("port this thing over", "it doesn't run here", "add a fullscreen mode"). A human only wrote
> the requirements, ran the acceptance checks, and pointed at whatever was broken. Consequently:
>
> - The architecture and coding style are "it runs, therefore it's correct" — **not** designed to
>   industrial standards. A single module may contain several inconsistent ways of doing the same thing.
> - Apart from the unit tests and per-pixel A/B comparisons in this repo, there is **no long-term
>   production validation**; edge cases are very likely uncovered.
> - Comments, docs and commit messages are mostly AI-written too, and may be stale or disagree with
>   the code (the code wins).
> - It is fine as a runnable reference implementation / learning material / toy. **If you want to
>   ship it, do your own thorough review and hardening first.**
>
> Correctness is propped up by running 9795 real presets and comparing screenshots against native
> libprojectM — but coverage is not correctness.

## What this is

The repo contains **two independent ports** that share two foundation libraries (an expression
engine and an HLSL translator):

| Port | Upstream | Rendering | Entry point |
|---|---|---|---|
| **Classic** | MilkDrop3 (Milkdrop2 plugin semantics) | Direct3D 9 (Vortice) + HLSL assembly shaders | `src/MilkDrop.Classic.App` |
| **ProjectM** | libprojectM 4.1.8 + `frontend-sdl-cpp` | ANGLE → OpenGL ES 3.0 (Silk.NET) + HLSL→GLSL | `src/MilkDrop.ProjectM.App` |

Both run on **Windows x64 only**: Classic is tied to D3D9, ProjectM is tied to ANGLE's D3D11
backend, and both capture audio through WASAPI loopback.

The two shared libraries are direct ports of their C++ counterparts and are used by both ports:

- **`src/MilkDrop.Eval`** — port of `projectm-eval`. An ns-eel expression interpreter (`regNN`
  slots, megabuf, gmegabuf, the full built-in function set) with **no `Reflection.Emit`**, so it is
  Native AOT safe.
- **`src/HlslParser`** — port of `hlslparser`. Translates the HLSL snippets inside presets into
  GLSL (`Version330` / `Version300Es`); `tex2D` / `tex3D` / `register(...)` are all supported.

## Screenshots

<p align="center">
  <img src="docs/projectm-crystal-palace.gif" width="420" alt="projectM: crystal palace" />
  <img src="docs/projectm-neon-space.gif" width="420" alt="projectM: neon space" />
</p>

<p align="center">
  <img src="docs/projectm-reaction-diffusion.png" width="420" alt="projectM: reaction diffusion" />
  <img src="docs/classic.png" width="420" alt="MilkDrop3 Classic rendering" />
</p>

<p align="center">
  <img src="docs/shadertoy-seascape.png" width="420" alt="ShaderToy: Seascape" />
  <img src="docs/shadertoy-fractal-land.png" width="420" alt="ShaderToy: Fractal Land" />
</p>

Top row is the ProjectM port (ANGLE/GLES3) in motion: `Serge + martin - crystal palace008` and
`martin - neon space ps3`. Middle left is a still from `Geiss - Reaction Diffusion 2`, middle right
is the Classic port (D3D9) running `Flexi - crush ice 52s`. Bottom row is the ShaderToy player:
`Seascape (Ms2SD1)` and `Fractal Land (XsBXWt)`, whole-window shots including the local library panel.

## Download

If you'd rather not install the .NET SDK or build it yourself, there is a prebuilt
**Windows x64 package** (the three apps plus presets, textures and sample shaders — unzip and run):

- Baidu Netdisk: <https://pan.baidu.com/s/1QTe2AXRftQvoQH7nxpUiMA?pwd=gbj7>
- Extraction code: `gbj7`

Unzip, then launch whichever exe you need; the ProjectM build reads `presets/` and `textures/` from
its own folder by default.

## Status

| Component | Status | How it was verified |
|---|---|---|
| `MilkDrop.Eval` | Done | 98 tests (`tests/MilkDrop.Eval.Tests`) |
| `HlslParser` | Done | 65 tests + the hlsl/glsl golden cases under `tests/golden` |
| `MilkDrop.ProjectM` + App | Done (P0–P10) | all 9795 presets run (67 fail to load, all due to broken preset files); 126 tests; AOT vs JIT screenshots differ by 0.89/255 on average |
| `MilkDrop.Classic.*` | Done | 187 tests (`tests/MilkDrop.Classic.Tests`) |

"Done" here means **every item in the port plan was implemented and accepted** (see
[`projectMPlan.md`](projectMPlan.md)); it does not mean the code quality, edge-case coverage or
performance is production grade — see the warning above.

The ProjectM port was benchmarked frame-by-frame against native `libprojectM`; numbers are in
[`projectMMisc.md`](projectMMisc.md).

## Requirements

- Windows 10/11 **x64**
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`TargetFramework=net10.0` in
  `Directory.Build.props`; `HlslParser` alone targets `net9.0`)
- A GPU with **D3D11** (ANGLE backend) and **D3D9** (Classic) support
- **VS 2022 Build Tools with the "Desktop development with C++" workload** (i.e. `link.exe`) —
  only needed for Native AOT

## Quick start

### ProjectM (the GLES3 port)

```powershell
# Just run it (you must point at a preset directory, or drop a presets\ folder next to the exe)
dotnet run --project src\MilkDrop.ProjectM.App -c Release -- ^
  --preset-dir "res\projectMSDL-2.0.0-win64\presets\presets-cream-of-the-crop"
```

Preset lookup order:

1. `--preset <file>` — a single `.milk`
2. `--playlist <file>` — one path per line, `#` starts a comment
3. `--preset-dir <dir>` — **repeatable, scanned recursively** for `.milk`
4. If none of the above produced anything, fall back to the `presets\` folder **next to the exe**

Textures work the same way: `--texture-dir` is repeatable, otherwise `textures\` next to the exe.

Common options:

```powershell
--width N --height N        window size (default 1024x768)
--fullscreen               start fullscreen (toggle at runtime with F11)
--no-ui                    don't create the ImGui UI (pure picture, for benchmarking)
--novsync                  disable vsync
--shuffle                  play presets in random order
--duration SEC --blend SEC per-preset time (default 30) / soft-cut time (default 3)
--no-audio                 don't capture system audio (silent driving signal)
--synthetic-audio          use a synthetic test tone (with beats, for batch runs)
--preset-dir DIR           see above
--smoke N                  render N frames then exit
--shot FILE                save the last frame as PNG before exiting
--auto-next N              switch to the next preset every N frames
--fixed-dt S               fixed timestep (for reproducible screenshot comparisons)
--bench [--bench-frames N] batch-run every preset and emit a CSV report
```

Full list: `--help`.

### Classic (the D3D9 port)

```powershell
dotnet run --project src\MilkDrop.Classic.App -c Release -- --preset random
```

No resource configuration needed: the app walks up from the current directory looking for the repo
root that contains `res\MilkDrop3\plugins\Milkdrop2\data\include.fx`. If that fails, point at it
explicitly:

```powershell
$env:MILKDROP_CLASSIC_ROOT = "F:\path\to\MilkDropSharp"
```

Common options:

```powershell
--width N --height N       window size (default 1280x720)
--preset <name|path|random>
--preset-duration SEC      average auto-advance interval (default 16, plus 0..10 s of jitter)
--blend SEC                crossfade time (auto 2.7 / manual 1.7 by default; 0 = hard cut)
--lock-preset              lock the current preset
--audio loopback|none      audio source (WASAPI loopback by default)
--sprite <name> [--sprite-code <EEL>]   attach a sprite plus its init code
--shader-compat            enable the shader compatibility layer (macro shadowing / double /
                           non-finite literals / get_fft / ps_2_0 instruction slot upgrades)
--reference                use the D3D9 reference device (software rasterizer, debugging)
--smoke [seconds]          run for N seconds then exit (default 3)
--shot FILE --shot-delay S take a timed screenshot
--ui-scale F               UI scale (inferred from DPI by default)
```

### Hotkeys

| ProjectM | | Classic | |
|---|---|---|---|
| `Space` | pause / resume | `N` / `P` | next / previous preset |
| `N` / `→` | next preset | `Space` | random preset |
| `P` / `←` | previous preset | `L` | lock current preset |
| `R` | random preset | | |
| `L` | lock current preset | | |
| `S` | screenshot | | |
| `F` | show / hide UI | | |
| `F11` | fullscreen / windowed | | |
| `H` | help | | |
| `Esc` / `Q` | quit | | |

## Native AOT

Both apps can be ahead-of-time compiled into a single exe that doesn't need the .NET runtime
(the native libraries still have to ship alongside it).

### ProjectM

AOT here is **conditional**: a normal `dotnet build` is unaffected, and it only kicks in when you
pass `-p:PublishAot=true` explicitly:

```powershell
# Clear the output directory first — stale files from an earlier publish are the most common
# reason people think "the dlls are missing from the package"
Remove-Item -Recurse -Force artifacts\p10\aot -ErrorAction SilentlyContinue

dotnet publish src\MilkDrop.ProjectM.App -c Release -r win-x64 -p:PublishAot=true -o artifacts\p10\aot
Remove-Item artifacts\p10\aot\*.pdb
```

Output (about 11 MB):

```
MilkDrop.ProjectM.App.exe   4.5 MB   real AOT native code
av_libglesv2.dll            5.1 MB   ANGLE (GLES3 → D3D11)
cimgui.dll                  1.2 MB   Dear ImGui native bindings
glfw3.dll                   0.2 MB   windowing / input
```

Cold start is 0.46 s (0.71 s for the JIT build). **Known limitation**: NAudio's WASAPI does not
work under AOT (`MMDeviceEnumeratorComObject..ctor()` is compiled into a stub that always throws),
so audio silently degrades to a static signal while rendering continues normally. Use the JIT build
or `--synthetic-audio` if you need sound.

### Classic

`MilkDrop.Classic.App.csproj` already enables `PublishAot` and `RuntimeIdentifiers=win-x64`, so a
plain publish is enough:

```powershell
dotnet publish src\MilkDrop.Classic.App -c Release -r win-x64
```

## Tests

```powershell
dotnet test MilkDrop.ProjectM.sln      # ProjectM + Eval
dotnet test MilkDrop.Classic.sln       # Classic + Eval
dotnet test HlslParser.sln             # HLSL translator (including golden-file comparisons)
```

Some rendering tests create an **offscreen GL context (32×24)** and actually execute shaders, so
they need a GPU capable of GLES3; a GPU-less CI box won't do.

## Assets and corpus

`res/` exists only so the apps and regression tests have something to run on; it is not source code:

| Path | Contents |
|---|---|
| `res/projectMSDL-2.0.0-win64/` | Official projectM Windows release: **9795 `.milk` presets** (`presets/presets-cream-of-the-crop`) and 66 textures; its `projectMSDL.exe` / `projectM-4.dll` serve as the baseline for A/B comparisons |
| `res/MilkDrop3/plugins/Milkdrop2/` | MilkDrop3 plugin assets: 477 presets, 129 textures, 31 `.shape`, 19 `.wave`, 82 sprite images, `data/include.fx` |

## Performance at a glance

Frame times of the C# port (Release JIT, ANGLE/GLES3) versus native libprojectM 4 — mesh 32×24,
silent, no UI, vsync off, in ms/frame:

| Resolution | Native (unsynced) | C# (`--smoke`, real loop) |
|---|---|---|
| 640×480 | 1.55 | 3.73 (268 FPS) |
| 1280×720 | 1.48–1.54 | 3.65 (274 FPS) |
| 1920×1080 | 1.47–1.51 | 3.70 (270 FPS) |

What the measurements showed:

- Frame time is essentially independent of resolution: the **bottleneck is on the CPU side**
  (dozens of GL state changes plus per-vertex EEL evaluation every frame).
- A few presets are much slower because of their **custom shape instance count** (e.g. 3584 shape
  instances per frame → 17 ms per frame), not because of fill rate.
- At high resolutions (e.g. 2560×1600 fullscreen) it flips to a **fill-rate** bottleneck: the warp
  shader samples textures several times per pixel, landing around 66 FPS.

Full numbers, the measurement scripts and preset-loading comparisons are in
[`projectMMisc.md`](projectMMisc.md) (Chinese).

## Repository layout

```
src/
  MilkDrop.Eval/              projectm-eval port (EEL interpreter, megabuf)
  HlslParser/                 hlslparser port (HLSL → GLSL 330 / 300es)
  MilkDrop.Classic.Render.D3D9/   D3D9 backend (Vortice, HLSL assembly, ImGui DX9)
  MilkDrop.Classic.Visualizer/    MilkDrop3 preset semantics (Visualizer/PresetState/WarpMesh/Sprite…)
  MilkDrop.Classic.App/           command line + window + UI
  MilkDrop.ProjectM/          libprojectM port (Renderer/MilkdropPreset/Audio…)
  MilkDrop.ProjectM.App/          command line + window + UI
tests/
  MilkDrop.Eval.Tests/  HlslParser.Tests/  MilkDrop.Classic.Tests/  MilkDrop.ProjectM.Tests/
  golden/                     HLSL→GLSL input/expected-output pairs
res/                          presets, textures, upstream releases (see above)
MilkDrop.ProjectM.sln  MilkDrop.Classic.sln  HlslParser.sln
```

## Documentation

| File | Contents |
|---|---|
| [`projectMPlan.md`](projectMPlan.md) | ProjectM port plan: goals, the C++→C# mapping tables, the P0–P10 breakdown and acceptance criteria |
| [`projectMIssue.md`](projectMIssue.md) | Pitfall log (ANGLE x64 / EGL entry points, MRT, shader translation, AOT…), numbered per phase |
| [`projectMMisc.md`](projectMMisc.md) | Measured performance, semantics notes, measurement scripts, TODOs |

> These docs are written in Chinese.

## Known limitations

- **Windows x64 only.** Classic is hard-wired to D3D9; ProjectM is hard-wired to ANGLE's D3D11
  backend and WASAPI loopback.
- The ProjectM port **does not include** projectM's C API layer, `ProjectMCWrapper`, or the SDL
  frontend; in-preset user sprites and the song-title animation were not ported.
- **ANGLE comes from Avalonia's build of `av_libglesv2.dll`** (genuinely x64, with `EGL_`-prefixed
  EGL entry points). The ANGLE native package shipped by Silk.NET is an x86 binary in
  `runtimes/win-x64` and throws `BadImageFormatException` inside an x64 process.
- **The AOT build of ProjectM has no audio** (NAudio's WASAPI COM class has no valid IL under ILC);
  it degrades to silence automatically.
- With `--fullscreen` the display's current mode wins; `--width` / `--height` are ignored.
- 67 of the 9795 presets (0.68%) fail to load; all of them are broken preset files (syntax or
  register errors), not porting issues.

## License

This repository is released under **GPL-3.0**, see [`LICENSE`](LICENSE).

Ported from [projectM](https://github.com/projectM-visualizer/projectm) (4.1.8) and
[MilkDrop3](https://github.com/MilkDrop2077/MilkDrop3); the upstream projects' own licenses and
copyright notices apply as well — comply with them when redistributing.
