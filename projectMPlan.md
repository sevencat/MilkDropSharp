# projectM 4.1.8 → C# 移植计划

> 需求见 `projectMPort需求.md`；踩坑记 `projectMIssue.md`；其他杂记 `projectMMisc.md`。
> 原码：`refcode/projectM/projectm-4.1.8`（libprojectM + playlist + sdl-test-ui）、`refcode/projectM/frontend-sdl-cpp-2.0.0-pre1`。

---

## 1. 目标与硬约束

| 项 | 决定 |
|---|---|
| 核心库 | `src/MilkDrop.ProjectM`（对应 `libprojectM`，命名空间 `MilkDrop.ProjectM`） |
| 宿主 | `src/MilkDrop.ProjectM.App`（对应 `frontend-sdl-cpp`/`sdl-test-ui`） |
| 测试 | `tests/MilkDrop.ProjectM.Tests`（xunit） |
| 解决方案 | 仓库根 `MilkDrop.ProjectM.sln`（含 `src`/`tests` 解决方案文件夹，并纳入 `MilkDrop.Eval`、`HlslParser`） |
| 窗口/UI | Silk.NET.Windowing（`GraphicsAPI.None`）+ ImGui.NET |
| 渲染 | ANGLE → OpenGL ES 3.0（`#version 300 es`），Silk.NET.OpenGLES 绑定 |
| 表达式 | 复用 `MilkDrop.Eval`（projectm-eval 已移植，`ProjectMEvalEngine`） |
| HLSL→GLSL | 复用 `HlslParser`（`GlslVersion.Version300Es`） |
| 框架 | `net10.0`（沿用 `Directory.Build.props`） |

**不做**：projectM 的 C API 层（`api/include/projectM-4/*`、`ProjectMCWrapper`、`PlaylistCWrapper`）、Touch 相关空实现、SDL 前端。

---

## 2. 现状分析

### 2.1 已具备（可直接依赖）

| 资产 | 位置 | 说明 |
|---|---|---|
| projectm-eval 移植 | `src/MilkDrop.Eval` | `IExpressionVm.Compile() → ICompiledExpression.Execute()`；`regNN` 槽、megabuf、全套 ns-eel 内建函数已在 `IntrinsicTable.cs` |
| hlslparser 移植 | `src/HlslParser` | 入口 `HlslToGlslTranslator.Translate(TranslationRequest)`，支持 `Version330` / `Version300Es`，`tex2D/tex3D/register(...)` 均可 |
| 音频采集 | `src/MilkDrop.Classic.App/WasapiLoopbackAudioSource.cs` | NAudio WASAPI loopback → float 环，可改成喂 `PCM` |
| ImGui 输入映射 | `src/MilkDrop.Classic.App/ImguiInput.cs` | 与渲染后端无关，可整文件复用 |
| 图片解码 | `StbImageSharp`（已被 Classic 使用） | 替代 SOIL2 |
| 语料 | `res/projectMSDL-2.0.0-win64/presets`（9795 个 `.milk`）+ `textures`（66 张） | 回归测试与人工比对基准 |
| 参考实现 | `res/projectMSDL-2.0.0-win64/projectMSDL.exe`（原生 projectM 4 + SDL） | A/B 截图对比的"标尺" |

### 2.2 不能复用（只作参考）

- `MilkDrop.Classic.Visualizer` 全部：`PresetState.cs`(54KB)/`ShaderTextGenerator.cs`/`Visualizer.cs`(99KB) 是 MilkDrop2/3 的插件语义（固定管线 D3D9 + `.milk` 混合），与 projectM 的 `MilkdropPreset` 管线互斥。
- `MilkDrop.Classic.Render.D3D9`：`ShaderAssembler.cs`(36KB)、`ShaderPrelude.cs`、`IShaderProgram` 的 `register(cN/sN)` 约定都是 D3D9 专有，GL 侧改成"从 GLSL 源码反射 uniform 名"。
- `AudioRingBuffer`（绑 `audiobuf.cpp` 的 sbyte 语义）→ projectM 走 `PCM` 的 float 环形缓冲，重写。

### 2.3 projectM 一帧真实顺序（照抄 `MilkdropPreset.cpp:78-170`）

1. 写音频/上下文，按需 resize FBO，`mainTexture` 指向上一帧
2. `PerFrameContext` 求值（`LoadStateVariables` → `ExecutePerFrameCode` → `LoadPerFrameQVariables`，clamp `gamma/echo_zoom`）
3. **motion vectors**（在 warp 之前）
4. flip 上一帧（`CopyTexture`）
5. 绑 current FBO + 把 `m_motionVectorUVMap` 挂 color attachment 1（MRT）
6. **warp shader + PerPixelMesh**
7. 摘掉 UV attachment
8. **blur chain**（`BLUR1/2/3`，最多 6 pass）
9. custom shapes → custom waveforms → 主 waveform
10. darken center
11. border
12. 再 flip，绑 current=read / previous=draw
13. **final composite**（有 comp shader 走 shader；否则 `VideoEcho` + `Filters` 老式效果）
14. 无 comp shader 时再 flip，交换 FBO 索引

> 注意：`video echo` 不是独立 pass，在 `FinalComposite` 内部。

---

## 3. 代码映射表

### 3.1 核心数据/纯逻辑（P1）

| C++ | C# | 备注 |
|---|---|---|
| `Audio/AudioConstants.hpp` | `Audio/AudioConstants.cs` | 576/480/512 |
| `Audio/FrameAudioData.hpp` | `Audio/FrameAudioData.cs` | bass/mid/treb/vol(+Att)、waveformLeft/Right、spectrumLeft/Right |
| `Audio/PCM.cpp` | `Audio/Pcm.cs` | float 环形缓冲 + `UpdateFrameAudioData` |
| `Audio/MilkdropFFT.cpp` | `Audio/MilkdropFft.cs` | Nullsoft FFT + 均衡/正弦包络表 |
| `Audio/Loudness.cpp` | `Audio/Loudness.cs` | 三频段 + FPS 阻尼 |
| `Audio/WaveformAligner.cpp` | `Audio/WaveformAligner.cs` | Mip/倍频程加权最小误差 |
| `TimeKeeper.cpp` | `TimeKeeper.cs` | 软切/硬切/easter-egg 随机时长 |
| `Utils.cpp` | `Utils.cs` | `ToLower`/路径工具 |
| `Renderer/RenderContext.hpp` | `RenderContext.cs` | 每帧只读参数 |
| `MilkdropPreset/PresetFileParser.cpp` | `MilkdropPreset/PresetFileParser.cs` ✅ | `.milk` 键值解析 |
| `MilkdropPreset/PresetState.cpp` | `MilkdropPreset/PresetState.cs` **→ P3** | 共享状态；除了 GL 对象还要 `TextureManager`/`MilkdropNoise`，与 shader 装配一起做 |
| `MilkdropPreset/Constants.hpp` | `MilkdropPreset/Constants.cs` | |
| `Preset.hpp` / `PresetFactory*` / `Factory.cpp` | `Preset.cs` / `PresetFactory.cs` / `PresetFactoryManager.cs` **→ P3** | 抽象基类以"渲染一帧"为核心，同样依赖 GL 层 |

### 3.2 GL 封装（P2）

| C++ | C# |
|---|---|
| `Renderer/Texture.cpp` | `Renderer/Texture.cs` |
| `Renderer/TextureAttachment.cpp` | `Renderer/TextureAttachment.cs` |
| `Renderer/Framebuffer.cpp`（MRT/`glDrawBuffers`/`MaskDrawBuffer`） | `Renderer/Framebuffer.cs` |
| `Renderer/Shader.cpp` | `Renderer/Shader.cs`（uniform 从源码反射） |
| `Renderer/Sampler.cpp` | `Renderer/Sampler.cs` |
| `Renderer/RenderItem.cpp`（VAO/VBO 基类） | `Renderer/RenderItem.cs` |
| `Renderer/CopyTexture.cpp` | `Renderer/CopyTexture.cs` |
| `Renderer/FileScanner.cpp`、`TextureSamplerDescriptor.cpp` | 同名 `.cs` |

> P2 备注：`GlContext`（`Rendering/GlContext.cs`）静态持有 `GL` 入口，对应 C++ 的全局 `gl*` 函数；
> 封装类的名字（`Framebuffer`/`Shader`/`Texture`/`Sampler`）与 `Silk.NET.OpenGLES` 的句柄类型**同名**，
> 外部使用时要加 using 别名（见 `projectMIssue.md` I-16）。

### 3.3 预设渲染（P3–P5）

| C++ | C# |
|---|---|
| `MilkdropPreset/MilkdropPreset.cpp` | `MilkdropPreset/MilkdropPreset.cs` |
| `MilkdropPreset/MilkdropShader.cpp`(27.7KB) | `MilkdropPreset/MilkdropShader.cs`（含 `PreprocessPresetShader` / `GetReferencedSamplers` / `TranspileHLSLShader`） |
| `MilkdropPreset/MilkdropStaticShaders.hpp.in` | `MilkdropPreset/MilkdropStaticShaders.cs` + 12 个 `Shaders/*.glsl` 嵌入资源 |
| `MilkdropPreset/PerFrameContext.cpp` | `PerFrameContext.cs` |
| `MilkdropPreset/PerPixelContext.cpp` + `PerPixelMesh.cpp` | `PerPixelContext.cs` + `PerPixelMesh.cs` |
| `MilkdropPreset/PerPixelMesh` 的 `WarpGrid` | `WarpGrid.cs` |
| `BlurTexture.cpp` | `BlurTexture.cs` |
| `FinalComposite.cpp` + `VideoEcho.cpp` + `Filters.cpp` | 同名 `.cs` |
| `MotionVectors.cpp`、`DarkenCenter.cpp`、`Border.cpp` | 同名 `.cs` |
| `CustomWaveform.cpp`、`Waveform.cpp`、`CustomShape.cpp` | 同名 `.cs` |
| `ShapePerFrameContext` / `WaveformPerFrameContext` / `WaveformPerPointContext` | 同名 `.cs` |
| `Waveforms/*`（12 种 + `LineBase` + `WaveformMath` + `Factory`） | `Waveforms/` 同名 `.cs` |
| `IdlePreset.cpp` | `IdlePreset.cs` |
| `MilkdropPresetExceptions.hpp` | 复用现有异常风格（`CompileException` 等） |

### 3.4 纹理/过渡/调度（P6–P7）

| C++ | C# |
|---|---|
| `Renderer/TextureManager.cpp` | `Renderer/TextureManager.cs` |
| `Renderer/MilkdropNoise.cpp`（GL_TEXTURE_3D） | `Renderer/MilkdropNoise.cs` |
| `Renderer/PresetTransition.cpp` + `TransitionShaderManager.cpp` + `TransitionShaders/*` | 同名 `.cs` + 6 个 `.frag` 资源 |
| `ProjectM.cpp` | `ProjectMEngine.cs`（**改名**：避免 `MilkDrop.ProjectM.ProjectM` 与命名空间同名） |
| `playlist/{Playlist,Item,Filter}.cpp` | `Playlist/` 同名 `.cs` |

---

## 4. 关键技术决策

**D1 GLES 3.0 而非桌面 GL。** 上游已有完整 `USE_GLES` 分支（`TransitionShaderManager.cpp:33-38`、`CopyTexture.cpp:9-13`、`MilkdropNoise.cpp:98-107`、`Framebuffer.cpp:316-322`），且 `ProjectM.cpp:173-179` 已为 Chrome/ANGLE+WebGL2 打过 `glDrawBuffers(GL_BACK)` 补丁——**移植时一律走 GLES 分支**，版本头统一 `#version 300 es` + `precision mediump float/sampler3D`。

**D2 EGL 自绑。** `Silk.NET.OpenGLES` 2.23.0 只含 `GL` 类，**不含 EGL 绑定**（已核对包内 XML）。因此自写 `Egl/EGLNative.cs`（约 12 个 P/Invoke：`eglGetProcAddress`/`eglGetPlatformDisplayEXT`/`eglInitialize`/`eglChooseConfig`/`eglCreateContext`/`eglCreateWindowSurface`/`eglMakeCurrent`/`eglSwapBuffers`/`eglSwapInterval`/`eglQueryString`/`eglDestroy*`），并用它实现 `IGLBindingsContext.GetProcAddress`，再 `GL.GetApi(bindings)`。

**D3 窗口仍由 Silk.NET.Windowing 建（`GraphicsAPI.None`）**，取 `HWND` 交给 EGL 建 window surface；`Present` 由 `eglSwapBuffers` 负责。与 `MilkDrop.Classic.App:96-151` 的宿主结构保持一致。

**D4 ANGLE 原生库用 `Avalonia.Angle.Windows.Natives`**：包内是**单 DLL** `av_libglesv2.dll`（EGL + GLES 合一，真 x64），EGL 入口带 `EGL_` 前缀。
  ~~`Silk.NET.OpenGLES.ANGLE.Native`~~ 不可用——它的 `runtimes/win-x64` 里是 x86 二进制（见 `projectMIssue.md` I-1）。
  csproj 用 `GeneratePathProperty` + `Content Include` 显式复制到输出目录（该包的 targets 只放在 `build/net461/`，对 net10.0 不生效，见 I-5）。

**D5 分区：3 工程，GL 封装留在核心库内**（不做 Classic 那种 `Render.D3D9` 独立工程）。理由：projectM 的 `PresetState`/`Framebuffer`/`TextureManager` 与 GL 对象强耦合，强行抽象会与 C++ 逐行对照失真。核心库引用 `Silk.NET.OpenGLES`；`GL` 实例通过 `Rendering/GlContext.Current` 静态持有（宿主启动时初始化），这样纯逻辑类（PresetFileParser/Audio/TimeKeeper/Playlist）可脱离 GL 单测。

**D6 着色器资源内嵌**：12 个静态 GLSL + 6 个过渡 GLSL 以 `EmbeddedResource` 打包，运行时按 `USE_GLES` 规则重写版本头（对应 `MilkdropStaticShaders.hpp.in:26-33`）。

**D7 HLSL→GLSL 走已有翻译器**：`MilkdropShader.TranspileHLSLShader` 内做 C++ 那套"手术"——预处理器、剔除预设自带的 `sampler_*`/`texsize_*` 声明、注入 sampler/texsize 声明、`Translate(Version300Es, EntryPoint="PS", Flags=AlternateNanPropagation)`。`HlslToGlslTranslator` 已声明"预设专属手术故意不做"，与本分工吻合。

**D8 ImGui 后端自写**：与 `ImguiDx9Renderer` 同思路，写 `ImguiGles3Renderer`（VAO+VBO+UBO 可选、font atlas 上传 `GL_RGBA`、`glScissor`、一个 300es 程序），约 300 行。比引入 native `imgui_impl_opengl3` 更契合现有工程（当前只有 `ImGui.NET 1.90.9.1`）。

**D9 表达式求值桥接**：projectM 的 per-frame/per-pixel/per-point 三个上下文各自持 VM；`MilkDrop.Eval` 的 `Execute()` 返回 `double`，写入 `float` 状态时要显式收窄，保持一致（避免 `float`/`double` 混用导致与原生画面偏差）。

---

## 5. 里程碑

| 阶段 | 内容 | 验收标准 | 预估 |
|---|---|---|---|
| **P0** 环境打通 ✅ | sln + 3 工程；ANGLE DLL 落位；`Egl`/`EglContext`/`GpuCapabilities`；窗口清屏；`ImguiGles3Renderer` | **已完成**：`--smoke 120` 退出码 0，~120 FPS，ImGui 2 个指令列表/1972 顶点渲染无 GL 错误；探测到 GLES 3.0 + MRT(RGBA8+RG16F) 可用。见 `projectMIssue.md` I-9 | 1–2 天 |
| **P1** 纯数据层 ✅ | Audio 全套（Pcm/MilkdropFft/Loudness/WaveformAligner/FrameAudioData）、TimeKeeper、Utils、RenderContext、PresetFileParser | **已完成**：52 个单测全绿；FFT 与 double 精度朴素 DFT 交叉验证；语料测试 9795 个预设全部解析、0 失败。`PresetState`/`PresetFactory` 因持有 GL 对象推迟到 P2 | 3–4 天 |
| **P2** GL 封装层 ✅ | Texture/TextureAttachment/Framebuffer(MRT)/Shader/Sampler/RenderItem/CopyTexture/FileScanner/TextureSamplerDescriptor + `GlContext` 静态入口 | **已完成**：离屏 pbuffer 上下文里 8 个像素回读测试全绿（RGBA8 清屏回读、RGBA8+RG16F MRT 双附件、CopyTexture 垂直翻转、MaskDrawBuffer、shader 编译错误、FileScanner） | 3–5 天 |
| **P3** 着色器装配 ✅ | `MilkdropShader`（预处理/采样器注入/转译）+ `GetReferencedSamplers` + blur 级别判定 + `MilkdropStaticShaders`（预设头 + 两个预设顶点着色器） | **已完成**：语料 15895 个 warp/comp 着色器，15759 编译通过，成功率 **99.14%**（目标 ≥95%），失败归类见 `projectMIssue.md` I-23；`LoadVariables`/从 `PresetState` 取纹理留到 P4 | 3–4 天 |
| **P4** 主渲染管线 ✅ | `PresetState`、`PerFrameContext`、`BlurTexture`、`TextureManager`（含 `MilkdropNoise`/idle 纹理）、`PerPixelContext`、`PerPixelMesh`、预设 warp shader、`MotionVectors`、`DarkenCenter`、`Border`、`FinalComposite`（含 `VideoEcho` + `Filters` 回退）、`CopyTexture` 两次 Y 翻转、`MilkdropPreset` 完整生命周期（构造/`Initialize`/`RenderFrame`/`OutputTexture`/`DrawInitialImage`/`PerFrameUpdate`） | **已完成**：离屏 32×24 里 5 个管线测试全绿（comp shader 输出定值颜色、无 comp 走 VideoEcho、motion vectors、尺寸变化重建、GL error 0）。与原生 A/B 截图待 P9 批量比对 | 5–7 天 |
| **P5** 波形与形状 ✅ | 16 种波形（12 种原生 + 4 个 Milkdrop2077 追加）+ `WaveformMath`（IIR 平滑/雅可比平滑/边缘裁剪）、`CustomWaveform`、`CustomShape`（贴图/渐变两套 VAO + 描边）及三个 per-* 上下文 | **已完成**：16 种模式顶点测试 + 自定义形状/波形出图测试全绿；`IdlePreset` 里的 shape/wave 代码可正常求值 | 4–5 天 |
| **P6** 音频 + 切换 ✅ | Audio 全套（P1 已完成）、`TimeKeeper`、`PresetTransition` + `TransitionShaderManager`（6 个内置过渡，GLES 精度头）、`IdlePreset` | **已完成**：6 个转场着色器全部编译通过；转场 quad 绘制 + 待机预设连续两帧渲染 GL error 0。WASAPI 采集与 30 分钟长跑属 P8/P9 | 3–4 天 |
| **P7** 纹理管理 ✅ | `TextureManager`（扫描/`randNN_`/wrap-filter 前缀/age 淘汰）、`MilkdropNoise`（3D 纹理）、`TextureSamplerDescriptor.TryUpdate` + 弱引用语义 | **已完成**：描述符失效后可按名字重取（占位纹理回退与原生一致），测试覆盖三条分支 | 2–3 天 |
| **P8** 应用层 ✅ | `ProjectMEngine`（对应 `ProjectM.cpp`：工厂/纹理/转场/音频/参数/事件）、`Preset`+`PresetFactory`+`PresetFactoryManager`、`MilkdropPresetFactory`、`Playlist`/`Item`/`Filter`；App 层：`AppOptions`（30+ 参数）、快捷键（Space/N/P/R/L/S/F/H/Esc）、预设浏览/搜索 UI、参数面板、FPS 统计、PNG 截图、文本 playlist、WASAPI 环回采集（NAudio，无设备时静音降级） | **已完成**：`--smoke` 待机预设 120 帧 120 FPS、GL error 0；UI 构建 + 截图验证；Engine 7 项测试 + Playlist/Filter 20 项测试全绿 | 3–5 天 |
| **P9** 验证打磨 | 9795 预设 × N 帧批量跑；GL error/崩溃/编译失败清单；与原生 A/B 截图 | 崩溃率 0、GL error 0；失败清单收敛并归档 | 3–5 天 |

合计约 **30–44 人日**。

---

## 6. 风险清单

| # | 风险 | 应对 |
|---|---|---|
| R1 | `Silk.NET.OpenGLES` 无 EGL 绑定 | 自写 `EGLNative`（P0 打通，已核实包内容） |
| R2 | ANGLE 原生库来源/版本不确定 | ✅ 已解决：固定 `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419（真 x64 单 DLL）；踩坑见 `projectMIssue.md` I-1/I-2 |
| R3 | warp pass 用 `RG16F` 双附件（UV map）写颜色，GLES3 需 `EXT_color_buffer_float` | ✅ 已实测通过（`GpuCapabilities` 真建 FBO 验证）：RG16F 单附件与 RGBA8+RG16F 双附件均可渲染，走原生 GLES 路径 |
| R4 | `glColorMaski` / `MaskDrawBuffer` 在 GLES 只能全屏蔽 | 照抄 `Framebuffer.cpp:316-322` 的 `USE_GLES` 分支 |
| R5 | 300es 精度限定符缺失导致编译失败 | 全静态 shader 与过渡 shader 统一注入 `precision mediump float;`（照 `TransitionShaderManager.cpp:35`） |
| R6 | hlslparser 在 ES 目标下对部分预设 HLSL 语法支持不足 | P3 批量语料统计，失败清单驱动 HlslParser 补丁；不改 `MilkDrop.ProjectM` 规避 |
| R7 | `MilkDrop.Eval` 的 `double` vs 原生 `float` 语义偏差 | 关键变量显式 `(float)` 收窄；对 Q 变量/`bass_att` 等做数值对比测试 |
| R8 | ImGui 后端自写工作量/IME 缺失 | P0 完成最小版；编辑框类 UI 后置，若必须则补 IME |
| R9 | 默认帧缓冲状态污染（FBO 状态泄漏到 FBO 0） | 照抄 `ProjectM.cpp:173-179` 的 `glDrawBuffers(GL_BACK)` 补丁 |
| R10 | 9795 预设 shader 编译耗时导致卡顿 | 异步编译 + 编译结果内存缓存（后续可选磁盘缓存） |
| R11 | 项目内已有 `MilkDrop.Classic` 与 `MilkDrop.Eval` 的 sln 分裂 | 新 sln 同时挂 `MilkDrop.Eval`、`HlslParser`，避免工程引用跨 sln |

---

## 7. 验证手段

1. **语料驱动**：`res/projectMSDL-2.0.0-win64/presets`（9795 个）+ `textures`（66 张）作为唯一输入源，任何阶段都跑全量而不是挑几个。
2. **数值 golden**：Audio 链路（FFT/Loudness/Aligner）用固定 PCM 输入与 C++ 输出对比；gl 层用像素回读对比。
3. **A/B 截图**：同预设、同帧号、同网格尺寸下与 `projectMSDL.exe` 截图比对（参考 `HlslParser.Tests` 的 golden 组织方式）。
4. **无人值守 smoke**：`--smoke N` 随机抽预设跑固定帧数，退出码反映是否有 GL error/崩溃/编译失败。

### 一键复跑（P8/P9）

```bat
:: 单元测试（194 项，含离屏 GL 管线）
dotnet test tests/MilkDrop.ProjectM.Tests

:: 界面 + 字体覆盖检查（缺字返回 1）
dotnet run --project src/MilkDrop.ProjectM.App -- --font-check

:: 无人值守渲染 + 截图（3 秒自检 + 末帧 PNG）
dotnet run --project src/MilkDrop.ProjectM.App -- --smoke 240 --no-ui ^
  --shot artifacts/p8/idle.png

:: P9 批量验证（9795 个预设 × 10 帧，CSV 每 200 条增量落盘）
dotnet run --project src/MilkDrop.ProjectM.App -- --bench --bench-frames 10 ^
  --width 640 --height 480 --no-audio --bench-report artifacts/p9/bench-all.csv ^
  --preset-dir res\projectMSDL-2.0.0-win64\presets\presets-cream-of-the-crop

:: 分段跑（每个进程约 2000 个，避免一次跑太久）
... --bench --bench-skip 2000 --bench-limit 2000 --bench-report artifacts/p9/bench-2000-4000.csv
```

CSV 列：`index,file,loaded,frames,gl_error,load_ms,mean_luminance,non_black_ratio,error,screenshot`。
判定口径：`loaded=0`（含表达式/着色器编译失败）与 `gl_error!=0` 记为失败；
`non_black_ratio` 仅作信息列（`! Transition` 这类预设本来就是过渡到黑）。

---

## 8. 文档分工

- 本文件：进度勾选与计划调整。
- `projectMIssue.md`：踩坑与绕过方案（每条带 C++ 源行号 / 现象 / 结论）。
- `projectMMisc.md`：预设语义笔记、参数默认值来源、待办杂项。
