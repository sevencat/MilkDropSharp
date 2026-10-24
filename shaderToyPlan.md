# ShaderToy 播放器移植计划

目标：把 `refcode/desktop-shadertoy-0.4.3`（Vulkan/Daxa + RmlUi 的桌面 Shadertoy 播放器）移植成
**Silk.NET + ImGui + ANGLE/GLES3** 的 .NET 版本，并且**补上上游明确没做的 Sound pass**（音频由 shader 生成）。

参考作品：<https://www.shadertoy.com/view/flGyWc> —— 示例代码已放在
`res/shadertoy/image.txt`（`mainImage`）与 `res/shadertoy/sound.txt`（`mainSound`）。

---

## 1. 上游是什么样子

| 项 | 上游（desktop-shadertoy 0.4.3） |
|---|---|
| 语言/规模 | C++20，`src/` 约 **4.6k 行**（`viewport.cpp` 1175 行为核心，RmlUi 界面与后端另占 2.4k） |
| 图形 API | **Vulkan**，经 `deps/Daxa` 封装；shader 用 `deps/glslang` 现场编译成 SPIR-V |
| 着色器形式 | Shadertoy 的 GLSL 片段，加上一层"Vulkan 化"预处理（`CombinedImageSampler2D`、push constant、`#define iChannelN`、保留字改名 `ds_*`） |
| UI | **RmlUi**（HTML/CSS 描述界面，`main.rml` 571 行 + `main.rcss`） |
| 依赖 | GLFW3、RmlUi、nlohmann_json、stb、fmt、**Boost.Asio+Beast+OpenSSL**（登录 shadertoy.com 下载）、nativefiledialog、efsw（文件监视）、Vulkan SDK |
| 支持范围 | image / buffer / cubemap pass；texture / buffer / cubemap / volume / keyboard 输入 |
| **不支持** | **Sound pass（`mainSound`）**、music/musicstream 输入、SPIR-V 之外的后端 |

结论：**图形那一半（Vulkan + Daxa + glslang）整块不需要移植**。Shadertoy 的代码本来就是 GLSL，
搬到 GLES3 反而比上游少一层转换；真正要重写的是"pass 编排 + 通道绑定 + UI"，而被我们已有基建覆盖的那部分很多。

## 2. 技术选型

| 维度 | 选择 | 理由 |
|---|---|---|
| 窗口/输入 | `Silk.NET.Windowing`（GLFW 后端） | 与 `MilkDrop.ProjectM.App` 完全一致，`Hotkeys`/`ImguiInput` 可直接复用 |
| 渲染 | **ANGLE → OpenGL ES 3.0**（`av_libglesv2.dll`，EGL + D3D11 后端） | 用户指定；`EglContext`/`Egl.cs` 已有；Shadertoy 源码几乎零改动 |
| 着色器 | `#version 300 es`，**不引入 glslang / SPIR-V** | ES3 原生支持 `sampler2D/Cube/3D`、`texture()`、`texelFetch`、`dFdx` |
| 浮点目标 | `RGBA16F`（image/cube）、`RGBA32F`（buffer/sound） | 与上游 format 一致；需 `EXT_color_buffer_float` |
| UI | **ImGui.NET**（`cimgui.dll` + 现有 `ImguiGles3Renderer`） | 已有完整 GLES3 后端，AOT 也已验证 |
| 音频输出 | `NAudio`（JIT）+ `winmm.waveOut` P/Invoke（AOT 兜底） | NAudio 的 WASAPI 在 AOT 下不可用（已知坑，见 `projectMIssue.md`） |
| FFT | **不复用** `MilkdropFft`，在 `MilkDrop.ShaderToy/Audio/MusicTexture.cs` 里自写 512 点 FFT | 原因见 `shaderToyIssue.md` S6-7：三线互不依赖，且 Shadertoy 要的是对数频率轴，projectM 的线性轴+均衡表本来就得重映射 |
| JSON | `System.Text.Json` + **源生成**（`JsonSerializerContext`） | AOT 下不能用反射式序列化 |
| 项目 | `src/MilkDrop.Rendering.Gles3/`（**P0 抽出的公共渲染库**）+ `src/MilkDrop.ShaderToy/`（库）+ `src/MilkDrop.ShaderToy.App/`（可执行）+ 独立 sln | 与 ProjectM 共用 GLES3 层，不复制第二份 |
| buffer 分辨率 | 可配置：`viewport`（默认）/ `fixed`（1024×256）/ `scale` | 见 §5 P4 |

## 3. 上游 → 本项目的代码映射

| 上游文件 | 行数 | 处理方式 | 落到哪里 |
|---|---|---|---|
| `src/app/viewport.cpp` | 1175 | **重写**。Daxa 的 image/task graph 全部换成"直接跑 GL"；pass 排序、通道绑定、mipmap、JSON 解析逻辑照搬 | `Pass/RenderLoop.cs`、`Pass/PassGraph.cs`、`Import/ShaderToyJson.cs` |
| `src/app/viewport.inl`（`GpuInput`） | 47 | 结构照搬（`iResolution/iTime/iTimeDelta/iFrameRate/iFrame/iChannelTime[4]/iChannelResolution[4]/iMouse/iDate/iSampleRate`），改成 C# struct + `SetUniform` 推送 | `Pass/GpuInput.cs` |
| `src/app/viewport.glsl` | 152 | 顶点/包装 shader 重写（ES3 版本），去掉 BDA/push constant | `Shaders/ViewportVertex.glsl.cs` |
| `src/app/viewport.hpp` | 85 | 只保留数据模型（pass/channel 枚举），去掉所有 Daxa 类型 | `Pass/PassModel.cs` |
| `src/app/resources.cpp` | 61 | 简化：stb_image → `StbImageSharp`；省掉 Daxa 上传任务 | `Resources/TextureLoader.cs` |
| `src/app/ping_pong_resource.hpp` | 92 | 换成 GL 双 FBO + 两张纹理交换 | `Renderer/PingPongTarget.cs` |
| `src/main.cpp` | 427 | 窗口创建/事件循环/拖放/异常兜底 → 复用 `MilkDrop.ProjectM.App` 的 Program 骨架 | `src/MilkDrop.ShaderToy.App/Program.cs` |
| `src/ui/*`（RmlUi 面板） | 1471 | **重写为 ImGui**：pass 列表、代码编辑、通道绑定、播放控制、截图（`buffer_panel.cpp` 782 行是主体） | `src/MilkDrop.ShaderToy.App/Ui/*` |
| `src/rml/*`（RmlUi 的 Daxa/GLFW 后端） | 974 | **不移植** | — |
| `deps/Daxa`、`deps/glslang` | 数万行 | **不移植** | — |
| `shadertoy_api/*`（Boost.Asio 抓取） | ~300 | 改成 `HttpClient` 抓 `https://www.shadertoy.com/view/<id>` 页面内嵌 JSON（P8，可选） | `Import/ShaderToyDownloader.cs` |
| **Sound pass** | 上游无 | **新增**：`mainSound` 渲染 + 回读 + 播放 | `Audio/SoundPassRenderer.cs`、`Audio/AudioOutput.cs` |

可复用（不重写，直接引用或小改）：

| 现成资产 | 用途 |
|---|---|
| `Rendering/Egl.cs`、`EglContext.cs`、`GlContext.cs`、`GpuCapabilities.cs` | EGL/GLES3 上下文、扩展探测 |
| `Renderer/Framebuffer.cs`、`Texture.cs`、`Sampler.cs`、`RenderItem.cs`、`CopyTexture.cs`、`TextureAttachment.cs` | FBO/纹理/采样器/全屏 quad |
| `App/ImguiGles3Renderer.cs`、`ImguiInput.cs`、`Screenshot.cs`、`Hotkeys.cs`、`AppOptions.cs` | UI 后端、输入映射、截图、参数解析 |
| `Audio/MilkdropFft.cs`、`Audio/Pcm.cs` | 音频通道纹理（FFT / 波形） |
| `tests/golden` 的做法 | shader 转译的输入/期望输出对照测试 |

## 4. 目录规划

**已定：P0 就把 GLES3 公共层抽出来**，ProjectM 与 ShaderToy 共用（不复制第二份）。

```
src/MilkDrop.Rendering.Gles3/          ← 新增公共库（从 MilkDrop.ProjectM 抽出）
  Egl.cs  EglContext.cs  GlContext.cs  GpuCapabilities.cs
  Shader.cs  Texture.cs  Sampler.cs  Framebuffer.cs  TextureAttachment.cs
  RenderItem.cs  CopyTexture.cs  MipmapChain.cs        （MipmapChain 为本项目新增：逐级 blit）
  Ui/ImguiGles3Renderer.cs  Ui/ImguiInput.cs  Screenshot.cs

src/MilkDrop.ShaderToy/
  Pass/            PassModel.cs  PassGraph.cs  RenderLoop.cs  GpuInput.cs
  Compile/         ShaderToyTranspiler.cs  (ShaderToy GLSL → GLSL ES 3.00)
  Renderer/        PingPongTarget.cs  ChannelBinder.cs
  Channels/        TextureChannel.cs  BufferChannel.cs  CubeChannel.cs  VolumeChannel.cs  KeyboardChannel.cs  AudioChannel.cs
  Audio/           SoundPassRenderer.cs  AudioEngine.cs  AudioBuffer.cs  MusicTexture.cs  IAudioSink.cs
                   （NAudio 的 WaveOutEvent 实现在 App 层：MilkDrop.ShaderToy.App/WaveOutAudioSink.cs）
  Import/          ShaderToyJson.cs
  Library/         LocalLibrary.cs  LibraryEntry.cs  ShaderToyDownloader.cs  AssetFetcher.cs
  Resources/       TextureLoader.cs  FontTexture.cs  BuiltinTextures.cs
src/MilkDrop.ShaderToy.App/
  Program.cs  AppOptions.cs  Player.cs
  Ui/              MainWindow.cs  PassEditor.cs  ChannelPanel.cs  CodeEditor.cs  LibraryPanel.cs
tests/MilkDrop.ShaderToy.Tests/
  TranspileTests.cs  JsonImportTests.cs  KeyboardTextureTests.cs  LibraryTests.cs  SoundPassTests.cs (需要 GPU)
MilkDrop.ShaderToy.sln
```

**不抽走**的东西（留在 `MilkDrop.ProjectM`）：`TextureManager`、`MilkdropNoise`、`TransitionShader*`、
`PresetTransition`、`MilkdropPreset/*` —— 这些是 projectM 语义，共库只放"通用 GLES3 封装"。

## 5. 阶段拆分（P0–P9）

每个阶段都以"能跑 + 有验收物"结尾，风格与 `projectMPlan.md` 一致。

### P0 抽公共库 + 骨架与上下文（中）

**0a. 抽 `MilkDrop.Rendering.Gles3`（先做，后面所有阶段都依赖它）**

- 新建类库，把 `MilkDrop.ProjectM` 里这些文件**原样搬迁**（约 2000 行，纯机械移动）：
  `Rendering/{Egl,EglContext,GlContext,GpuCapabilities}.cs`、`Renderer/{Shader,Texture,Sampler,Framebuffer,TextureAttachment,RenderItem,CopyTexture}.cs`、
  `App/{ImguiGles3Renderer,ImguiInput,Screenshot}.cs`。
- 命名空间 `MilkDrop.ProjectM.*` → `MilkDrop.Rendering.Gles3`；`ProjectM` 与 `ProjectM.App` 加 `ProjectReference` + 改 `using`。
- 顺手把 `Framebuffer` 里 projectM 专用（`RemoveColorAttachment` 的 attachment 索引语义等）保留，但不要往里塞 Shadertoy 的东西；
  ShaderToy 侧需要的"多 pass 目标"用新类 `PingPongTarget` 组合这些原语。
- 新增 `MipmapChain`（逐级 blit 生成 9 级），两边都能用。
- **验收**：`dotnet build MilkDrop.ProjectM.sln` 零错误；`--smoke 300` 跑 5 个基准预设，与抽库前的 `engine` 单帧耗时对比在 ±2% 内（用之前那套 `MILKDROP_PERF` 埋点）；`--bench --bench-limit 40` 全通过。

**0b. ShaderToy 骨架**

- 建 `MilkDrop.ShaderToy` + `MilkDrop.ShaderToy.App` + sln；`Directory.Build.props` 已有 `net10.0`。
- 引 `MilkDrop.Rendering.Gles3`，建 ES3 上下文、开 GLFW 窗口、清屏渐变。
- 挂 `ImguiGles3Renderer` 显示 FPS。
- **验收**：窗口起来、`GL_VERSION` 为 `OpenGL ES 3.0 (ANGLE ...)`、ImGui 可见、`EXT_color_buffer_float` 为真、
  `EXT_color_buffer_half_float` 一并打日志。

### P1 数据模型与 JSON 导入（小）

- `ShaderToyJson`：解析官方导出格式（`renderpass[] / type / code / inputs / outputs / sampler`），
  容忍 `id` 数字/字符串混用、`code` 里的字面 `\n`、缺失 `sampler`。
- `common` pass 拼成公共代码；`buffer/cubemap` 建 id→索引表；`image` 只认第一个。
- **验收**：`default-shader.json`、`res/shadertoy/*.txt` 组装的工程、以及从 shadertoy 导出的 10 个 JSON 都能解析出正确结构（不渲染，只打印 pass 图）。

### P2 Shadertoy → GLSL ES 3.00 转译（中，核心风险）

- 反转义 → 剥 `#version`/`#extension` → 注入 `precision highp float/int;`
- 按 JSON 的**真实类型**声明 `uniform sampler2D iChannelN` / `samplerCube` / `sampler3D`；
  未绑定的通道给 1×1 黑纹理（`Channels` 后补）
- 包一层 `void main()`：调 `mainImage(fragColor, gl_FragCoord.xy)`（或 `CUBEMAP` 变体）；
  用户自己写了 `main()` 就跳过包裹
- 兼容宏：`gl_FragColor`→`fragColor`、`texture2D/textureCube`→`texture`、`varying`/`attribute`→`in/out`
- 内置 uniform 快照：`iResolution/iTime/iTimeDelta/iFrameRate/iFrame/iMouse/iDate/iSampleRate/iChannelResolution[4]/iChannelTime[4]`
- **验收**：把 `res/shadertoy/image.txt`（flGyWc）与 `tests/golden` 里 20 段真实 shadertoy 代码全部编译通过（编译日志归档），失败项逐条记录到 `shaderToyIssue.md`。

### P3 单 pass Image 渲染（中）

- 全屏 quad + `RenderItem` 复用；image pass 渲到 `RGBA16F`，再 `CopyTexture` 到默认帧缓冲（带 sRGB/线性处理）。
- 时间推进（可暂停/重置/`--fixed-dt`）、鼠标状态机（`iMouse` 的点击/释放取负语义）。
- **验收**：flGyWc 的画面与网页截图**逐像素对齐**（尺寸、y 轴方向、鼠标交互都对）。

### P4 Buffer pass 与 pass 图（大）

- 每个 buffer pass：两张 `RGBA32F` 纹理 ping-pong（同 pass 自反馈在 GL 里是非法操作）；
  每帧按 JSON 顺序执行（**不做拓扑排序**，与上游一致；shadertoy 语义就靠这个顺序）。
- mipmap：`mipmap` 过滤的通道在每帧末尾生成 **9 级**（上游 `#define MAX_MIP 9`，即 `max_lod = 8`），
  用逐级 blit 实现，不依赖 `glGenerateMipmap`。
- 视口尺寸变化 → 重建所有 pass 目标并保留内容（照搬上游的 resize 逻辑）。

**buffer 分辨率做成可配置项（已定）**

三种模式，存在工程 JSON 里，命令行可覆盖：

| 模式 | 说明 | 典型用途 |
|---|---|---|
| `viewport`（默认） | 跟随绘制缓冲尺寸，即上游行为 | 分辨率无关的作品，最清晰 |
| `fixed` | 固定像素尺寸，默认 `1024×256`（网页版 Shadertoy 的默认） | 依赖固定 buffer 分辨率/hash 抖动的作品，与网页对齐 |
| `scale` | 视口 × 系数（如 `0.5`） | 大分辨率下省填充率（buffer 常见瓶颈），画面允许糊一点 |

- 命令行：`--buffer-mode viewport|fixed|scale`、`--buffer-size 1024x256`、`--buffer-scale 0.5`；
  UI 里给同样的下拉 + 数值框，改动即时重建目标（不重启）。
- `iChannelResolution[i]`、`iResolution`（pass 内看到的）都按**实际使用的 buffer 尺寸**回填，
  否则 fixed 模式下依赖 `iChannelResolution` 的算法会错。
- 切换模式时按"保留内容"的路径重建（同 resize），避免闪一帧黑。
- **验收**：同一个 fluid 类作品在 `viewport` 与 `fixed 1024x256` 下都能跑；
  fixed 模式与网页截图对比误差明显小于 viewport 模式；切换模式不崩、不掉纹理。

### P5 输入通道全家桶（中）

| 通道类型 | 实现要点 |
|---|---|
| `texture` | 图片加载 + `filter`(nearest/linear/mipmap) + `wrap`(clamp/repeat) + `vflip` |
| `buffer` | 引用其他 pass 的输出（含 ping-pong 的“上一帧”语义） |
| `cubemap` | 上传 6 面（1024×1024/面）+ `samplerCube` |
| `volume` | 3D 纹理（`sampler3D`），从 shadertoy 的 volume 资源或 `.raw` 生成 |
| `keyboard` | 256×3 RGBA8 键盘纹理，按 Shadertoy 约定编码按键状态 |
| `music`/`musicstream` | **新增**：我们自己生成的音频 → 512×2 音频纹理（第 0 行频谱，第 1 行波形） |

- **验收**：每类通道各挑 2 个 shadertoy 作品跑通；键盘通道用“按 A 变色”的小 shader 自测。

### P6 Sound pass 与音频输出（大，上游完全没有）· **已落地**（实测记录见 `shaderToyIssue.md` S-P6）

**语义备注：网页端到底在算什么**

- 网页端的入口是 `vec2 mainSound(in float time)`，参数就是**当前时刻（秒）**。
  浏览器为每个采样点算一个 `time = 采样序号 / 44100.0`，shader 返回这一刻左右声道的幅度（`-1..1`）。
- 典型写法就是一句纯数学，不需要任何"音频 API"：

  ```glsl
  // A4 = 440Hz，2π ≈ 6.2831
  vec2 mainSound(float time) {
      return vec2(sin(6.2831 * 440.0 * time));   // 左右声道同一个正弦
  }
  ```

  440Hz 一个周期约 **2.27 ms**，44.1kHz 下约 **100 个采样点** —— 所以它算的是"这一瞬间两个声道该推出多少"。
- 浏览器并不逐样本调一次 draw：它按 WebAudio 的缓冲块（常见 512 / 1024 个采样）批量把这批波形填满，
  收集成连续一段再交给声卡。我们的做法照此：**GPU 批量渲染 → 回读 → 环形缓冲 → 声卡**。
- **签名有两种**，都要支持：
  - 现行网页端：`vec2 mainSound(in float time)`（只给时间）
  - 早期/常见导出：`vec2 mainSound(int samp, float time)`（多给绝对采样序号，方便写按索引的算法）
  - 实测仓库里的示例 `res/shadertoy/sound.txt` 第 84 行是 **`vec2 mainSound(int s, float t)`**，即第二种。
  - GLSL 没有函数重载，所以编译前要扫一遍参数表，按实际签名生成 wrapper（详见 `shaderToyIssue.md` S6-1）。

- 渲染与播放：
  - 目标 `RGBA32F` 纹理 `512×2`（或 `1024×2`），一次跑出一批采样；`samp` 与 `time` 都通过 uniform 传入；
  - `glReadPixels` 回读 float，交错成 stereo → 环形缓冲 → 播放器。
- 播放：`NAudio.Wave.WaveOutEvent`（JIT）；AOT 走 `winmm.waveOut`。缓冲 3~4 段 × 20ms，避免爆音。
- 音频自产的闭环：`sound` pass 的输出同时喂给 `music` 通道（可选开关），这样 image pass 能对它做可视化。
- 外部音频（可选）：WASAPI 环回采集 → FFT → 音频纹理（`projectM` 线的 `AudioCapture` 已有，可复制）。
- **验收**：
  - `res/shadertoy/sound.txt`（`(int s, float t)` 签名）能听到声音；
  - 一个 `vec2 mainSound(float time)` 的最简正弦（440Hz A4）也能听到标准音，且频率实测 ≈440Hz（不加任何额外处理直接听起来就是 A 音）；
  - 频谱纹理与听到的声音一致；连续播放 10 分钟无爆音/漂移；采样率与设备不符时有重采样兜底。

### P7 ImGui 界面（中）

- pass 列表（image/buffer/cube/sound）、每个 pass 的多行代码编辑（`InputTextMultiline`，可选后续接高亮）
- **本地库列表**：左侧一栏，显示 `shaders/` 里扫到的作品（缩略图 + 标题 + 作者 + ID），双击加载，按名字/作者过滤
- 通道面板：下拉选类型、拖入图片、采样器 filter/wrap/vflip
- 播放控制：暂停/重置/时间轴、FPS、分辨率、截图、全屏（复用 `Hotkeys` 的 F11）
- 错误面板：编译失败时把 GLSL 日志 + 行号显示出来（这是日常最常用的功能）
- **验收**：不改 JSON、只在界面里改代码 → 重新编译并生效；从左侧列表点一个本地作品直接播放；拖入一个 shadertoy JSON 直接播放。

### P8 本地库、下载与导入导出（中）

**目录布局（每个作品一个独立子目录，按 ID 命名）**

```
shaders/                          ← 本地库根（默认 exe 旁，可用 --library 指定）
  flGyWc/
    project.json                  ← Shadertoy 官方导出格式，**原样保真**
    meta.json                     ← 本地元信息：来源 URL、下载时间、标题、作者、标签、收藏、最后播放时间
    thumbnail.png                 ← 预览图（有就存）
    textures/
      noise.jpg                   ← 该作品用到的 input 资源（图片 / cubemap 6 面 / volume）
```

- 目录名只用 **ID**（6~7 位字母数字），标题里的 `:` `?` `*` 等一律不进路径 —— 规避 Windows 非法字符与 260 字符限制。
- `project.json` 保存**原始文本**（不经过我们模型的反序列化再序列化），避免 `\n` 转义被改写、字段顺序/未知字段丢失。
- `meta.json` 由我们维护，`project.json` 视为"上游格式"不塞私有字段 —— 这样导出的文件还能直接导回 shadertoy。

**下载流程（输入 ID 或 URL，如 `flGyWc` / `https://www.shadertoy.com/view/flGyWc`）**

1. `HttpClient` 取页面（带正常 UA；不移植上游的 Boost.Asio + OpenSSL 登录流程，公开作品不需要登录）。
2. 从页面内嵌的 `gShaderToy` JSON 取出完整工程。
3. 扫描所有 `inputs[]`：`texture`（`src`）、`cubemap`（6 个 `src`/`filepath`）、`volume`（`id`）、`music/musicstream`（忽略），逐个下载到 `textures/`。
4. **改写 JSON 里的资源路径为相对路径**（`textures/noise.jpg`）—— 否则下次离线加载还是指向 `https://...`。
5. 抓 `thumbnail` 存 `thumbnail.png`；写 `project.json`（原样）+ `meta.json`。
6. 单个资源 404/超时 → 记日志、用占位图/黑纹理，**不让整次下载失败**；UI 里标出"缺资源"。

**本地库的行为**

- 启动扫 `shaders/*/project.json` 建索引（只读 `meta.json` + 文件大小，不快照读全部代码），列表按最近播放排序。
- 同名 ID 再次下载：默认覆盖 `project.json`/`meta.json`，资源按需增量（已存在且非空则跳过）；UI 提供"另存为 `<id>-2`"。
- 删除：UI 里删整目录（二次确认）；显示每个作品占用空间。
- 命令行：`--shader <id|路径>` 直接播；`--library DIR` 指定库根；`--list` 打印库内容。
- 手工放进去的作品（拷一个 `project.json` + `textures/`）也要能被扫到 —— 不依赖 `meta.json`，缺失就现场生成默认值。

- **验收**：
  - 输入 `flGyWc` → 下载完成 → 断网重启程序 → 仍能从本地库加载并正常播放（含它用到的纹理）；
  - 目录里只有 `project.json` 也能被识别；
  - 下载一个带 cubemap 的作品，6 面资源齐全；
  - 故意让某个纹理 URL 404，作品仍能加载、UI 提示缺资源；
  - 重复下载同 ID 不产生重复目录。

### P9 验证与 AOT（中）

- 选 10~15 个 shadertoy 作品（覆盖 image/buffer/cubemap/keyboard/sound），与网页截图逐像素对比，误差写进报告。
- 批量脚本：`--smoke N --shot` 出图，`--bench` 出每 pass 耗时。
- Native AOT 发布（复用 ProjectM 线的做法）：JSON 源生成、ImGui 原生库随行、音频退化为静音或 waveOut。
- **验收**：AOT 产物体积/启动时间记录在案；10 个案例全部通过。

## 6. 工作量估算

| 部分 | 预计 C# 行数 | 说明 |
|---|---|---|
| **抽公共库 `MilkDrop.Rendering.Gles3`** | 新增 ~80 行，搬迁 ~2000 行 | P0 先做：纯机械移动 + 改命名空间，主要成本在 ProjectM 的回归验证 |
| 转译器 | 250–350 | 比上游简单（不需要 Vulkan 化），但要处理一堆边缘语法 |
| Pass 模型 + JSON 导入导出 | 400–500 | 上游 230 行解析逻辑 + 容错 |
| 渲染循环 / pass 图 / 通道绑定 | 500–700 | 上游 `record()` 约 650 行（287–944）是主体 |
| 通道实现（6 类） | 400–500 | 纹理/立方体/3D/键盘/音频 |
| Sound pass + 音频输出 | 400–500 | **上游没有，纯新增** |
| ImGui 界面 | 400–600 | 替代上游 1471 行 RmlUi 面板 |
| buffer 分辨率三模式 + UI | 60–100 | 见 P4 |
| **本地库 + 下载（含资源抓取）** | 250–350 | 目录扫描/索引/meta、HTML 取 JSON、资源下载与路径重写、列表 UI |
| 测试 | 300–400 | 转译 golden + 通道编码 + 音频回路 + 本地库 |
| **合计** | **约 3.0k–4.1k 行（新增）** | 另有 ~2k 行从 ProjectM 搬进公共库，不算新写 |

不移植的部分（占上游一半以上）：`src/rml/*`（974 行，RmlUi 的 Daxa/GLFW 后端）、
`src/ui/*` 的 RmlUi 版实现（1471 行，改用 ImGui 重写）、Vulkan/Daxa 适配、glslang 集成与 SPIR-V 反射、
Boost.Asio/OpenSSL 登录 —— **合计约 2.4k 行 C++ 加两个第三方依赖整块消失**。

按 `projectMPlan.md` 的节奏（那条约 3.5 万行 C# 用了 P0–P10），这个项目体量小得多，
预计 **P0–P9 十个阶段**，其中 P2（转译）和 P4（buffer/pass 图）、P6（音频）是三处硬骨头。

## 7. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| **抽公共库把 ProjectM 改坏** | 9795 预设的既有验收失效 | P0 抽完后立刻跑 `--smoke` 5 个基准 + `--bench 40` + `engine` 单帧耗时对比（±2%），并单独提交一次 |
| `EXT_color_buffer_float` 缺失（老显卡/远程桌面） | RGBA32F buffer 不可用，feedback 类作品全废 | 启动即探测，缺失时明确报错并降级到 RGBA16F（画面会漂） |
| `fixed` 模式与作品预期不符 | 某些作品按网页 `1024×256` 写死坐标，`viewport` 模式下会偏 | 默认 `viewport`，UI 里一键切 `fixed 1024x256`，并在对比报告里两种模式都跑 |
| GLES3 语法覆盖面不足 | 一部分 shadertoy 编译不过 | P2 用 20 段真实代码做回归，失败项进 issue 文档，必要时加重写规则 |
| 转译规则做成"字符串替换"式补丁堆 | 后续难维护 | 规则集中在 `ShaderToyTranspiler`，每条规则带单测（输入/输出片段对照） |
| Sound pass 回读造成 GPU 同步 | 帧率抖动 | 批量渲染 + 环形缓冲，回读放在每 N 帧；用 `PBO` 异步回读兜底 |
| 音频输出与画面不同步 | 听感/可视化错位 | 用音频时钟驱动 `iTime`（可选），或至少做漂移补偿 |
| ImGui 代码编辑器体验差 | 编辑长 shader 难受 | P7 先用 `InputTextMultiline`，后续再评估接 `ImGuiColorTextEdit` |

## 8. 与其它两条线的关系

- 三线并列、**互不依赖**：`MilkDrop.Classic`（D3D9/HLSL）、`MilkDrop.ProjectM`（GLES3/projectM 语义）、`MilkDrop.ShaderToy`（GLES3/Shadertoy 语义）。
- 共享：`MilkDrop.Eval`（Shadertoy 用不到，但同仓库）、`HlslParser`（用不到，Shadertoy 是 GLSL 原生输入）。
- **已定：P0 抽出 `MilkDrop.Rendering.Gles3`**（EGL/GL 封装、ImGui GLES3 后端、截图），ProjectM 改成引用它；
  `MilkdropFft`、`AppOptions`/`Hotkeys` 这类 App 层代码仍各自保留（语义不同，不值得共库）。
- 抽库后仓库是"一条公共渲染库 + 三条业务线"的结构，README 的"两条移植线"描述和结构图要一起改。
- 本计划落地后需要同步更新 `README.md` / `README.cn.md`：目前写的是"两条移植线"，届时改成三条
  （若抽出了公共渲染库，仓库结构图也要跟着改）。
