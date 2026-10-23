# projectM 移植踩坑记录

> 每条格式：现象 → 证据 → 结论/绕过方案。计划见 `projectMPlan.md`，杂记见 `projectMMisc.md`。

---

## P0（环境打通）阶段

### I-1 `Silk.NET.OpenGLES.ANGLE.Native` 的 win-x64 里其实是 x86 二进制

- **现象**：进程启动即 `System.BadImageFormatException: 试图加载格式不正确的程序 (0x8007000B)`，栈顶在 `Egl.GetProcAddress`。
- **证据**：读 PE 头，`runtimes/win-x64/native/libEGL.dll` 与 `libGLESv2.dll` 的 Machine 都是 `0x014C`（x86），与 `runtimes/win-x86` 下的文件字节数完全相同。
  已核对版本 `2023.10.11` / `2024.3.20` / `2025.9.12`，三个版本都一样（打包脚本把 x86 目录复制到了 win-x64）。
- **结论**：该包在 x64 下不可用。改用 `Avalonia.Angle.Windows.Natives`（`runtimes/win-x64/native/av_libglesv2.dll`，Machine=`0x8664`）。

### I-2 Avalonia 的 ANGLE 是"单 DLL + EGL 入口加前缀"

- **现象**：以为会有 `libEGL.dll` + `libGLESv2.dll` 两个文件，且入口名是标准的 `eglGetError`。
- **证据**：
  - 包内只有 `av_libglesv2.dll`；导出表共 1773 个符号，其中 `EGL_*` 121 个（`EGL_GetError`、`EGL_ChooseConfig`、`EGL_CreateWindowSurface`…），GL 入口不带前缀（`glTexImage3D` 等 1652 个）。
  - 该 DLL 里虽然能搜到 `eglGetError` 之类的字符串，但**没有对应导出**（`TryGetExport` 返回 false），别被字符串误导。
- **结论**：`Rendering/Egl.cs` 里 `LibName = "av_libglesv2.dll"`，所有 `EntryPoint` 写成 `EGL_Xxx`；
  `EGL_GetProcAddress` 查 `eglGetPlatformDisplayEXT` 时两种写法都要试（`eglGetPlatformDisplayEXT` / `EGL_GetPlatformDisplayEXT`）。
  换回 Chrome/官方 ANGLE 时只需改 `LibName` + 删掉 `EGL_` 前缀（已在文件头注释里写明）。

### I-3 `Silk.NET.OpenGLES` 不含 EGL 绑定

- **证据**：`Silk.NET.OpenGLES` 2.23.0 包里只有 `Silk.NET.OpenGLES.GL`、`OpenGLESLibraryNameContainer` 等 333 个类型，`Silk.NET.Core` 里只有 `IGLContext`/`INativeContext`，**没有** `EGL` 类型。
- **结论**：自写 `Rendering/Egl.cs`（19 个 P/Invoke）+ 用 `GL.GetApi(Func<string, nint>)` 传入自己的解析函数
  （模块导出优先，扩展走 `eglGetProcAddress`）。

### I-4 ANGLE 不把 `EGL_ANGLE_platform_angle` 写进 `EGL_EXTENSIONS`

- **现象**：`eglQueryString(display, EGL_EXTENSIONS)` 里没有 `EGL_ANGLE_platform_angle`，但 `eglGetPlatformDisplayEXT` 明明可用。
- **结论**：不能用扩展串判断这个能力；启动日志改成只记录"走的是哪条取 display 的路径"。

### I-5 NuGet 原生包的 `build/net461/*.targets` 对 net10.0 不生效

- **证据**：`Silk.NET.OpenGLES.ANGLE.Native` 里 targets 放在 `build/net461/` 与 `buildTransitive/net461/`，NuGet 只在 TFM 兼容时才导入（net461 与 net10.0 不兼容），原生 DLL 不会被拷到输出目录。
- **结论**：在 csproj 里用 `PackageReference ... GeneratePathProperty="true" ExcludeAssets="all"` +
  `<Content Include="$(PkgXxx)\runtimes\win-x64\native\*.dll" Link="%(Filename)%(Extension)" CopyToOutputDirectory="Always" />` 显式复制。

### I-6 `dotnet new sln` 在 .NET 10 默认生成 `.slnx`

- **结论**：要经典格式就加 `--format sln`（仓库里 `MilkDrop.Classic.sln` 是经典格式，保持一致）。

### I-7 ImGui.NET 1.90.9.1 的几处 API 细节

- `ImGuiChildFlags.Borders` 不存在，正确名字是 `ImGuiChildFlags.Border`（单数）。
- `ImGuiIOPtr.IniFilename` 是只读属性（`NullTerminatedString`），不能赋值 `null`；要禁用 ini 得走 `io.NativePtr->IniFilename`。
- `ImDrawCmdPtr` 取纹理用 `GetTexID()`（`TextureId` 属性仍在，但新版本已弃用）。
- `ImDrawListPtr.VtxBuffer.Data` 是 `nint`，构造 `ReadOnlySpan<T>` 前要显式转 `ImDrawVert*`。
- **ImGui 第一帧 `DrawData` 是空的**（`CmdListsCount == 0`）属正常，第二帧起才有内容（P0 实测：2 个指令列表 / 1972 顶点 / 3006 索引）。

### I-8 Silk.NET.OpenGLES 的枚举/重载命名与桌面 GL 包不同

- `GLEnum.Rg16f` 写成 `GLEnum.RG16f`、`GLEnum.Rg` 写成 `GLEnum.RG`（大写 RG 前缀）。
- `GLEnum` 里**没有** `FramebufferSrgb` 成员，ImGui 后端里暂时不做 sRGB 状态保存/恢复。
- `DeleteTextures`/`DeleteFramebuffers`/`DeleteSamplers` 没有单值重载，单个数要写成 `gl.DeleteTextures([tex])`。
- `TexImage2D` 传空像素用 `(void*)0` 那个重载（generic `T0 ByRef` 重载不能传 null）。

### I-9 P0 能力探测结果（本机，作为后续降级判断的基线）

环境：AMD Radeon 780M / Windows D3D11 / ANGLE 2.1.1 (git 1c89805903c1，`Avalonia.Angle.Windows.Natives` 2.1.27548.20260419)

```
GL version  : OpenGL ES 3.0 (ANGLE 2.1.1)
GLSL version: OpenGL ES GLSL ES 3.00
limits      : drawBuffers=8, colorAttachments=8, textureSize=16384, textureUnits=16, vertexAttribs=16
samplers=yes, texture3D=yes
renderable  : RGBA8=yes, RGBA16F=yes, RG16F=yes, MRT(RGBA8+RG16F)=yes
extensions  : EXT_color_buffer_float, EXT_color_buffer_half_float, OES_texture_float_linear,
              OES_texture_half_float_linear, EXT_float_blend, EXT_texture_filter_anisotropic,
              GL_OES_standard_derivatives, EXT_disjoint_timer_query
```

- 结论：**R3（warp pass 的 RG16F 运动向量 UV 附件 + MRT）不再是风险**，可按 projectM 原生 `USE_GLES` 路径 1:1 移植。
- 上下文建立耗时 ~100 ms；`--smoke 120` 稳定在 ~120 FPS（vsync 关闭时）/ 窗口合成下按显示器刷新率跑。

---

## P1（纯数据层）阶段

### I-10 `WaveformAligner` 首帧会把波形整体平移（原生既有行为，别"修"）

- **现象**：第一次 `Align()` 后波形被整体前移了几十个样本，看起来像 bug。
- **原因**：`_oldWaveformMips` 初值全 0，而 `Align()` 是先 `GenerateWeights()` 再 `CalculateOffset()`
  （`WaveformAligner.cpp:196-206`），于是"最优偏移"变成"找信号最弱的那 96 个样本"。
- **结论**：照抄不动。单测 `Align_FirstFrameShiftsAgainstSilence` 专门锁住这个行为。

### I-11 三频段都吃左声道频谱

- **证据**：`PCM.cpp:64-66` 里 `middles.Update(m_spectrumL...)`、`treble.Update(m_spectrumL...)` 用的是
  **L**（不是 R），看起来是复制粘贴笔误但影响所有预设的取色。
- **结论**：保持一致（C# `Pcm.UpdateFrameAudioData` 同样三处传 `_spectrumL`），否则画面会和原生不一样。

### I-12 `vol` / `vol_att` 的权重是 `0.333f`

- **现象**：静音时 `vol` 是 0.999 而不是 1。
- **结论**：`PCM.cpp:85` 写死 `0.333f`，保持。单测按 0.999 断言。

### I-13 `PresetFileParser` 的数值解析是 `std::stoi` / `std::stof` 前缀语义

- **现象**：`int.Parse("1.5")` 直接抛异常，而原生返回 1；`float.Parse("3abc")` 抛异常，原生返回 3。
- **结论**：手写前缀扫描（跳过空白、可选正负号、连续数字/小数点/指数），失败才退回默认值；
  溢出时 `std::stoi` 抛 `out_of_range`（属于 `logic_error`，被同一个 catch 吃掉）→ 同样返回默认值。
  见 `PresetFileParser.TryParseLeadingInt/TryParseLeadingDouble`。

### I-14 头注释说"解析时会剥掉 `//` 注释"，实现里并没有

- **证据**：`PresetFileParser.hpp` 的注释这么写，但 `PresetFileParser.cpp` 全篇没有剥注释逻辑；
  真正剥注释的是 `Utils::StripComments`（`Utils.cpp:32-78`），在 shader 装配阶段调用。
- **结论**：C# 按实现来（`PresetFileParser` 不剥注释），`Utils.StripComments` 留给 P3 的 `MilkdropShader`。

### I-15 P1 范围调整：`PresetState` / `PresetFactory` 推迟到 P2

- **原因**：`PresetState` 的构造函数就要建 `Framebuffer`/`Texture`/`MilkdropNoise`，
  `Preset` 抽象接口以 `RenderFrame`/`DrawInitialImage` 为核心，脱离 GL 层无法落地。
- **结论**：P1 只做纯数据（音频/时间/工具/解析），其余随 GL 封装一起进来（实际落到了 P3）。

---

## P2（GL 封装层）阶段

### I-16 `Silk.NET.OpenGLES` 自己就有 `Framebuffer`/`Shader`/`Texture`/`Sampler` 类型

- **现象**：测试里 `using Silk.NET.OpenGLES;` + `using MilkDrop.ProjectM.Renderer;` 之后
  `new Framebuffer(1)` 报 CS0104 二义性——Silk 那边有同名的 GL 句柄结构体。
- **结论**：外部代码引用这些类时要加别名：
  `using Framebuffer = MilkDrop.ProjectM.Renderer.Framebuffer;`（`Shader`/`Texture`/`Sampler` 同理）。
  在 `Renderer` 命名空间内部不受影响（同命名空间优先）。后续 App / MilkdropPreset 里都会碰到。

### I-17 C++ 的 `std::map::insert` 不覆盖已占用的槽位

- **证据**：`Framebuffer::CreateColorAttachment` / `SetAttachment` 用的是
  `m_attachments.at(index).insert({key, attachment})`，插入失败时新 `shared_ptr` 当场析构（纹理被删）。
- **结论**：C# 用 `SortedDictionary.TryAdd`，失败时 `attachment.Dispose()` 丢弃新附件，语义一致。
  另外用 `SortedDictionary` 而不是 `Dictionary`，因为 `UpdateDrawBuffers` 依赖"颜色附件按枚举升序"。

### I-18 `CopyTexture::Draw(tex, targetTexture, ...)` 这条重载其实不生效

- **证据**：`CopyTexture.cpp:143-155` 只是给 `TextureAttachment` 换了 `shared_ptr`，
  **没有**调用 `glFramebufferTexture2D` 重新挂载，所以 FBO 仍渲染到旧纹理，然后又把指针换回来。
- **旁证**：`MilkdropPreset.cpp:106/149` 调这个重载时 `targetTexture` 传的都是 `nullptr`，
  真正用到"换页"的是 `Draw(tex, framebuffer, index, ...)` 那条（MilkdropPreset.cpp:163/183）。
- **结论**：C# 按原样移植（保持行为一致），但不要在测试/新代码里依赖 target 非空的用法。已在代码注释里标注。

### I-19 `RemoveAttachment` 不能顺手销毁附件

- **原因**：`CopyTexture` 的翻页是"从 A 摘下来 → 挂到 B"，C++ 靠 `shared_ptr` 保命。
- **结论**：C# 的 `Framebuffer.RemoveAttachment` 只从表里摘除并返回附件，不 Dispose，由调用方决定。
  另外 `CopyTexture` 每帧都在换页，这里一旦 Dispose 就是每帧 use-after-free。

### I-20 `RenderItem::Init()` 在派生类构造函数里调虚函数

- **说明**：C++ 里 `CopyTexture` 的构造函数体里调 `RenderItem::Init()` → `InitVertexAttrib()`（虚函数），
  C++ 里此时对象已经是派生类型，OK。
- **C# 对应**：字段初始化器在基类构造之前执行，派生类构造函数体内调 `Init()` 时字段已就绪，同样安全。
  但派生类构造函数里若在 `Init()` 之后再初始化字段要小心顺序（`CopyTexture` 的 `_sampler` 是字段初始化器，安全）。

### I-21 离屏 GL 测试的线程绑定

- **现象**：EGL 的"当前上下文"是线程绑定的；xunit 不保证同一线程跑完一个类的所有测试。
- **结论**：`OffscreenGlFixture.TryMakeCurrent()` 在每个测试开头显式 `eglMakeCurrent`；
  拿不到上下文（无 GPU / 缺 ANGLE 库）时测试直接 return 跳过（xunit 2.x 没有动态 Skip）。

---

## P3（着色器装配）阶段

### I-22 `PreprocessPresetShader` 删 `sampler_state` 时改错了字符串（上游 bug）

- **证据**：`MilkdropShader.cpp:346-369` 先把 `program` 去注释拷成 `stripped`，循环里删的却是
  `stripped.replace(startPos, found - startPos, "")`，`program` 本身没变。于是后面
  `found = stripped.find("shader_body")` 得到的下标相对 `program` 已经错位（长度被删短了），
  带 `sampler_state` 的预设会被插到错误位置。
- **旁证**：同仓库的 `tests/libprojectM/MilkdropShaderCommentParsingTest.cpp:94-118` 镜像这段逻辑时，
  改的是 `program` 并每轮重新 `StripComments`——那才是设计意图。
- **结论**：C# 按"意图"实现（改原文 + 重新去注释，`MilkdropShader.RemoveSamplerStateBlocks`），
  注释里保留说明。其余步骤（签名替换、花括号截断、声明注入顺序）与 C++ 逐行一致。

### I-23 P3 语料验收结果

- **方法**：`MILKDROP_CORPUS=1 dotnet test --filter MilkdropShaderCorpusTests`，
  遍历 `res/projectMSDL-2.0.0-win64/presets` 全部 `.milk`，对每个预设的 warp/comp 代码做
  「预处理 → 注入 sampler/texsize 声明 → hlslparser 转译 → ANGLE 编译」。
- **结果**：**15895 个着色器，15759 成功，136 失败，成功率 99.14%**（目标 ≥95%），耗时约 470s。
- **失败归类**（完整清单在测试输出的临时文件里）：
  | 数量 | 原因 |
  | --- | --- |
  | 55 | `HLSL parsing failed`（warp 37 / comp 18），预设用了 hlslparser 不支持的写法 |
  | 8 | `GLSL generating failed`（comp 8） |
  | ~60 | ANGLE：`'[]' : array index out of range`（生成的 GLSL 里对常量/向量下标越界） |
  | 5 | ANGLE：`'shadow' : Function does not return a value`（生成器把某函数体丢了 return） |
  | 5 | ANGLE：`'+'/'/' : wrong operand types`（`float` 与 `const int` 混算，HLSL 允许隐式提升） |
  | 3 | ANGLE：`'sample' : Illegal use of reserved word`（GLSL 保留字改名表里没有 `sample`） |
- **结论**：达到 P3 验收门槛。上述失败是转译器（hlslparser 移植）层面的既有差异，
  与预设装配逻辑无关，留待 P4/P9 逐类处理；`preset` 语料里 `<no corpus inputs>` 之类不受影响。

### I-24 `MilkdropShader` 与 C++ 的接口差异（因依赖未移植）

- **原因**：`LoadTexturesAndCompile(PresetState&)` / `LoadVariables(PresetState&, PerFrameContext&)`
  需要 `PresetState`、`TextureManager`、`BlurTexture`、`PerFrameContext`，这些随 P4/P6/P7 才进来。
- **结论**：C# 的 `MilkdropShader` 只保留文本/转译部分：
  `LoadCode` + `TranspileHLSLShader(main, blur, others)` + `LoadTexturesAndCompile(Shader, main, blur, others)`；
  描述符由调用方给，program 由调用方持有（C++ 的 `m_shader` 在构造时就建 GL 对象，C# 不这么早碰 GL）。
  `GetReferencedSamplers` 里顺带做了 C++ 放在 `LoadTexturesAndCompile` 的"直接写 `sampler_blur1/2/3`"判定，
  结果等价（`UpdateMaxBlurLevel` 只升不降）。

---

## P4（主渲染管线）阶段

### I-25 `gmegabuf` 从"每个预设一份"变成"进程一份"

- **证据**：C++ `PresetState::PresetState()` 里 `globalMemory(projectm_eval_memory_buffer_create())`，
  每个预设持有自己的 gmegabuf，并在析构时销毁；<c>PerFrameContext</c> 构造时把它传给上下文。
- **C# 现状**：`MilkDrop.Eval` 的 `CompileContext.GlobalMemory` 是 `static`（对应 C 里的
  `static_global_memory`），所有预设、所有上下文共享同一份。
- **影响**：预设切歌时 `gmegabuf` 不会被清空，跨预设残留（C++ 每换一个预设就是空缓冲）。
  绝大多数预设只在 `gmegabuf` 里放临时数据，画面差异通常看不出来；与原生不一致的点在于切歌后的前几帧。
- **结论**：先按现有 Eval 的行为接（`PerFrameContext` 只共享 `globalRegisters`），
  若要完全对齐，需要给 `CompileContext` 增加可注入的 global buffer（改动 Eval 层，留待 P6 切歌时评估）。

### I-26 `PresetState` 的 GL 成员推迟

- **原因**：`untexturedShader`/`texturedShader`/`mainTexture`/`blurTexture` 依赖静态着色器全集与
  `BlurTexture`、`TextureManager`（P4/P7）。
- **结论**：本阶段 `PresetState` 只做数据、代码与表达式运行时（`GlobalRegisters`/`FrameQVariables`/
  `AudioData`/`RenderContext`/`RandomTextureDescriptors`）；GL 成员随 warp/composite 通路一起补。
