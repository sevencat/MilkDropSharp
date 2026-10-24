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
- **结论**：C# 的 `MilkdropShader` 保留文本/转译部分：
  `LoadCode` + `TranspileHLSLShader(main, blur, others)` + `LoadTexturesAndCompile(Shader, main, blur, others)`；
  描述符由调用方给，program 由调用方持有（C++ 的 `m_shader` 在构造时就建 GL 对象，C# 不这么早碰 GL）。
  `GetReferencedSamplers` 里顺带做了 C++ 放在 `LoadTexturesAndCompile` 的"直接写 `sampler_blur1/2/3`"判定，
  结果等价（`UpdateMaxBlurLevel` 只升不降）。
- **P4 已补**：`LoadVariables(PresetState, PerFrameContext)` 与
  `LoadTexturesAndCompile(PresetState)` 都已按 C++ 实现（含主纹理每帧刷新、模糊链绑定、描述符 `TryUpdate`）。

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

### I-26 `PresetState` 的 GL 成员推迟（P4/P5 已补）

- **原因**：`untexturedShader`/`texturedShader`/`mainTexture`/`blurTexture` 依赖静态着色器全集与
  `BlurTexture`、`TextureManager`（P4/P7）。
- **结论（已补）**：`UntexturedShader`/`TexturedShader` 做成**惰性**属性（首次访问才编译），
  这样纯数据测试仍然不需要 GL；`MainTexture` 由 `MilkdropPreset` 每帧设置；`BlurTexture` 惰性创建。
  `Dispose` 里只在 `GlContext.IsInitialized` 时释放 GL 对象。

### I-27 `GetSafeBlurMinMaxValues` 把区间撑开时写成了 `avg - 0.05`

- **证据**：`BlurTexture.cpp:392-397`（以及 2、3 级）在区间宽度小于 0.1 时执行
  `blurMin = avg - fMinDist * 0.5f; blurMax = avg - fMinDist * 0.5f;`——min/max 被设成同一个值，
  而不是 `avg ± 0.05`，随后 `scale = 1 / (max - min)` 会变成 inf。
- **结论**：逐行照抄（`BlurTexture.GetSafeBlurMinMaxValues`），让画面与原生一致；
  测试 `GetSafeBlurMinMaxValues_WidensTooNarrowRange` 钉住这个行为。

### I-28 3D 噪声平滑里 `base_y` 用的是 `z * size + y * size`

- **证据**：`MilkdropNoise.cpp:247`、`257`：X 方向插值的基址写成 `z * size + y * size`，
  正确应是 `z * size * size + y * size`；因此 `z > 0` 的切片会被写到别的层上。
- **结论**：逐行照抄（`MilkdropNoise.Generate3D`），注释里标了出处。视觉上是噪声纹路，
  与原生一致比"修正"更重要。

### I-29 纹理淘汰的年龄相同时会 0/0

- **证据**：`TextureManager.cpp:143` `(age - newest) / (oldest - newest)`，当参与比较的纹理年龄全相同时
  分母为 0（float 下得到 NaN），随后 `(uint32_t)(sizeBytes * NaN)` 变成 0，结果就是**一张都不淘汰**。
- **结论**：C# 保持同样算法（`float` 除零同样得到 NaN，转 uint 得 0）；测试里用两张不同年龄的纹理
  覆盖真正会淘汰的分支。另外 `GetTexture` 的注释说"Resets the texture age to zero"，但实现里并没有重置，
  C# 同样不重置。

### I-30 空的纹理描述符不会自动重新解析

- **证据**：`MilkdropShader::LoadVariables` 里对每个纹理描述符有
  `if (desc.Empty()) { desc.TryUpdate(*textureManager); }`，用来在纹理被淘汰后重新取。
  `TextureSamplerDescriptor::TryUpdate` 依赖 `TextureManager` 的 `weak_ptr` 语义。
- **现状（P7 已补）**：描述符改用 `WeakReference<Texture>` 保存纹理，`LoadVariables` 里在绑定前
  调用 `TryUpdate`（`UpdateFailed` 之后不再重试）。
- **注意**：`TextureManager::GetTexture` 对找不到的名字会回落到占位纹理，因此 `TryUpdate` 拿到的
  描述符通常"非空"，`UpdateFailed` 只在管理器整体不可用时才置位——与 C++ 行为一致。

---

## P5（波形与形状）阶段

- 16 种波形逐行照抄（含 4 个 Milkdrop2077 追加波形里"`sample + Samples - sampleOffset`"这种
  刻意越界的写法），`WaveformMath.SmoothWave` 与 `CustomWaveform.SmoothWave` 各自保留
  （前者 2N-1 个点、后者 2N 个点，C++ 就是这样）。
- `CustomShape` 需要两套 VAO/VBO（贴图 / 不贴图），基类 VAO 留给描边；与 C++ 一致。
- 读取 `shapecode_N_image`（projectM 扩展）、`wavecode_N_bDrawThick`/`shapecode_N_thickOutline`。

---

## P6（音频 + 切换）阶段

### I-31 `PresetTransition` 的噪声纹理解绑用了错误的单元号

- **证据**：`PresetTransition.cpp:125-128`：循环里 `noiseDescriptors[i - 2].Unbind(textureUnit)`，
  而 `textureUnit` 此时已经是循环结束后的值（12），不是每个描述符自己的单元号。
- **结论**：C# 按 `i + 2` 解绑（`Renderer/PresetTransition.cs`）。视觉上无差别（下一帧会重新绑定），
  但避免了把别的单元也一起解绑。

### I-32 转场着色器是"数据驱动"的，没有 6 个 C++ 类

- **证据**：`Renderer/TransitionShaders/*.frag` 只有 6 段 `mainImage` 实现，
  `TransitionShaderManager` 把公共头 + 实现 + `main()` 拼起来编译。
- **结论**：plan 里"6 种转场类"实际落成 `TransitionShaderSources`（6 个字符串访问器）+
  `TransitionShaderManager`（编译/随机取）+ `PresetTransition`（每帧画 quad），文件数与 C++ 对齐。

---

## P7（纹理管理）阶段

`TextureManager` / `MilkdropNoise` 在 P4 就跟着 warp shader 一起进来了（预设没有纹理时跑不起来），
P7 补的是**淘汰联动**：I-30 的 `TryUpdate` + 描述符弱引用。测试覆盖"空描述符能重取"、
"找不到时回落占位纹理"、"有效描述符不被替换"三条分支。

---

## P8（应用层）阶段

### I-34 `ProjectM` 类改名 `ProjectMEngine`

- **原因**：C++ 的 `libprojectM::ProjectM` 在 C# 里与命名空间 `MilkDrop.ProjectM` 同名，
  引用时类型名会被解析成命名空间，无法使用。
- **结论**：引擎门面叫 `ProjectMEngine`（`src/MilkDrop.ProjectM/ProjectMEngine.cs`）。

### I-35 `ParseFilename` 只认 `/`，Windows 路径会得到空文件名

- **证据**：`MilkdropPreset.cpp` 里 `ParseFilename` 用 `find_last_of('/')`，`\` 不认。
  C# 照抄（并有测试钉住 `ParseFilename(@"C:\x\a.milk") == ""`）。
- **影响**：Windows 上 `Preset.FileName` 是空串。
- **结论**：`ProjectMEngine.ActivePresetFileName` 在 `FileName` 为空时回退到"加载时用的完整路径/URL"，
  这样 UI 和测试都有可读名字；`Preset.FileName` 本身保持 C++ 语义不变。

### I-36 批量跑里的"全黑"不全是 bug

- **证据**：`presets-cream-of-the-crop/! Transition/*` 这批预设的设计就是"过渡到黑"
  （名字里直接写着 Fast/Slow transition to black），120 帧时本来就该是黑的。
- **结论**：`--bench` 报告里 `non_black_ratio` 只是"信息列"，不当失败条件；
  失败判定只看加载异常与 `glGetError`。

### I-37 ImGui 上下文在 `--bench` 模式下不能 `DestroyContext`

- **现象**：bench 结束时 `OnClosing` 里 `ImGui.DestroyContext()` 让进程 0xC0000005 崩溃。
- **原因**：`--bench` 分支不建 ImGui 上下文（没有输入上下文、没有渲染器），
  但 `OnClosing` 用 `_options.ShowUi` 判断，仍为 true。
- **结论**：改用 `_imguiContextCreated` 标志，只有真的创建过才销毁。

### I-38 新增两个 NuGet 依赖

- `NAudio` 2.2.1：WASAPI 环回采集（`AudioCapture`），无设备时构造不抛异常、静音降级。
- `StbImageWriteSharp` 1.16.7：PNG 写出（`Screenshot`），与已有的 `StbImageSharp` 同作者。
- 两者都只在 App 层，核心库 `MilkDrop.ProjectM` 不引入音频/图片依赖。

---

## P9（验证打磨）阶段

### I-41 批量跑必须喂"合成音频"，否则会把"没声音"误判成"渲染坏了"

- **现象**：第一轮 bench（`--no-audio`，10 帧）统计出 663 个 `non_black_ratio < 1%` 的预设，
  看上去像大片黑屏。
- **原因**：两个叠加因素——(1) 很多预设的亮度/颜色直接由 `bass/mid/treb` 驱动，无音频时全程为 0；
  (2) 10 帧（≈0.17 秒）对"warp 反馈慢慢累积"的预设远远不够。
- **结论**：新增 `--synthetic-audio`（440 Hz + 每 0.5 秒一次低频脉冲）与 `--bench-list` 复测。
  663 个里 329 个在 60 帧 + 合成音后出图；剩余的需要更长演化时间，属"慢热"而非 bug。

### I-40 高 DPI 下不能靠"OnLoad 里改 window.Size"来放大窗口

- **现象**：1.75x DPI 的机器上，界面只铺满左上角一块、其余是白的，截图尺寸也对不上。
- **原因**：EGL window surface 在 `EglContext.CreateForWindow(hwnd)` 时按**当时的客户区尺寸**定型；
  而 `window.Size = ...` 在 GLFW 下是异步请求，OnLoad 阶段改完立刻建 surface，
  就会出现 surface 还是旧尺寸、视口按新尺寸的错位。
- **结论**：窗口尺寸只用 `--width/--height` 决定，不再按 DPI 放大；
  DPI 只放大 ImGui 字体与控件尺寸。渲染相关的尺寸统一取 `window.FramebufferSize`
  （视口、ReadPixels、`SetWindowSize`、bench 报告尺寸都用它），不要用 `window.Size`。

### I-39 预设自身的语法错误表现为"加载失败"（与原生一致）

- **证据**：例如 `martin - purple pulsator - mom - ns2w.milk` 的
  `per_frame_22=k1 =  is_` / `per_frame_23=beat*equal(index%2,0);`，被换行拆断成两条语句。
- **行为**：C++ 的 `PresetFileParser::GetCode` 也是逐行拼 `\n`，`compile` 同样失败并抛
  `MilkdropCompileException`，`Preset::Initialize` 失败 → 整个预设加载失败。
- **结论**：保持"抛异常 = 报 PresetSwitchFailed"的行为，不静默吞掉；这类预设进 P9 失败清单。

---

## P10（Native AOT）阶段

### I-42 Silk.NET 在 AOT 下找不到窗口平台

- **现象**：AOT exe 启动即崩：`PlatformNotSupportedException: Couldn't find a suitable window platform (none registered)`。
- **原因**：`Silk.NET.Windowing` 默认按 assembly 名**反射加载**后端（`Silk.NET.Windowing.Glfw`），
  NativeAOT 裁剪后该 assembly 不可达。
- **结论**：`Program.Main` 里显式 `Window.PrioritizeGlfw()`，并在 `PublishAot` 条件下用
  `<TrimmerRootAssembly Include="Silk.NET.Windowing.Glfw"/>` / `Silk.NET.Input.Glfw` 根住。

### I-43 NAudio 的 WASAPI 在 NativeAOT 下不可用

- **现象**：ILC 报 `Method 'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject..ctor()' will always throw
  because: Invalid IL or CLR metadata`；运行期构造 `WasapiLoopbackCapture` 抛 `InvalidProgram_Specific`。
- **原因**：NAudio 用经典 COM interop（`Marshal.GetTypeFromCLSID` + RCW），NativeAOT 不支持动态 COM 激活。
- **结论**：`AudioCapture` 构造失败时降级为静音并打日志（AOT 版仍可渲染）。
  需要录音时用 JIT 版，或把 WASAPI 换成 `[GeneratedComInterface]` 手写封装（P10 待办，不影响出图）。

### I-44 没有固定时间步长就没法做像素级 A/B

- **现象**：同一预设、同样 240 帧，JIT 与 AOT 的截图平均差异 56/255，看起来毫无关系。
- **原因**：`time` 来自真实时钟，两次运行耗时不同（3.9s vs 2.3s），"第 240 帧"对应的模拟时间不一样。
- **结论**：`TimeKeeper.FixedTimeStep` + `--fixed-dt`，批量跑默认 1/60。
  固定后同一对截图的平均差异降到 **0.89/255**（剩下的是 `rand_frame`/`rand_preset` 与浮点累加）。
  要做逐位一致，还需要再补一个 `--seed`（当前未实现，作为可选项记录）。

---

## 通用（跨阶段）

### I-45 Q 变量扩到 64 的实现要点

- **只改三处**：`Constants.QVarCount` 32→64、`PresetShaderHeaderGlsl330.inc` 加 `_qi.._qp` 声明与 `q33..q64` 的
  `#define`、其余代码（`PerFrameContext`/`PerPixelContext`/`PerPixelMesh`/`MilkdropShader`）本来就按
  `QVarCount` 循环，uniform 名用 `"_q" + (char)('a' + i/4)` 生成，自动扩到 `_qp`。
- **必须注意**：`PresetShaderHeaderGlsl330.inc` 会被 HlslParser 当源码解析，**注释里不能出现非 ASCII**，
  否则报 "HLSL parsing failed"（排查时踩到过一次，错误信息只会打印整个 source，不指出行号）。
- **验证**：`q33`/`q64` 写进 per_frame 能驱动 comp shader 输出（新增测试）；
  抽 200 个预设跑 bench，加载失败仍是原来那 4 个预设自身语法损坏的，GL error 0。

### I-46 测试偶发 "HLSL parsing failed" 的真正原因

- **现象**：连跑 `dotnet test` 偶尔 1–3 个 `MilkdropShaderTranslationTests` 失败，重跑又全绿。
- **根因**：`MilkdropStaticShaders.Get()` 用 `_instance ??= new(...)` + 普通 `Dictionary` 缓存。
  xunit 并行下两个线程同时首次取单例／同时写缓存，会建出两个实例或让 `Dictionary` 抛/读半成品，
  拼出来的 shader 源码就是坏的。给 `HlslToGlslTranslator.Translate` 加锁并不能解决，因为状态不在这里。
- **修复**：
  1. `MilkdropStaticShaders` 改成 `Lazy<T>(ExecutionAndPublication)` + `ConcurrentDictionary`；
  2. `HlslToGlslTranslator.Translate` 仍加静态锁（C++ 移植过来的静态表本来也不是线程安全的）；
  3. 测试程序集 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`——
     这批测试共用进程级 `GlContext`，本来就不该并行（3s → 5s）。
- **结果**：连跑 5 次全绿。

### I-33 预设类名与命名空间同名

- **证据**：C++ 是 `namespace MilkdropPreset { class MilkdropPreset; }`，C# 照搬会得到
  `namespace MilkDrop.ProjectM.MilkdropPreset` + `class MilkdropPreset`。
- **影响**：在 `using MilkDrop.ProjectM.MilkdropPreset;` 之后写简单名 `MilkdropPreset` 会解析成**命名空间**，
  必须写完整名或别名（测试里用了 `using MilkdropPresetType = ...`）。
- **结论**：保留同名以对齐 C++ 的映射表；对外部调用者（P8 App 层）统一用别名。
