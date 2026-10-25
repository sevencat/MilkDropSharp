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
| 音频输出 | `NAudio.WinMM` 3.3.0（`WaveOut`，winmm 托管封装） | 纯 P/Invoke，AOT 实测可用（`projectMIssue.md` I-43 的 WASAPI 坑已随 NAudio 3 解决） |
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
| `src/ui/*`（RmlUi 面板） | 1471 | **重写为 ImGui**：只读的 pass/通道/错误面板、播放控制、截图（`buffer_panel.cpp` 782 行是主体）；**不做代码编辑**（见 P7 的"后续调整"） | `src/MilkDrop.ShaderToy.App/Ui/*` |
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
  Import/          ShaderToyJsonImporter.cs  ShaderLibrary.cs  ShaderMeta.cs  ShaderToyPage.cs
                   JsonTextRewriter.cs  ShaderToyDownloader.cs  LocalJsonImporter.cs  LocalLibrary.cs
  Resources/       TextureLoader.cs  FontTexture.cs  BuiltinTextures.cs
src/MilkDrop.ShaderToy.App/
  Program.cs  AppOptions.cs  Player.cs  FileDialog.cs
  Ui/              ShaderToyUi.cs  LibraryPanel.cs  UiFonts.cs
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
- 播放：`NAudio.Wave.WaveOut`（3.x 起 `WaveOutEvent` 改名为 `WaveOut`）。缓冲 3 段 × 20ms，避免爆音。
- 音频自产的闭环：`sound` pass 的输出同时喂给 `music` 通道（可选开关），这样 image pass 能对它做可视化。
- 外部音频（可选）：WASAPI 环回采集 → FFT → 音频纹理（`projectM` 线的 `AudioCapture` 已有，可复制）。
- **验收**：
  - `res/shadertoy/sound.txt`（`(int s, float t)` 签名）能听到声音；
  - 一个 `vec2 mainSound(float time)` 的最简正弦（440Hz A4）也能听到标准音，且频率实测 ≈440Hz（不加任何额外处理直接听起来就是 A 音）；
  - 频谱纹理与听到的声音一致；连续播放 10 分钟无爆音/漂移；采样率与设备不符时有重采样兜底。

### P7 ImGui 界面（中）· **已落地**（实测记录见 `shaderToyIssue.md` S-P7）

落地时的偏差：本地库列表读的是 `ShaderLibrary`（P8 的读侧提前做了），写侧（下载/导出）仍是 P8；
其余按本计划实现。界面细节：
最初的布局是"左侧库 / 中间主控 + 编辑器 / 右侧 pass 列表"，中文字体与等宽字体从系统字体加载（S7-5）。
（现行布局见下面"后续调整"：左列上=本地库、左列下=pass 列表、右侧=主控，高度自适应。）

**后续调整（P8 之后）：界面改为只读。** 去掉 pass 代码编辑器（`PassEditor.cs` 已删除）、通道下拉/采样器/
vflip/路径编辑、"应用并重编译"、三个标签页；只保留展示（通道折叠面板、pass 列表、错误面板）+
主控栏一个 **「重新加载」** 按钮（从数据库/文件重新读入，失败时保留当前画面）。
因此本节"界面里改代码 → 生效"这条验收作废，改由 `--selfcheck-reload` 覆盖
（内存改代码 → 重新加载 → 代码必须恢复成文件内容）。实测见 `shaderToyIssue.md` S7-13。

**现行布局（再调整）**：左列上 = 本地库（只留列表，取消"选中项详情"块，播放/打开目录/收藏/删除进右键菜单）+
左列下 = pass 列表；右侧 = 主控（高度自适应内容）；画面占其余区域。
**列表顺序在扫描时就固定**：播放只就地更新"最近播放"，不重排；隐式重扫（下载/导入/删除）保持既有顺序、
新增作品排最前；只有"重新扫描"按钮按最近播放重排。

- pass 列表（image/buffer/cube/sound）——**只读**，不再提供多行代码编辑
- **本地库列表**：左列上半，显示扫到的作品（标题 + 作者 + ID + 缺资源/收藏标记），双击播放，右键菜单管理
- 通道面板：**只读**展示每个 pass 的 `i0..i3`（类型、目标、filter/wrap/vflip）
- 播放控制：暂停/重置/时间轴、FPS、分辨率、截图、全屏（复用 `Hotkeys` 的 F11）、**重新加载**
- 错误面板：编译失败时把 GLSL 日志 + 行号显示出来（这是日常最常用的功能）
- **验收**：从左侧列表双击一个本地作品直接播放；拖入一个 shadertoy JSON 直接播放；
  外部改库（或拖入的 json 文件）后点"重新加载"，改动生效（原"界面里改代码"的验收已随编辑功能一起取消）。

### P8 本地库、下载与导入导出（中）· **已落地**（实测记录见 `shaderToyIssue.md` S-P8）

落地时的偏差：

- 写侧代码全部放在 `Import/`（与 `ShaderLibrary` 同目录），没有再单开 `Library/`：
  `ShaderMeta.cs`（meta.json 读写）、`ShaderToyPage.cs`（页面抠 JSON）、`JsonTextRewriter.cs`（原文级路径改写）、
  `ShaderToyDownloader.cs`（下载编排 + 资源抓取）、`LocalLibrary.cs`（删除/占用/最近播放）。
- **"导出"没有单独做**：`project.json` 本来就是原样保存的上游文本（只改了资源路径），拷出去即可导回 shadertoy；
  真正需要导出的只有 meta 里的私有信息，而那部分刻意不进 project.json。
- volume 资源**没下载**：`inputs[].id` 只是资源标识，站点没有可靠的下载 URL 约定；下载时记一条提示，
  绑定侧仍是占位纹理（S-P8）。
- 命令行额外加了 `--list`、`--download <id|url>`、`--import FILE`、`--as-copy`，`--shader` 现在也接受作品 ID（不只是文件路径）。
- 顺带修了渲染侧的一个真问题：下载后 JSON 里是相对路径 `textures/xxx.jpg`，而管线原来只按"文件名"在搜索目录里找，
  离线加载会全部落空 —— `ResolveTexturePath` 现在先按"作品目录 + 相对路径"找（`shaderToyIssue.md` S8-7）。
- 补了**本地 JSON 导入**（文件名即 ID），见本节末尾。

**目录布局（数据根 → 数据库 + 图片）**  ← **再调整：作品数据改为从 `shadertoy.db` 取，不再扫 `shaders/`**

数据根（`dataDir`）解析顺序：`--data` → **exe 旁的 `shadertoy.json`** 的 `dataDir`
→ **exe 旁的 `stdata/`**。相对路径按 exe 目录解析。内部结构与 `F:\code\ShaderToySnap\output`
（ShaderToySnap 的抓取结果）等价，两边指同一个目录即可：

```
<数据根>/                          ← 例如 F:\code\ShaderToySnap\output，或 exe 旁的 stdata
  shadertoy.db                    ← 作品数据**唯一来源**：shaders / renderpasses / shader_inputs / shader_outputs
  media/                          ← 站点 /media/... 资源按原路径存放（图片只从这里拿）
    media/a/<hash>.png            ← 即 JSON 里的 /media/a/<hash>.png
  screenshots/                    ← F12／界面截图
  shaders/<id>/                   ← 早期"文件布局"：下载/导入工具仍会写，**播放侧不再读**
```

- **播放侧只认 db + `media/`**：`ShaderLibrary.Scan(数据根)` 读 `shaders` 表出列表 —— **只查
  `ShaderId` / `Name` / `UserName` / `CapturedAt` 四列**（够显示、够排序，不数 pass、不数缺图、不读代码），
  详细数据等"双击播放"时才由 `ShaderDatabase.TryReadDetails(id)` / `TryLoadProject(id)` 去取。
- 工程优先取 `shaders.RawJson`（原样上游文本）；**pass 代码以 `renderpasses.Code` 为准**，
  但要求同 `PassIndex` 且 pass 类型一致才覆盖 —— 表被手工改乱时宁可用 JSON，不能张冠李戴。
  `RawJson` 缺失/坏掉时按 `renderpasses` + inputs/outputs 三张表重建（工程里带一条提示）。
- 图片路径原样保留站点形式（`/media/a/x.png`）→ 对应 `<数据根>/media/media/a/x.png`；管线按
  "搜索目录 + 相对路径 / 文件名 / `textures/` 下文件名"三级兜底，手工摆的图也能用。
- 缺图统计只算 texture/cubemap/volume 里 `LocalFilePath` 为空的；keyboard/music 的 `FilePath`
  是站点预设图，不算缺。
- SQLite 走系统自带 `winsqlite3.dll`（P/Invoke，不引 NuGet、AOT 友好），**只读**打开，不写别人的库结构。
- 写侧（下载/导入/补齐资源）代码保留但不再接界面/命令行：抓取交给 ShaderToySnap。
  `ShaderToyDownloader` / `LocalJsonImporter` / `MissingResourceFetcher` / `ShaderMeta` 仍由测试驱动，
  写的是上表的 `shaders/<id>/` 文件布局（`raw.json` + `meta.json` + `textures/`）。

**下载流程（输入 ID 或 URL，如 `flGyWc` / `https://www.shadertoy.com/view/flGyWc`）**

1. `HttpClient` 取页面（带正常 UA；不移植上游的 Boost.Asio + OpenSSL 登录流程，公开作品不需要登录）。
2. 从页面内嵌的 `gShaderToy` JSON 取出完整工程。
3. 扫描所有 `inputs[]`：`texture`（`src`）、`cubemap`（6 个 `src`/`filepath`）、`volume`（`id`）、`music/musicstream`（忽略），逐个下载到 `textures/`。
4. **改写 JSON 里的资源路径为相对路径**（`textures/noise.jpg`）—— 否则下次离线加载还是指向 `https://...`。
5. 抓 `thumbnail` 存 `thumbnail.png`；写 `raw.json`（原样）+ `meta.json`。
6. 单个资源 404/超时 → 记日志、用占位图/黑纹理，**不让整次下载失败**；UI 里标出"缺资源"。

**本地库的行为（数据库版）**

- 启动读 `<数据根>/shadertoy.db` 的 `shaders` 表建列表，按 `CapturedAt` 倒序（列表顺序扫描时固定，播放不重排）；
  库不存在/损坏只给一条提示，不抛异常、不崩。
- **列表只带"名字"级信息**（标题 / 作者 / ID）：pass 数、缺图数、标签这些要查别的表，统一推迟到打开作品时
  （`--list` 这种显式命令会自己按需再查一次，属于例外）。左栏项内不显示"缺 N 图"标记、hover 也只给标题/ID/作者，
  铺开详情在右侧主控面板（打开后显示来源/ID/作者/pass 数）。
- 左栏底部只留一行"数据库: …\shadertoy.db"（鼠标悬停显示完整路径），不再放操作提示行。
- 没有收藏/删除/占用统计：这些信息 db 里没有，也不去写别人的库；需要管理请用 ShaderToySnap。
- 命令行：`作品ID` 直接播（找不到就提示 + 播演示）、磁盘上的 json 路径则直接读文件；
  `--data DIR` 指定数据根；`--list` 打印库内容。拖入 json 仍可直接播放（不入库）。

**导入本地 JSON（文件名即 ID）**

- 入口：原为左侧库的 **「导入 JSON…」**（comdlg32 的 `GetOpenFileNameW`）+ 命令行 `--import FILE`；
  **数据源改为数据库后这两个入口已下线**（抓取/入库由 ShaderToySnap 负责），`LocalJsonImporter` 仅保留给测试与工具调用。
  `--as-copy` 对导入同样生效（同名另存为 `<id>-2`）。
- ID = **文件名（不含扩展名）**，目录 = `<id 小写>`；文件名里的非法字符在目录名上换成 `_`，
  `meta.json` 里保留原名（`raw.json` 的 `info.id` 不参与命名）。
- 资源：JSON 里引用的**本地文件**（绝对路径，或相对 JSON 所在目录的路径，或同目录同名文件）一起拷进 `textures/`，
  并把 JSON 里的路径改写成相对路径；cubemap 自动带上同目录的 `_1.._5` 六面。
- 站点路径（`/media/...`、`http(s)://`）离线拿不到 → 记进 `missingResources`，作品照样导入（缺的通道用黑纹理）。
- 源文件已在库根下的（比如重复导入 `shaders/x/raw.json`）直接拒绝，避免自我覆盖。
- **手工补资源**：站点要登录才能拿图（curl/.NET 均被 Cloudflare 403），所以支持"自己下载好丢进去"：
  放到 `<库>/<id>/textures/<文件名>`（平铺布局就是 `<库>/textures/`）；`ResolveTexturePath` 会按文件名兜底，
  JSON 里仍是 `/media/a/xxx.png` 也能用。想同时把 JSON 改成相对路径并清掉"缺资源"标记 →
  右键"补齐缺失资源"（文件已在就不联网）或 `--fetch-missing <id|目录>`。
- **代理**：下载/补齐都走可配置代理，默认 `socks5://127.0.0.1:1080`；
  `--proxy URL` / `MILKDROP_PROXY` / exe 旁的 `shadertoy.json` 的 `proxy`。

- **验收（数据库版）**：
  - `--data F:\code\ShaderToySnap\output`（或写进 settings 的 `dataDir`）→ `--list` 列出 db 里的作品，
    双击列表或 `--shader <id>` 能出帧；
  - 带贴图的作品（`flGyWc` / `73KXRh`）纹理从 `media/media/a/` 取到，控制台没有"找不到图片"；
  - 在库里改 `renderpasses.Code` → 点"重新加载"→ 画面按新代码变化；
  - 把 `shaders.RawJson` 清空 → 仍能按表重建并播放（界面/控制台提示"按表重建"）；
  - 多 pass 作品（`73ySRh` 的 image + common + buffer）pass 数与代码各自对得上；
  - 数据库不存在/损坏 → 列表为空 + 一条提示，程序照常启动。

### 多显卡机器：选哪块卡（已落地）

ANGLE 走 D3D11，默认用**系统默认适配器**（不指定就跟随 Windows"设置 → 系统 → 显示 → 图形"里给本 exe
配的高性能/省电偏好）。要精确指定：

- `--gpu list`：列出 DXGI 枚举到的所有适配器（编号 / 名称 / 厂商:设备 / 显存 / 是否软件）。
- `--gpu <编号|名字片段|luid:xxxxxxxx>`：按索引（`1`）、名称片段（`nvidia` / `radeon` / `intel`，命中多块时
  取非软件、显存最大的）、或 LUID 选卡；也可写进 `shadertoy.json` 的 `gpu` 字段（命令行优先）。
- 实现：`GpuAdapters`（`dxgi.dll` 的 `IDXGIFactory1::EnumAdapters1` + `IDXGIAdapter1::GetDesc1`，手写 vtable，
  不引 NuGet、不开内建 COM）→ 拿 LUID → 传给 `EglContext.CreateForWindow` →
  以 `EGL_PLATFORM_ANGLE_DEVICE_ID_HIGH/LOW_ANGLE`（`0x34D6/0x34D7`）建 platform display。
- 选错/不存在时打印候选列表并回落系统默认，不影响启动。

### 垂直同步（默认开）

- 默认 `eglSwapInterval(display, 1)`：跟显示器刷新率对齐，不撕裂、也不空转烧 GPU；
  启动日志会打 `VSync: 开（跟随显示器刷新率）`。
- `--no-vsync` 关掉（截图/跑分/测上限时用），日志打 `VSync: 关`。
- 只影响交换节奏，不影响 `--fixed-dt` 的确定性（同帧号仍是同状态，回归对比不受影响）。

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
| ~~ImGui 代码编辑器体验差~~ | ~~编辑长 shader 难受~~ | **已规避**：界面不做编辑，改用外部编辑器 + "重新加载"（S7-13） |

## 8. 与其它两条线的关系

- 三线并列、**互不依赖**：`MilkDrop.Classic`（D3D9/HLSL）、`MilkDrop.ProjectM`（GLES3/projectM 语义）、`MilkDrop.ShaderToy`（GLES3/Shadertoy 语义）。
- 共享：`MilkDrop.Eval`（Shadertoy 用不到，但同仓库）、`HlslParser`（用不到，Shadertoy 是 GLSL 原生输入）。
- **已定：P0 抽出 `MilkDrop.Rendering.Gles3`**（EGL/GL 封装、ImGui GLES3 后端、截图），ProjectM 改成引用它；
  `MilkdropFft`、`AppOptions`/`Hotkeys` 这类 App 层代码仍各自保留（语义不同，不值得共库）。
- 抽库后仓库是"一条公共渲染库 + 三条业务线"的结构，README 的"两条移植线"描述和结构图要一起改。
- 本计划落地后需要同步更新 `README.md` / `README.cn.md`：目前写的是"两条移植线"，届时改成三条
  （若抽出了公共渲染库，仓库结构图也要跟着改）。
