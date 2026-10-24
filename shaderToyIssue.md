# ShaderToy 播放器踩坑记录

> 每条格式：现象 → 依据 → 结论/对策。计划见 `shaderToyPlan.md`。
>
> **状态：尚未开工。** 标注 `已确认` 的来自上游源码或本仓库既有结论；标注 `待验证` 的是预判，
> 落地时必须实测后改写成真实记录（附证据）。

---

## S-P0 环境与上下文

### S0-1 ANGLE 必须用 Avalonia 的 `av_libglesv2.dll`（已确认）

- **现象**：用 `Silk.NET.OpenGLES.ANGLE.Native` 直接跑，进程启动即 `BadImageFormatException`。
- **依据**：`projectMIssue.md` 的 I-1 / I-2 —— 那个包 `runtimes/win-x64` 里是 x86 二进制；
  Avalonia 的 `av_libglesv2.dll` 是真 x64，且导出的是 `EGL_*` 带前缀的入口。
- **对策**：复用 `src/MilkDrop.ProjectM/Rendering/Egl.cs`（`LibName = "av_libglesv2.dll"`、`EGL_Xxx`）。

### S0-2 `EXT_color_buffer_float` 是硬依赖（待验证）

- **现象**：Shadertoy 的 buffer pass 用 `RGBA32F`、image/cubemap 用 `RGBA16F`，缺这个扩展就建不出可渲染的浮点 FBO。
- **依据**：上游 `viewport.cpp:1173-1180`，`pass_format = R32G32B32A32_SFLOAT`（buffer）/ `R16G16B16A16_SFLOAT`（image、cube）。
- **对策**：P0 启动即查询并打日志。缺失时直接报错退出，**不要**静默降级成 RGBA8 —— feedback 类作品会直接崩坏，
  故障现象是"画面全黑/糊成一团"，很难反查到根因。

### S0-3 浮点纹理的线性过滤与 mipmap 生成（待验证）

- **现象**：`linear` 采样 `RGBA32F` 需要 `OES_texture_float_linear`；`glGenerateMipmap` 对 `RGBA32F` 在部分实现上不支持。
- **依据**：上游用"手工逐级"方式生成 mipmap（`viewport.cpp:493-500`、`603-610`，`MAX_MIP-1` 次循环），没有直接用 `glGenerateMipmap`；
  `MAX_MIP = 9`（`viewport.cpp:30`），即 9 级、`max_lod = 8`。
- **对策**：跟随上游做逐级 blit 生成（`CopyTexture` 已有，加一个 `level` 参数即可），不依赖自动生成。

### S0-4 浮点回读的格式组合（待验证）

- **现象**：音频（P6）要 `glReadPixels` 读 `RGBA32F`。
- **对策**：用 `GL_RGBA` + `GL_FLOAT`（ANGLE/D3D11 支持，但会触发同步）。不接受 `GL_RGBA`+`GL_UNSIGNED_BYTE` 的转换读法
  —— 那会把 HDR 音频压成 8bit，直接失真。回读失败时打印 `glGetError` 与内部格式，别猜。

### S0-5 抽公共库会牵动 ProjectM 的两处"隐式约定"（待验证）

- **现象**：`MilkDrop.ProjectM` 里 `Rendering/`（EGL/上下文）与 `Renderer/`（GL 封装 + projectM 专用件）两个目录语义重叠，
  而且 `GlContext.Gl` 是**静态单例**——同一个进程里只能有一个 GL 上下文，将来 ShaderToy 与 ProjectM 若要在同一进程共存会互相踩。
- **对策**：
  - 抽库时按"通用（进公共库）/ projectM 专用（留下）"划线，别把 `TextureManager`、`MilkdropNoise` 带进去；
  - `GlContext` 保留静态入口（单进程单上下文，够用），但公共库不要有第二个全局状态；
  - 抽完立刻跑 ProjectM 的基准回归（`--smoke` 5 个预设 + `engine` 单帧耗时 ±2% + `--bench 40`），单独提交，便于回滚。

---

## S-P2 转译（Shadertoy GLSL → GLSL ES 3.00）

### S2-1 JSON 里的 `code` 是"字面 `\n`"（已确认）

- **现象**：从 `default-shader.json` 读出来的代码是一整行，含大量 `\n` 两个字符。
- **依据**：上游在 `load_shadertoy_json` 里对 `common_code`、`pass_file.contents` 都调了 `replace_all(..., "\\n", "\n")`（`viewport.cpp:979`、`1168`）。
- **对策**：解析后立即做一次 `\n` → 换行的反转义；同时**保留**原 JSON 里的 `\\n`（两个反斜杠）不误伤。

### S2-2 `#version`/`#extension`/`precision` 的注入位置（待验证）

- **现象**：ES3 要求 `#version 300 es` 在最前面；片段着色器没有默认 `highp`，浮点默认 `mediump`。
- **对策**：固定模板
  ```
  #version 300 es
  precision highp float;
  precision highp int;
  precision highp sampler2D;   // sampler 也要给精度
  ... 用户代码 ...
  ```
  剥离用户代码里的 `#version` 与 `#extension`（`GL_EXT_shader_texture_lod`、`GL_ARB_gpu_shader5` 等在 ES3 上非法或不需要）。
- **注意**：`mediump` 会让 ray marching / hash 噪声出现严重色带，`highp` 必须显式写。

### S2-3 `iChannelN` 必须按真实类型声明（已确认）

- **现象**：上游把所有通道都 `#define` 成 `CombinedImageSampler2D`，再按 JSON 类型覆盖；
  cubemap 写成 2D 采样器会编译失败。
- **依据**：`viewport.cpp:1029-1047` 的 `type_json_to_glsl_image_type()`：`buffer/keyboard/texture → CombinedImageSampler2D`、
  `cubemap → Cube`、`volume → 3D`。
- **对策**：我们直接声明真 uniform（`sampler2D` / `samplerCube` / `sampler3D`），类型来自 JSON 的 `inputs[].type`。
  **同一个 pass 里四个通道的类型可以各不相同**，编译前一次性拼好声明块。

### S2-4 代码用了 `iChannelN` 但 JSON 里没有对应 input（待验证）

- **现象**：一些导出的 JSON 里 `inputs` 少于代码里实际使用的通道数。
- **依据**：上游遇到不认识的类型直接 `continue`（`viewport.cpp:1093`），声明仍保留，运行时绑到空/黑图。
- **对策**：默认给四个通道都声明 `sampler2D`，未绑定时绑 1×1 全黑纹理；
  只有 JSON 明确写了 `cubemap`/`volume` 才换成对应类型。

### S2-5 旧语法兼容（待验证）

- **现象**：老作品用 WebGL1 风格：`gl_FragColor`、`texture2D`、`varying`、`attribute`、`iGlobalTime`。
- **对策**：注入兼容宏，且**只在对应用户代码里出现时**才加（避免污染）：
  ```
  #define texture2D texture
  #define textureCube texture
  #define iGlobalTime iTime
  ```
  `gl_FragColor` 改成我们自己的输出变量（内部名用 `ds_fragColor`，避免与用户变量撞名）。

### S2-6 包裹 `main()` 的写法（待验证）

- **现象**：Shadertoy 只有 `void mainImage(out vec4 fragColor, in vec2 fragCoord)`，没有 `main()`；但少数作品自己写了 `main()`。
- **对策**：检测到用户代码里已有 `void main(` 就跳过包裹；否则追加
  ```glsl
  layout(location = 0) out vec4 ds_fragColor;
  void main() { vec4 c = vec4(0.0); mainImage(c, gl_FragCoord.xy); ds_fragColor = c; }
  ```
  cubemap pass 的入口签名不同（`mainCubemap(out vec4, in vec3, in vec3, float, int)`），要按 `pass_type` 分支。

### S2-7 ES3 的语法收紧点（待验证，逐条加单测）

- 不支持隐式 `int → float` 转换（`float x = 1;` 报错，需 `1.0`）；
- `for` 循环条件必须能用常量上界分析出上限（ANGLE 会展开），大的 while/递归直接编译失败；
- `switch` 的 `case` 必须是常量表达式；
- 不支持 `texture2DLod`（旧名）、`texelFetch` 的 offset 变体在 ES3.0 不支持（ES3.1 才有）；
- 结构体里放 sampler 有限制，`sampler2D arr[2]` 索引必须是常量表达式（ES3.0）。
- **对策**：P2 的回归集里专门放一批"已知会失败"的样本，失败信息进本文件；不要指望一次性全绿。

### S2-8 ANGLE 编译大 shader 很慢（待验证）

- **现象**：几千行的 noise/raymarch 作品，ANGLE 转译到 HLSL 可能几百毫秒，切 pass 时掉帧。
- **对策**：编译放在 UI 线程之外（或至少显示"编译中"）；按 pass 缓存 program，只在代码/通道类型变化时重编。

---

## S-P3 / S-P4 渲染与 pass 图

### S3-1 同一纹理读写是非法操作（已确认）

- **现象**：buffer pass 天然会"读上一帧的自己"（`texture(iChannel0, uv)` 且 `iChannel0` 指向自己）。
- **依据**：上游用 `PingPongImage`（`ping_pong_resource.hpp`，`history_resource` / `output_resource` 交替）。
- **对策**：每个 buffer pass 两张 `RGBA32F` 纹理 + 两个 FBO，写完交换；`iChannel` 指向**写之前**那张。
  在 GL 里硬读同一张纹理不是"结果随机"，而是直接触发未定义行为（常见表现：全黑、闪烁、驱动报错）。

### S3-2 mipmap 必须在写完这一帧之后生成（待验证）

- **现象**：给正在被采样的纹理生成 mipmap 会打断渲染。
- **依据**：上游在 `record()` 末尾统一处理 `pass.needs_mipmap`（`viewport.cpp:603-610`）。
- **对策**：所有 pass 跑完再统一生成；生成时先把 FBO/纹理从采样槽上解绑。

### S3-3 buffer 分辨率三种口径，必须可配（已确认 → 已定方案）

- **现象**：网页版 Shadertoy 的 buffer 固定 `1024×256`；上游用的是视口分辨率（`viewport.cpp:290`）。
  两边对同一作品可能画出不同结果 —— 凡是用 `iChannelResolution` 或把像素坐标当 hash 输入的作品都会偏。
- **对策**：做成三模式可配置（工程 JSON 存一份，命令行/UI 可覆盖）：
  `viewport`（默认，跟随视野）/ `fixed`（默认 `1024x256`，对齐网页）/ `scale`（视口 × 系数，省填充率）。
  - `iResolution` 与 `iChannelResolution[i]` 必须回填**实际使用的 buffer 尺寸**，不能一律填视口尺寸；
  - 切模式走"重建 + 保留内容"的路径（同 resize），否则会闪一帧黑；
  - 对比报告里两种模式都跑一遍，`fixed` 才是与网页对齐的基准。
- **待验证**：`fixed` 模式下把 buffer 从视口尺寸缩小到 1024×256 后，mipmap 链/采样密度变化对画面的影响有多大。

### S3-4 图片上传的 `vflip`（待验证）

- **现象**：Shadertoy 的 `inputs[].sampler.vflip` / 老字段 `vflip: "true"` 决定图片是否上下翻转；
  不处理会出现"图是倒的"。
- **依据**：`viewport.cpp` 的 sampler 选择里带了独立的 vflip 变体（9 种 sampler 组合 = filter × wrap × vflip）。
- **对策**：上传时按 `vflip` 决定行序；**FBO 纹理不需要翻**（GL 纹理原点本来就在左下，与 `gl_FragCoord` 一致）。

### S3-5 `glGetError` 不能每帧无脑调（待验证）

- **现象**：每个 pass 后查错会把驱动同步化，帧率腰斩。
- **对策**：`--gl-check` 调试开关；正常路径只在"资源创建 / shader 编译 / FBO 完整性"时查。

---

## S-P5 输入通道

### S5-1 键盘纹理的编码与键位映射（待验证）

- **现象**：Shadertoy 的 keyboard 通道是 256×3 的 RGBA8 纹理，按 **浏览器 `keyCode`** 置位；
  我们手上是 GLFW 的 `Keys` 枚举，两套编号完全不同。
- **依据**：上游固定建 `256×3` 纹理（`viewport.cpp:304`、`338`）。
- **对策**：写一张 `GLFW Keys → keyCode` 映射表（只覆盖常用键），按 keyCode 写像素；
  用一个"按下 A 变红"的自测 shader 验证，别靠猜。

### S5-2 cubemap 面序在不同来源下可能相反（待验证）

- **现象**：Shadertoy 的 cubemap 资源是 `+X,-X,+Y,-Y,+Z,-Z`；从 DDS/其他工具导出可能是 D3D 顺序。
- **对策**：加载时提供 `--cube-flip-y` 之类的开关，先用一个带方向标记的测试 cube 确认。

### S5-3 `volume` 通道依赖 shadertoy 站点资源（已确认）

- **现象**：上游的 volume 输入靠 `input["id"]` 从 shadertoy 的 volume 资源库加载（`viewport.cpp:1112-1114`），离线时拿不到。
- **对策**：离线导入时缺失 → 绑 1×1×1 黑色 `sampler3D`，并在 UI 里标黄提示"该通道需要联网资源"。

### S5-4 通道 sampler 的 9 种组合（已确认）

- **现象**：filter（nearest / linear / mipmap）× wrap（clamp / repeat）× vflip = 12 种，上游只建了其中一部分。
- **依据**：`get_sampler()` 用 `filter + wrap*3` 索引（`viewport.cpp:1069`），并且把 `mipmap` 强行降级成 `linear`（`1104-1109`，注释写着 `TEMPORARY HACK`）。
- **对策**：GL 里不需要预先建 sampler 组合 —— 直接 `glSamplerParameter` 动态设（`Sampler.cs` 已有），省掉这张表。

---

## S-P6 Sound pass 与音频（上游完全没有，纯新增）

### S6-1 `mainSound` 有两种签名，必须先探测（语义已确认 / 覆盖面待验证）

- **现象**：网页端现行签名是 `vec2 mainSound(in float time)` —— 参数就是**当前时刻（秒）**，浏览器对每个采样点传
  `time = 采样序号 / 44100.0`，返回这一刻左右声道的幅度。典型实现是一句纯数学：
  `return vec2(sin(6.2831 * 440.0 * time));`（A4 = 440Hz，2π ≈ 6.2831）。
  早期/常见导出则是 `vec2 mainSound(int samp, float time)`，多给一个**绝对采样序号**，方便写按索引的算法。
  GLSL 没有重载解析，wrapper 只能按实际签名生成 —— 猜错就是编译失败或采样错位。
- **依据**：Shadertoy 官方 Sound pass 语义；仓库示例 `res/shadertoy/sound.txt` **第 84 行实测为
  `vec2 mainSound(int s, float t)`**（第二种签名），说明两种都真实存在。
- **对策**：编译前扫 `mainSound\s*\(([^)]*)\)` 判参数表：
  - 单参数（`float time`）→ `vec2 v = mainSound(uSampleTime);`
  - 双参数（`int samp, float time`）→ `vec2 v = mainSound(int(uSampleIndex), uSampleTime);`
  - 形参带 `in`/`highp` 修饰、写成多行都要能匹配，别按固定字符串比对；
  - 回读后逐样本 `clamp(-1.0, 1.0)`（网页端同样限幅，超范围会爆音）。
- **补充**：浏览器是"按缓冲块批量填，再整段交给声卡"，不是逐样本 draw call —— 所以我们批量渲染 + 回读是同一个思路，
  仅批次大小不同（见 S6-2）。

### S6-2 每帧产出量远大于一帧该播的量（待验证）

- **现象**：60 FPS 时每帧只该产出 735 个采样，但 GL 一次至少要渲染一整行纹理。
- **对策**：`512×2` 的 `RGBA32F` 纹理一次出 512×2 = 1024 个采样（左 512 + 右 512？不，是同一行两个采样点），
  实测后确定每帧跑 2 次（≈2048 采样，约 46ms 音频）；多余部分推进环形缓冲，欠载时补渲。
  缓冲目标：3~4 段 × 20ms，避免爆音。

### S6-3 `glReadPixels` 的同步代价（待验证）

- **现象**：每帧回读会插入 GPU 同步点，帧率抖动。
- **对策**：先做同步回读（简单、正确），测出开销后再考虑 `PBO` 双缓冲异步回读。

### S6-4 设备采样率 ≠ 44100（待验证）

- **现象**：多数声卡是 48000，直接按 44100 喂会变调/变速。
- **对策**：**按 44100 渲染 + 线性重采样到设备采样率**（保音高）；不要按设备采样率改 `time` 公式。

### S6-5 音频时钟与画面时钟不同源（待验证）

- **现象**：`iTime` 走墙钟，sound pass 走采样计数，长时间播放会漂移（声音比画面快/慢）。
- **对策**：P6 先用墙钟；P9 评估"用音频已播放采样数反推 `iTime`"（会让 `--fixed-dt` 失效，需要开关）。

### S6-6 AOT 下 NAudio 的 WASAPI 直接崩（已确认）

- **现象**：`MMDeviceEnumeratorComObject..ctor()` 在 ILC 下被编译成必定抛异常的桩，`InvalidProgram_Specific`。
- **依据**：见 `projectMIssue.md` 与 README 的"已知限制"。
- **对策**：`IAudioSink` 抽象：JIT 用 `WaveOutEvent`（winmm，托管封装，AOT 也基本安全），AOT 再不行就静音并提示。

### S6-7 音频通道纹理（music / musicstream）的格式（待验证）

- **现象**：Shadertoy 的音频输入是 512×2 的纹理：第 0 行 = 频谱（对数频率轴），第 1 行 = 波形；`r` = 左、`g` = 右。
- **对策**：用 `MilkdropFft`（已移植）算 512 bin 的对数频谱 + 波形，每帧上传 512×2 RGBA8（8 KB，微不足道）。
  sound pass 自产音频时，把它同时喂给这个通道，形成"最简合成器 + 可视化"的闭环。

---

## S-P7 / S-P9 界面与 AOT

### S7-1 ImGui 多行文本编辑的性能陷阱（待验证）

- **现象**：`InputTextMultiline` 每次调用都要把 `string` 封送成原生缓冲，几千行的 shader 每帧都封送会卡。
- **对策**：用 `byte[]` + `GCHandle`/`NativeString` 常驻缓冲，只在 `IsItemDeactivatedAfterEdit` 时把内容同步回 `string` 并触发重编译。

### S7-2 `System.Text.Json` 在 AOT 下必须用源生成（已确认）

- **现象**：反射式 `JsonSerializer.Deserialize<T>` 在裁剪后会抛 `NotSupportedException`。
- **对策**：`[JsonSerializable(typeof(ShaderToyProject))]` + `JsonSerializerContext`；`JsonDocument` 走 DOM 解析也可以（不需要元数据）。

### S7-3 图片解码库要选纯托管（待验证）

- **现象**：ImageSharp 的某些路径用反射/动态代码，AOT 下需要额外配置。
- **对策**：优先 `StbImageSharp`（纯托管、无反射），PNG/JPG 足够；需要 DDS/HDR 再单独加。

### S7-4 JSON 里的非法字符（待验证）

- **现象**：老作品的 `code` 里可能混入非 UTF-8 字节或裸控制字符，`JsonDocument.Parse` 直接抛。
- **对策**：读文件时按 `UTF8` 严格解，失败则回退 `Latin1` 并记日志；解析失败时在 UI 里显示原始报错位置。

---

## S-P8 本地库与下载

### S8-1 页面里的 JSON 是内嵌 JS，不是接口返回（待验证）

- **现象**：没有 API key 时，`https://www.shadertoy.com/view/<id>` 的 HTML 里把工程塞在脚本变量中
  （形如 `var gShaderToy = {...};`），不是干净的 JSON 响应，还可能带 `&quot;` 之类转义。
- **对策**：定位 `gShaderToy` 起点后做**括号配对**（考虑字符串与转义）截取，不要用"匹配到 `</script>`"这种硬切；
  截出来的文本先做 HTML 实体反转义再丢给 `JsonDocument.Parse`。解析失败时把原始片段落盘到 `shaders/_failed/` 便于排查。

### S8-2 资源 URL 必须在保存时改写成相对路径（已确认必要性）

- **现象**：Shadertoy 的 `inputs[].src` / `filepath` 是 `https://.../media/...` 绝对地址。
  原样存下来，下次离线加载就会去联网（失败）或拿到 404。
- **对策**：下载时把资源落到 `textures/`，同时把 JSON 里对应字段改成 `textures/xxx.jpg`；
  相对路径的基准固定为"作品目录"，这样整个目录可以整体拷走。
- 注意同一张图被多个 input 引用时**只下载一次**（按 URL 去重）。

### S8-3 `project.json` 必须原样保存（待验证）

- **现象**：如果按我们的模型反序列化再序列化写回，会丢未知字段、改字段顺序，还可能把 `\n` 转义处理错
  —— 再导入时变成"双重反转义"，代码里出现多余空行或 `\n` 字面量。
- **对策**：**保存原始文本**（只改写资源路径那几处，做最小侵入的文本替换或用 DOM 保留原节点），
  私有信息一律写 `meta.json`，让 `project.json` 保持"可直接导回 shadertoy"。

### S8-4 下载要带正常 UA，且要限速/重试（待验证）

- **现象**：CDN 对空 UA / 高频请求可能返回 403 或 429。
- **对策**：`HttpClient` 设 `User-Agent`、`Referer`；单作品下载并发 ≤4，遇 429 指数退避重试 2 次；
  单个资源失败只记日志、不中断（缺资源标记进 `meta.json`）。

### S8-5 目录名只能用 ID（已确认必要性）

- **现象**：作品标题里常有 `:` `?` `*` `/`，直接当目录名在 Windows 上非法；长标题还会顶到 260 字符限制。
  另外 NTFS 大小写不敏感，`flGyWc` 与 `flgywc` 会指向同一目录。
- **对策**：目录名固定为小写化的 ID；标题只存在 `meta.json` 与 UI 里；解析输入时 ID 大小写不敏感地回去找目录。

### S8-6 本地库扫描要容忍"手工放进去的作品"（待验证）

- **现象**：用户可能只拷了一个 `project.json` + `textures/` 进 `shaders/`，没有 `meta.json`，也没有缩略图。
- **对策**：扫描以 `project.json` 存在为准；`meta.json` 缺失时现场用默认值（标题取目录名、作者未知）并**不回写**；
  缩略图缺失就不显示图，不要生成占位文件污染库目录。
