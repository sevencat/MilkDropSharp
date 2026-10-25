# ShaderToy 播放器踩坑记录

> 每条格式：现象 → 依据 → 结论/对策。计划见 `shaderToyPlan.md`。
>
> **状态：P0–P8 已落地**（导入、转译、image 渲染、pass 图、输入通道、sound pass 与自产音频、ImGui 界面、
> 本地库、下载、本地 JSON 导入、代理配置与"手工补资源"；`tests/MilkDrop.ShaderToy.Tests` 159 个用例全绿，
> 其中 P8 新增 56 个）。
> 未做：cube/volume **资源的绑定**（文件能下载，采样仍走占位）、resize 内容保留、AOT 发布（P9）。
> 另外：本站真实下载需要登录/浏览器指纹，curl 与 .NET 客户端（含走 socks5）都被 Cloudflare 403 拦，
> 故自动下载暂时搁置，改用手工放 `textures/` + "补齐"（S8-8 / S8-11）。
> 标注 `已确认` 的来自上游源码、本仓库既有结论或已实测；标注 `待验证` 的是预判，
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

## S-P1 导入与真实导出（踩坑记录）

> 样本目录：`res/shadertoy/stpreset/*.json`。往这里丢新作品，`RealExportTests`（MemberData 动态枚举）
> 会自动把"能导入 + image pass 能编译"纳入回归，不需要改代码。

### S1-1 根节点是数组 `[{ ... }]`，不是对象（已确认）

- **现象**：从 Shadertoy API / 页面拿到的 JSON 最外层是 `[ { "ver": "0.1", ... } ]`；
  站点"导出"按钮给的是单个对象。按对象写死解析会直接报"根节点必须是对象"。
- **对策**：`ParseRoot` 递归剥数组（多于一个元素时记 warning 并取第一个）。

### S1-2 复制粘贴会把 JSON 弄坏，且坏法有好几种（已确认，真实样本）

- **现象**：`flGyWc.json` / `73ySRh.json` 严格解析直接失败，坏法如下：
  1. 字符串里出现**裸 CR/LF**（长 GLSL 行被硬换行）→ `'0x0D' is invalid within a JSON string`
  2. `\/` 被拆成 `\ /`（5 处）→ `'\' is not a valid escape`
  3. 两字符运算符被拆成 `> =` / `< =`（`73ySRh` 的第 88 行因此编译失败）
  4. 键名被写成 `" name"` / `" type"` / `" inputs"`（引号内多一个空格）
  5. 值带前导空格：`"type": " image"`、`"name": " Image"`
- **对策**（只在严格解析失败时才动手，全部记 warning）：
  - 裸换行按上下文修复：**预处理器指令行（`#define`）内折成空格**（否则宏被拦腰截断，
    这是 `73ySRh`/`flGyWc` 第一次跑编译报 `'<' : syntax error` 的原因），其余折成 `\n`；
  - `\` + 空白 + 字符 → 重新粘合（`\ /` → `\/`）；
  - `[<>=!&|+*/%-]` + 空白 + `[=&|]` → 合并成两字符运算符（**不含 `+ +` / `- -`**，
    那两个分开写是合法代码）；
  - 属性查找退化为"键名 Trim + 忽略大小写"；`type`/`name` 比较前 Trim。
- **代价**：这些都是启发式修复，宁可多留 warning 也不静默吞掉。彻底坏掉的文件仍会抛原始 `JsonException`。

### S1-3 `common` pass 不一定排在最前面（已确认）

- **现象**：`73ySRh.json` 的 pass 顺序是 `Image → Common → Buffer A`，而 `flGyWc` 是 `Image → Sound`（无 common）。
- **对策**：`ShaderToyProject.BuildCommonCode()` 扫全部 pass 收集 `common`，不假设位置；
  但**执行顺序**（P4）必须严格用 JSON 里的顺序，不能重排。

## S-P2 转译（Shadertoy GLSL → GLSL ES 3.00）

> **P2 实测结果**（ANGLE/D3D11，GLES3）：`tests/MilkDrop.ShaderToy.Tests/ShaderCorpus.cs` 的 14 段语料
> （raymarch / hash 噪声 / buffer 自反馈 / cubemap / keyboard / 旧 WebGL1 / struct+数组 / 全屏 sample 变体）
> 与上游 `default-shader.json` 全部编译通过；`iResolution`、`iTime` 在链接后的 program 里 location 有效。
> 实现见 `src/MilkDrop.ShaderToy/Compile/ShaderToyTranspiler.cs`。

### S2-1 JSON 里的 `code` 是"字面 `\n`"（已确认）

- **现象**：从 `default-shader.json` 读出来的代码是一整行，含大量 `\n` 两个字符。
- **依据**：上游在 `load_shadertoy_json` 里对 `common_code`、`pass_file.contents` 都调了 `replace_all(..., "\\n", "\n")`（`viewport.cpp:979`、`1168`）。
- **对策**：解析后立即做一次 `\n` → 换行的反转义；同时**保留**原 JSON 里的 `\\n`（两个反斜杠）不误伤。

### S2-2 `#version`/`#extension`/`precision` 的注入位置（已确认，P2 实测）

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
- **实测**：`#version 300 es` 确实必须第一行（前面有空行/注释也会报错）；`precision highp sampler2D/samplerCube/sampler3D;`
  在 ANGLE 上合法；用户代码里自带 `precision mediump float;` 可以保留（GLSL 允许多次设置默认精度，后者覆盖前者，
  所以模板里的 `highp` 要写在用户代码**之前**才有效）。

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
- **实测（P2，ANGLE 的 ESSL3 前端）**：
  - 隐式 `int → float` **确实不支持**：`float x = 1;` 报 `'=' : cannot convert from 'const int' to 'highp float'`，
    `2 + x` 报 `no operation '+' exists`。所以 `ShaderToyTranspiler` 不做隐式转换补救（网页端 WebGL2 同样是 ANGLE，
    能跑的作品本来就不会这么写）。
  - 能通过：`for` 常量上界、`while (t < 10.0 && steps < 48)`、`switch` 整型 + `case` 常量、全局 `const mat2`、
    全局数组、`texture(sampler2D, vec2, float bias)`（片段里合法）、`textureLod`、`texelFetch`、`dFdx`。
  - 未验证：`texelFetch` 的 offset 变体、`sampler2D arr[2]` 的动态索引、超大 while/递归。

### S2-8 ANGLE 编译大 shader 很慢（待验证）

- **现象**：几千行的 noise/raymarch 作品，ANGLE 转译到 HLSL 可能几百毫秒，切 pass 时掉帧。
- **对策**：编译放在 UI 线程之外（或至少显示"编译中"）；按 pass 缓存 program，只在代码/通道类型变化时重编。

---

## S-P3 / S-P4 渲染与 pass 图

> **P3 实测结果**：`ImagePassRendererTests` 在 ANGLE 上做了"渲染 → 拷到 RGBA8 → `glReadPixels` → 逐像素断言"，
> 证明四件事同时成立：`iResolution` 等 uniform 真的推到了（否则画面为 0）、视口尺寸对、**Y 轴没翻**
> （左下暗右上亮，翻过就互换）、`SetSize` 重建目标后还能继续画。App 用上游 `default-shader.json`
> 连跑 6 秒无异常，目标格式 `RGBA16F`（本机 `EXT_color_buffer_float` / `_half_float` 都在）。
> 计划里 P3 的验收物 flGyWc 截图对比做不了（仓库没有该作品资源），换成上面这组像素断言。

### S3-6 上游的 `GpuInput` 只填了 6 个字段（已确认）

- **现象**：上游声明了 `iDate / iChannelResolution[4] / iSampleRate`，但整个 `viewport.cpp` 只写
  `Resolution / Time / TimeDelta / FrameRate / Frame / Mouse`（grep `gpu_input\.` 只有这 6 个名字）。
- **后果**：用 `iChannelResolution` 做 texelFetch 定位的作品在上游会拿到 0，画面直接不对。
- **对策**：本项目的 `GpuInput` 全部填充；`iDate` 用 `(年, 月-1, 日, 当天秒数)`（月份与浏览器
  `Date.getMonth()` 一致，0 起），`iSampleRate` 固定 44100。

### S3-7 注释里的关键字会误伤整串替换（已确认，P3 踩到）

- **现象**：上游 `default-shader.json` 的代码里有一句注释 `// Time varying pixel color`。
  最初用 `\bvarying\b` 正则做 `varying → in`，把注释改成了 `// Time in pixel color`，
  还记了一条假的"转译提示"。同类问题更危险的是 `// void main() {}` —— 会让"用户已自定义 main，
  跳过包裹"的判断误命中，最终 shader 没有入口点。
- **对策**：`ShaderToyTranspiler` 改成**词法扫描**：跳过 `//`、`/* */` 注释后按标识符匹配；
  所有"代码里有没有 X"的判断都在"注释换成空格（保留换行）"的副本上做。已有测试
  `Transpile_LeavesKeywordsInsideCommentsAlone` 覆盖。

### S3-8 `iMouse` 的松开语义，网页端是"取负"（已确认）

- **现象**：上游 `on_mouse_button()` 松开时直接写 `zw = (-1, -1)`（`viewport.cpp:655-659`），
  丢掉了"在哪里松开"，与网页端不一致。
- **依据**：Shadertoy 端 `iMouse.zw` 在按住时是点击位置（正值），松开后**负的那个点击位置**；
  作品普遍用 `iMouse.z > 0` 判断"正在拖拽"。
- **对策**：`Input/MouseState` 按网页端实现（松开 → `zw = -clickPos`），`xy` 只在按住时跟随。

> **P4/P5 实测结果**：`PipelineTests`（渲染 → 拷到 RGBA8 → 回读像素）验证了 buffer→image 通道、
> ping-pong 累加（0.1/0.2/0.3 逐帧增长）、`iResolution`/`iChannelResolution` 回填（fixed 64x16 → 0.25/0.0625）、
> 键盘通道按下/松开；`TextureChannelTests` 用 2x2 BMP 验证 `vflip` 两个方向都正确。
> 真实作品 `73ySRh`（Image + Common + Buffer A）在管线里跑 3 帧出图（最大通道值 255，非全黑）。

### S4-1 buffer 必须 ping-pong，且交换时机决定语义（已确认，P4 实现）

- **现象**：同一张纹理既当采样源又当渲染目标是 `GL_INVALID_OPERATION`（见 S3-1）。
- **实现**：`PassTarget` 两张 RGBA16F + 两个 FBO；**画完一个 pass 立刻交换**。
  这样"后面的 pass 读本帧结果、自己读自己是上一帧"两个语义同时成立 —— 交换放在画之前就会读到两帧前。
- **验证**：自己读自己累加 0.1，逐帧 0.1 / 0.2 / 0.3，交换顺序写错会立刻露馅。

### S4-2 buffer 分辨率：网页端三种，上游只有一种（已确认）

- **现象**：上游 `viewport.cpp:559` 直接拿视口当 buffer 尺寸，所以 fluid 类作品（网页端默认 1024x256）
  在桌面端跑出来"更糊/更慢"，与网页对不上。
- **实现**：`BufferResolution` 三模式（viewport / fixed 1024x256 / scale），`iChannelResolution[i]` 按实际尺寸回填。
- **未做**：切模式/resize 时**不保留** buffer 内容（会闪一下）。计划里要求的"重建 + 保留内容"欠账，P9 补。

### S4-3 buffer 目标用 RGBA16F 而非 RGBA32F（偏差，已实测）

- **现象**：`shaderToyIssue.md` S0-2 原本定 RGBA32F。但 ES3 核心**不保证 RGBA32F 可线性过滤**
  （要 `OES_texture_float_linear`），而 Shadertoy 的 buffer 普遍用 linear 采样；一旦驱动不给，
  画面会变成 nearest 甚至采不到。
- **实现**：统一 RGBA16F（本机 `EXT_color_buffer_half_float` 在，可渲染 + 可过滤），`Notes` 里不静默。
- **以后**：探测到 `OES_texture_float_linear` 再切 RGBA32F。

### S4-4 mipmap：GLES3 一行搞定，mag filter 不能设 mipmap（已确认）

- **现象**：上游在 Vulkan 里只能逐级 blit 出 9 级（`MAX_MIP = 9`、`max_lod = 8`）。
- **实现**：`glGenerateMipmap` + `TEXTURE_MAX_LEVEL = 8` 即可，效果与网页端一致（box filter）。
- **坑**：`glSamplerParameter(TEXTURE_MAG_FILTER, LINEAR_MIPMAP_LINEAR)` 是 `GL_INVALID_ENUM` ——
  mag 只接受 NEAREST/LINEAR，所以 mipmap 通道要"先建线性采样器，再单独改 min filter"。

### S5-1 texture 通道：vflip 必须自己翻行（已确认）

- **现象**：上游用 `stbi_set_flip_vertically_on_load(1)`；StbImageSharp 的 flip 是全局状态，
  多次/并发加载会互相影响。
- **实现**：`TextureLoader` 解码后按行翻转（`FlipRows`），用 2x2 BMP 双向验证过。
- **路径**：Shadertoy 的 `filepath` 是 `/media/...` 站点路径，本地只能按**文件名**在搜索目录里找；
  找不到就绑黑色占位并在 UI 提示（P8 下载后自动命中）。

### S5-2 keyboard 通道：3 行 × 256 列，按浏览器 keyCode 索引（已确认）

- **约定**：行 0 = 按住，行 1 = 本帧刚按下，行 2 = 本帧刚松开；x 轴就是 keyCode。
- **上游问题**：直接写 GLFW 键值，搬回网页端对不上。
- **实现**：`KeyboardKeyMap`（GLFW Key → 浏览器 keyCode），`KeyboardChannel` 负责边沿行与上传。

### S5-3 占位纹理必须匹配 sampler 类型（已确认）

- 给 `samplerCube` 绑 1x1 的 2D 纹理是 `GL_INVALID_OPERATION`，**不是**"采到黑色"。
- 所以 2D / cube / 3D 三种占位都要有，且渲染前按"JSON 声明的类型"先铺一遍，
  否则未绑定通道的采样结果是未定义的（驱动相关）。
- 规则集中在 `ShaderToyTranspiler.ResolveChannelTypes()`，shader 声明与渲染绑定共用同一份。

### S5-4 还有被换行拆开的运算符（已确认，真实文件）

- **现象**：`73ySRh` 的 Buffer A 里有 `z>` + 换行 + `=-2.6`，编译报 `0:57: '=' : syntax error`。
- **为什么之前的修复没管**：JSON 层的"运算符合并"跑在转义前，跨行的情况此时已经是 `\n` 转义，看不到。
- **对策**：改到 `ParsePass` 里、`UnescapeSource` **之后**合并（间隙允许 `\r\n`），并记 warning。
  顺带把驱动报的行号对应源码贴进 `Notes`，否则只有 "0:57" 根本没法定位。

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

### S6-1 `mainSound` 有两种签名，必须先探测（已确认，P6 实测）

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
- **实测**（P6，ANGLE/D3D11）：探测与包装在 `Compile/MainSoundEntry.cs`，扫的是"去掉注释后的代码"
  （同 S3-7，注释里的 `// vec2 mainSound(...)` 不会被误认）。
  - `res/shadertoy/sound.txt`（真实作品 lunchbreak，`vec2 mainSound(int s, float t)`）编译通过并出声，
    单批峰值为 0.2609；
  - 440 Hz 最简正弦（`vec2 mainSound(in float time)`）在 1024 个采样里测得 **21 次过零** ≈ 10.5 个周期
    （理论 10.2），左右声道完全一致 → 频率/声道映射都对；
  - `(int s, float t)` 用 `float(s)/44100.0` 断言了**绝对样本序号**（第二批的 s 正好是 1024），
    不是"每批从 0 开始"这种常见写错；
  - 形参顺序反过来（`(float t, int samp)`）、带 `in highp`、跨行都能识别。
  - 覆盖测试：`tests/.../Audio/MainSoundEntryTests.cs`、`SoundPassRendererTests.cs`。

### S6-2 每帧产出量远大于一帧该播的量（已实测，按水位补渲染）

- **现象**：60 FPS 时每帧只该产出 735 个采样，但 GL 一次至少要渲染一整行纹理。
- **对策**：`512×2` 的 `RGBA32F` 纹理一次出 512×2 = 1024 个采样（左 512 + 右 512？不，是同一行两个采样点），
  实测后确定每帧跑 2 次（≈2048 采样，约 46ms 音频）；多余部分推进环形缓冲，欠载时补渲。
  缓冲目标：3~4 段 × 20ms，避免爆音。
- **实测**：没按"每帧固定 2 批"做，而是**按水位补**（`AudioEngine.Fill`）：目标 200 ms（8820 帧），
  每帧最多补 6 批。`--audio-log` 连跑 12 秒（flGyWc 与 XsBXWt 各一次）：
  缓冲稳定在 **198–222 ms**、**欠载 0 帧**、设备侧每秒正好消费 44100 帧，
  说明"渲染多少 / 播多少"自平衡，不会溢出也不会饿死。
- **启动预填**：不预填时开头欠载 2646 帧（约 60 ms 静音，听感是"咔"一下）。
  `AudioEngine.Start` 现在先 `Prefill()` 到水位再开设备，之后整段日志欠载都是 0（见 S6-9）。

### S6-3 `glReadPixels` 的同步代价（已实测，暂时不需要 PBO）

- **现象**：每帧回读会插入 GPU 同步点，帧率抖动。
- **对策**：先做同步回读（简单、正确），测出开销后再考虑 `PBO` 双缓冲异步回读。
- **实测**（AMD Radeon 780M / ANGLE D3D11）：真实作品 lunchbreak 的 sound pass，
  连跑 60 批（每批 1024 采样、`GL_RGBA/GL_FLOAT` 回读 32 KB）平均 **0.27 ms/批**，
  而一批覆盖 23.2 ms 音频 —— 占用量约 1.2%，且每帧最多 6 批也就是 ~1.6 ms。
  同机长跑 12 秒缓冲水位没有抖动，所以 **PBO 异步回读不做了**（等出现更重的 sound shader 再说）。
  测量点：`SoundPassRendererTests.RenderBatch_SynchronousReadbackCostIsAcceptable`。

### S6-4 设备采样率 ≠ 44100（部分验证）

- **现象**：多数声卡是 48000，直接按 44100 喂会变调/变速。
- **对策**：**按 44100 渲染 + 线性重采样到设备采样率**（保音高）；不要按设备采样率改 `time` 公式。
- **实测**：`AudioBuffer` 实现了保音高的线性重采样（`step = 44100 / deviceRate`，插值在相邻源帧之间），
  单测覆盖 44100 / 22050 两条路径（`AudioBufferTests`）。
  **但我们没去查设备采样率**：`WaveOutAudioSink` 按 44100 开设备，交给系统混音器重采样。
  好处是零重采样抖动、实现最简单；代价是"设备真实采样率"这条路径在真机上没有触发过（只有单测）。
  等 P9 接 AOT/winmm 兜底时再评估是否要用 `waveOutGetDevCaps` 拿真实采样率。

### S6-5 音频时钟与画面时钟不同源（已实现，漂移待长测）

- **现象**：`iTime` 走墙钟，sound pass 走采样计数，长时间播放会漂移（声音比画面快/慢）。
- **对策**：P6 先用墙钟；P9 评估"用音频已播放采样数反推 `iTime`"（会让 `--fixed-dt` 失效，需要开关）。
- **实测**：12 秒内音频采样计数（44100/s）与 `PlaybackClock` 的墙钟时间没有肉眼可见差异
  （`--audio-log` 里"已播"每 2 秒涨 88200 帧，非常稳）。计划里"连续播放 10 分钟无漂移"的验收还没做。

### S6-6 AOT 下 NAudio 的 WASAPI 直接崩（已确认，AOT 未验证）

- **现象**：`MMDeviceEnumeratorComObject..ctor()` 在 ILC 下被编译成必定抛异常的桩，`InvalidProgram_Specific`。
- **依据**：见 `projectMIssue.md` 与 README 的"已知限制"。
- **对策**：`IAudioSink` 抽象：JIT 用 `WaveOutEvent`（winmm，托管封装，AOT 也基本安全），AOT 再不行就静音并提示。
- **实测**：`IAudioSink` 放在库层（`Audio/IAudioSink.cs`），NAudio 只出现在 App 层的 `WaveOutAudioSink`，
  用的是 `WaveOutEvent`（winmm），**没有碰 WASAPI**。JIT 下连跑 12 秒正常出声、无异常。
  `NullAudioSink` 作为"没设备/静音"的兜底。AOT 发布（P9）还没跑，所以"winmm 在 ILC 产物里也能用"仍属待验证。
- **后续（已验证）**：升级到 NAudio 3 后，AOT 产物里 `WaveOut` 正常启动，日志为
  `音频: sound pass 已启用（NAudio WaveOut (winmm, 44100 Hz 16-bit stereo)）`。
  `projectMIssue.md` I-43 的 WASAPI 崩溃也一起解决了（3.x 改用 `[GeneratedComInterface]`）。

### S6-7 音频通道纹理（music / musicstream）的格式（已确认，P6 实测）

- **现象**：Shadertoy 的音频输入是 512×2 的纹理：第 0 行 = 频谱（对数频率轴），第 1 行 = 波形；`r` = 左、`g` = 右。
- **对策**：用 `MilkdropFft`（已移植）算 512 bin 的对数频谱 + 波形，每帧上传 512×2 RGBA8（8 KB，微不足道）。
  sound pass 自产音频时，把它同时喂给这个通道，形成"最简合成器 + 可视化"的闭环。
- **实测**：实现落在 `Audio/MusicTexture.cs`，但**没有引用 projectM 的 `MilkdropFft`**，而是自写了 512 点 FFT，理由有两条：
  1. 三线互不依赖（计划 §8）：引用 `MilkDrop.ProjectM` 会把 projectM 语义整套拖进 ShaderToy；
  2. `MilkdropFft` 出的是**线性**频率轴 + projectM 自己的均衡曲线，而 Shadertoy 的音频纹理要的是
     **对数**频率轴（20 Hz–20 kHz 铺满 512 列），本来就得重做映射，复用的价值只剩"少写 60 行蝶形"。
  代价是这张表不能和原生 projectM 逐像素对齐 —— 但那是 projectM 线的验收目标，与 Shadertoy 无关。
- **采样约定实测**：`v = 0.25` 采到频谱行、`v = 0.75` 采到波形行；波形行静音时正好是 0.5 灰（128），
  所以"绑错成黑占位"这类错误会立刻暴露（`PipelineTests.MusicChannel_BindsTheAudioTexture`）。
- **频谱对齐实测**：满幅 440 Hz 正弦（左=右）→ 波形行实测 min=0 / max=255（正负半周都在）；
  频谱峰值落在第 **220** 列。这不是"算错了"：512 点 FFT 的 bin 宽是 86 Hz，440 Hz 落在第 5 个 bin
  （430.7 Hz），这个 bin 在对数轴上覆盖第 **215–228** 列，整段都是同一个幅度、形成平坦顶部，
  断言取的是"第一个最大值"，所以是 220 而不是理论中心 229。
  幅度按 `/(N*0.25)` 归一化后满幅正弦正好打满 255 —— 显示上会削顶，但比"小音量看不见"更实用。
  加 Hann 窗是为了压 leakage（不加窗时单个正弦会漏到相邻 bin），不是实测对比的结论。
- **性能**：每批上传 4 KB（512×2 RGBA8），两个声道各一次 512 点 FFT（每帧最多 6 批 = 12 次），可忽略。

### S6-8 外部音频文件（`type = music` 指向 .mp3）还没接（已确认缺口）

- **现象**：`XsBXWt`（Fractal Land）的 channel 0 是 `music`，`filepath` 指向站点上的 `.mp3`；网页端会把这首曲子播出来，
  画面跟着它跳。
- **现状**：我们把 `music` / `musicstream` 一律绑到自产音频纹理。作品没有 sound pass 时这张纹理是"静音波形"
  （第 1 行 0.5 灰、第 0 行全黑）—— 不崩、不黑屏，但"跟着音乐动"的效果没有。
- **对策**：P8 下载资源时把音频文件一并落盘，用 NAudio 的 `AudioFileReader` 解码后推进同一个 `AudioBuffer`
  → 同一张 `MusicTexture`。数据流已经通，缺的只是"音源换成文件"。

### S6-9 启动瞬间会欠载 60 ms（已确认 → 已修）

- **现象**：设备一开就按它自己的节奏取数据，但环形缓冲还是空的，于是前 3 段（约 60 ms）全是静音；
  听感上是"咔"一下才开始。
- **实测**：修复前 `--audio-log` 第一行就是"欠载 2646 帧"，之后不再增长（说明只是启动那一下）。
- **对策**：`AudioEngine.Start` 先 `Prefill()` 到 200 ms 水位、再 `sink.Start()`；`Pump()` 仍然受
  `MaxBatchesPerPump` 限制（不能因为一次卡顿就把整帧时间吃光）。
- **复测**：同样 12 秒，欠载 **0 帧**，缓冲稳定在 198–222 ms。

### S6-11 sound pass 自己的 `iChannelN` 也要绑（已确认）

- **现象**：sound pass 里也能写 `texture(iChannel0, ...)`。常见的是读 `music`（把音频做成"反馈式"合成），
  少数作品读 buffer A（拿上一帧画面做数据驱动的声音）。
- **坑**：`shaderToyPlan.md` 里 P6 只说了"渲染 mainSound"，没提通道；不绑的话 sampler 指向纹理 0，
  采样结果未定义（驱动之间表现不同，可能全黑也可能报警告）。
- **现状**：`SoundPassRenderer` 按 `<see cref="ShaderToyTranspiler.ResolveChannelTypes"/>` 的声明类型铺占位
  （2D / cube / 3D），`music` / `musicstream` 绑真实的音频纹理；`buffer` 引用的通道暂不绑，并在 `Notes` 里写一条。
  覆盖测试：`SoundPassRendererTests.MusicChannelIsBoundForSoundPass`（读波形行得到 0.5 灰）。

### S6-10 sound pass 的目标格式是 RGBA32F，与 pass 图不同（已确认）

- **现象**：pass 图统一用 RGBA16F（S4-3，为了可线性过滤），sound 却要 RGBA32F。
- **理由**：16F 的尾数只有 10 位，-60 dB 以下基本全是量化噪声，做音频会听到底噪；
  而 sound 目标**只当渲染目标 + 回读**、从不被采样，所以 S4-3 那个"RGBA32F 不一定能过滤"的顾虑在这里不存在。
- **实测**：本机 `EXT_color_buffer_float` 在，`RGBA32F` FBO 完整、`GL_RGBA/GL_FLOAT` 回读正常
  （峰值 0.26 这种小音量信号也没被压成 0）。扩展缺失时 `Notes` 里会写一条，音频退化为静音而不是崩。

---

## S-P7 / S-P9 界面与 AOT

### S7-1 ImGui 多行文本编辑的性能陷阱（已确认，实现与预想不同）

- **现象**：`InputTextMultiline` 每次调用都要把 `string` 封送成原生缓冲，几千行的 shader 每帧都封送会卡。
- **对策**：用 `byte[]` + `GCHandle`/`NativeString` 常驻缓冲，只在 `IsItemDeactivatedAfterEdit` 时把内容同步回 `string` 并触发重编译。
- **实测（坑）**：ImGui.NET 1.90.9.1 **只暴露了 `ref string` 的多行重载**，`InputText` 有 `byte[]`/`IntPtr` 重载，`InputTextMultiline` 没有。
  所以最终是绕到原生入口 `ImGuiNative.igInputTextMultiline(label, buf, size, ...)`（`byte*` + 定长缓冲）。
- **实现**（`App/Ui/PassEditor.cs`）：
  - 容量 = 源码 UTF-8 字节数 × 2 + 16 KB，钉住（`GCHandle.Pinned`），label 也固定成一份 UTF-8；
  - 只有 `IsItemEdited()` 时才扫一次 0 结尾重算长度，`IsItemDeactivatedAfterEdit()` 时把文本写回 `ShaderPass.Code` 并自动重编译；
  - 缓冲将满时编辑器上方显示红字警告（原生入口不能扩容，只能提示）。
- **当时的验收**：`--selfcheck-edits` 把 image pass 换成"整屏品红"，`Player.ApplyEdits()` 返回成功、
  pass 数 1、编译错误 0，`--shot` 出图整屏品红（`artifacts/p7_edit.png`，未入库它已被 .gitignore 忽略）。
- **现状（P8 之后，S7-13）**：界面改为只读，`App/Ui/PassEditor.cs` 已删除；本节留作"以后真要做编辑器"的参考。

### S7-5 默认字体没有中文（已确认 → 已修）

- **现象**：ImGui 默认 `ProggyClean` 只有 ASCII，界面里的中文全变方块（此前几版的 UI 一直如此，只是没人细看）。
- **对策**：`App/Ui/UiFonts.cs` 在 `ImGui.CreateContext()` 之后、渲染器建图集之前挂字体：
  - 中文：`msyh.ttc`（微软雅黑，`GetGlyphRangesChineseSimplifiedCommon()`），设为 `io.FontDefault`；
  - 等宽：`consola.ttf`，并用 `MergeMode` 把中文字形合进来 —— 否则 shader 里的中文注释还是方块；
  - 全都从 `C:\Windows\Fonts` 读，不随包分发；找不到就退回默认字体并打一行警告。
- **实测**：图集从 512×64 涨到 **1024×2048（8 MB）**，仍是一次性上传；中文标签与中文注释都正常显示。

### S7-6 `Screenshot` 读默认帧缓冲会 GL_INVALID_OPERATION（已确认 → 已修）

- **现象**：`--shot` 出来的 PNG 是空白（RGBA 全 0），同时 `glGetError()` = `0x0502`。
- **根因**：`Framebuffer.Unbind()` **只解绑 draw**（与上游 C++ 一致），read framebuffer 还指着最后一个离屏目标（RGBA16F）；
  用 `GL_UNSIGNED_BYTE` 去读 16F 的 FBO → `GL_INVALID_OPERATION`，`ReadPixels` 什么也没写。
- **对策**：`Screenshot.Save` 开头显式 `BindFramebuffer(ReadFramebuffer, 0)` —— 它的契约就是"存默认帧缓冲"，本来就该自己保证。
- **复测**：整窗截图正常（含界面）。这个 bug 在 P4–P6 一直存在，只是没人用过截图功能。

### S7-7 `--shot` / `--selfcheck-reload`：无人值守的界面回归

- **`--shot PATH [--shot-frames N]`**：第 N 帧（默认 30）把**整窗**（shader + 界面）存图后退出。
  用于肉眼检查界面，也可作为 P9 的截图基线。
- **`--selfcheck-reload`**（替代已删除的 `--selfcheck-edits`）：先改掉内存里的 image 代码（不写盘），
  再 `Player.Reload()`；代码必须恢复成文件里的内容 —— 这样能证明"重新加载"确实是从**磁盘**读，
  而不是把内存里的东西再编译一遍。**实测**：`Reload 成功，代码已从文件恢复 = True`。
- **实测**：`--shot-frames 150` 全程 2.3 s（含约 1.6 s 启动），渲染稳定在 **240 FPS** 左右（flGyWc，1280×720，ANGLE/D3D11）。

### S7-8 FPS 统计口径（已确认 → 已修）

- **现象**：界面长期显示 `0.6 FPS`，但实际是 240 FPS。
- **根因**：第一帧把"编译 shader + 上传 8 MB 字体图集"的 1.6 s 算进了统计窗口；之后窗口要再攒满 0.5 s 才刷新，
  低帧率期间看起来就像卡死。同时 Silk 给的 `deltaSeconds` 也不适合当计时基准。
- **对策**：帧率用自建 `Stopwatch` 算，且窗口起点在**加载完成之后**才归零。

### S7-9 本地库扫描（P7 提前做了 P8 的读侧）

- **实现**：`Import/ShaderLibrary.cs`（库层、纯文件 IO、可单测）：
  - 两种布局：`<root>/<id>/project.json`（P8 下载后的标准布局）与 `<root>/*.json`（仓库里的 `res/shadertoy/stpreset`）；
  - `meta.json` 存在时只读它（不解析代码）；不存在才用容忍型导入器完整解析一次取 `info.name/username`；
  - `project.json` 坏掉时退回"目录名/文件名"并记警告，不让整个列表失败；
  - 缩略图认 `thumbnail.png` / `thumbnail.jpg` / `preview.png`，按路径缓存（`TextureLoader`，StbImageSharp）。
- **实测**：`res/shadertoy/stpreset` 扫出 5 个作品（标题/作者都正确），排序按修改时间；
  覆盖测试 `tests/.../Import/ShaderLibraryTests.cs`（6 个用例）。

### S7-10 快捷键与 ImGui 的键盘捕获（已确认）

- **现象**：开了 `ImGuiConfigFlags.NavEnableKeyboard` 后，`WantCaptureKeyboard` 经常为真，
  于是空格/R/F11/F12 全部失效 —— 编辑器一打开就再也没法暂停或截图。
- **对策**：F11/F12（功能键）不检查捕获状态；空格/R 仍然只在界面没吃键盘时生效（否则打字会打出一个空格还顺便暂停）。
- **注意**：界面里那句"键盘被界面占用"是真实状态，不是 bug。

### S7-12 高 DPI 下界面字号偏小（已确认 → 已修）

- **现象**：界面文字明显偏小（用户反馈）。实测这台机器是 **175% DPI（168 DPI）**，
  而进程原本没声明 DPI 感知 —— 1280×720 的窗口把那 1280×720 当点数用，于是整块界面只有应有尺寸的 1/1.75。
  之前截图里"窗口 1280×720 但 PNG 只有 1092×614"也是同一件事的副作用。
- **修法**（对齐 ProjectM 线的做法）：
  1. 建窗口**之前** `SetProcessDpiAwarenessContext(PerMonitorV2)`（实测返回成功）；
  2. 窗口尺寸按 `GetDpiForSystem()/96` 放大：1280×720 → **2240×1260**，并夹到屏幕 92%/88% 以内；
  3. `ImGui.GetStyle().ScaleAllSizes(scale)`，字体 18×scale / 代码 17×scale（175% 下 31.5px / 29.8px）；
  4. 界面里的面板宽度、表格列宽、时间轴滑块宽度等像素常量**全部乘 scale**，否则字变大了面板还是老的宽度，按钮会被挤出去；
  5. `--ui-scale F` 可手动覆盖（也会影响启动窗口尺寸）。
- **图集代价**：31.5px + 2500 个汉字，ImGui 默认 2×2 过采样会顶到 2048×4096（32 MB）；
  关掉过采样（`OversampleH/V = 1`）后是 **2048×2048（16 MB）**，31px 字号下肉眼无差。
- **布局陷阱**：`imgui.ini` 会记住上次的窗口尺寸，DPI/分辨率一变，旧尺寸会把面板挤扁。
  现在检测到帧缓冲尺寸变化就强制重排一次（`ImGuiCond.Always`），之后用户自己拖的布局仍会被记住。
- **实测**：2240×1260 下 flGyWc 约 **89 FPS**；音频缓冲 212 ms、欠载 0 帧；
  `--shot` 出图确认中文、按钮、通道表都放得下（`artifacts/p7_final.png`）。

### S7-11 P7 的已知缺口（未实测的部分）

- **拖拽**：`window.FileDrop` → JSON 走 `_openPath`（与 `--shader` 同一条加载路径，已验证），
  图片以前塞进选中通道，现在只弹提示（界面不再编辑，S7-13）。但"真的用鼠标拖一次"没法自动化，交互本身未实测。
- **缩略图**：`res/shadertoy/stpreset` 里没有 `thumbnail.png`，所以界面上一直是"（没有缩略图）"；
  扫描能识别缩略图路径有单测覆盖，解码路径与通道贴图共用 `TextureLoader`（有测试）。
- **编辑结果不回写 JSON**（历史）：那时候界面改动只活在内存里；现在没有内存编辑了，
  想改就改 `project.json` 再点"重新加载"。

### S7-13 界面改为只读：只留一个"重新加载"（按需求调整，已实现）

- **去掉的东西**：pass 代码编辑器（`PassEditor.cs` 整块删除）、通道下拉/采样器/vflip/路径编辑、
  "应用并重编译"、"还原"、诊断/通道/转译后 GLSL 三个标签页，以及 `Player.ApplyEdits` /
  `Player.RefreshChannelBindings` 这两个只为编辑存在的入口。
- **留下/新增**：本地库面板（扫描、下载、删除、收藏、占用、缺资源）、播放控制（暂停/重置/时间轴/
  截图/全屏/buffer 分辨率）、只读的"通道"折叠面板、pass 列表（只读）、错误面板（含"复制上下文"），
  外加主控栏一个 **「重新加载」** 按钮。
- **重新加载 = 从文件重新读入**：`Player.Reload()` 重新解析 `project.json`；
  与 `LoadFile` 的差别是**失败时保留当前画面**（不回落到演示作品），避免外部编辑器正好写坏文件时丢掉正在看的东西。
- **坑：不能在 ImGui 帧中途换 GL 资源**。按钮事件发生在 `_ui.Draw()` 里，此刻管线/纹理正在用；
  所以事件只置一个标志，`Program` 在**下一帧开头**（与拖入 JSON 同一条路径）执行 `ReloadProject`。
- **坑：主控窗固定占满整列会挡住画面**。去掉编辑器后中间列只剩一块信息面板，
  现在高度改成自适应内容（`SetNextWindowSize(width, 0)`），画面在面板下方完整可见。
- **验收实测**：`--selfcheck-reload` 输出 `Reload 成功，代码已从文件恢复 = True`；
  `--shot` 出图确认布局（库 / 主控 / pass 列表三栏，无编辑器，主控栏有"重新加载"）。

### S7-14 布局再调整：pass 列表挪到左下角、列表顺序固定（按需求调整，已实现）

- **去掉左下角的"选中项详情"块**（缩略图、占用、播放/打开目录/删除/收藏按钮）：库面板只剩
  「重新扫描 / 导入 JSON… / 过滤 / 列表 / 一行提示」，变矮；这些操作改到列表项的**右键菜单**
  （播放 / 打开目录 / 收藏 / 删除…→ 弹确认框），平铺布局的项禁用"删除…"。
- **pass 列表从右栏挪到左下角**（库面板正下方，等高剩余空间，内容可滚动）；右侧整块留给主控窗 +
  画面，主控窗仍高度自适应内容。
- **列表顺序在扫描时固定**：
  - 双击播放**不再触发重扫**：`Player` 记完 `lastPlayed` 后，UI 只把那一项**就地**替换
    （`LibraryPanel.MarkPlayed`），列表顺序不变（以前重扫会按最近播放把刚播的顶到最上面）；
  - 隐式重扫（下载/导入/删除/收藏后）用 `Rescan(preserveOrder: true)`：既有作品保持原位置，
    新增作品排最前；
  - 只有用户点「重新扫描」才按"最近播放"重排。
- **坑：右键菜单必须紧跟 `Selectable`**。行尾还有 `SameLine` 的作者/★/缺资源文字，
  `BeginPopupContextItem` 绑定的是"最后一项"，放在文字后面会导致右键只有点在文字上才生效。
- **实测**：`--shot` 出图确认为"左列上=库、左列下=pass 列表、右侧=主控 + 画面"；
  导入 alpha/bravo/charlie 三个作品后播放列表最底部的 bravo，左栏顺序仍是 `alpha, charlie, bravo`
  （bravo 没有被顶到最前），`meta.json` 里 `lastPlayed` 已更新。

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

落地代码：`Import/ShaderMeta.cs`（meta.json）、`Import/ShaderToyPage.cs`（页面抠 JSON）、
`Import/JsonTextRewriter.cs`（原文级路径改写）、`Import/ShaderToyDownloader.cs`（下载编排 + 资源抓取）、
`Import/LocalJsonImporter.cs`（本地 JSON 导入）、`Import/LocalLibrary.cs`（删除/占用/最近播放）；
UI 在 `App/Ui/LibraryPanel.cs`（含 `App/FileDialog.cs`），命令行在 `Program.cs`。

### S8-0 下载/导入的数据流（实现后的真实路径）

1. **来源**：下载 = 站点页面内嵌 JSON；导入 = 用户本地的 `.json` 文件（文件名即 ID）。
2. **校验**：两条路都过同一个容忍型导入器 `ShaderToyJsonImporter`（裸控制字符修复、键名带空格、`id` 数字/字符串混用）。
3. **资源计划**：扫 `inputs[]` → `texture`（`filepath`/`src`）、`cubemap`（`_0.._5` 六面）、`volume`（跳过下载，仅导入本地面）；
   同一 URL/源文件只处理一次，目标名固定 `textures/<文件名>`（撞名加 `-序号`）。
4. **落地**：资源进 `textures/`；`project.json` 用**原文** + `JsonTextRewriter` 只改路径那几处；
   `meta.json` 记私有信息（来源/时间/收藏/最近播放/缺资源）。
5. **目录**：`<库>/<id 小写>/`（重名另存 `-<n>`）；扫描以 `project.json` 存在为准，列表按 `lastPlayed` 排序。
6. **播放**：管线先按"作品目录 + 相对路径"找资源（S8-7），找不到再退回按文件名，仍找不到就用黑纹理并在提示里点出来。
7. **下载特有**：UA/Referer、并发 ≤4、429/5xx 退避重试 2 次、单资源失败只记 `missingResources`；
   **实测受限于本机被 Cloudflare 403（S8-8），端到端只验证了导入与"下载失败"路径。**

### S8-1 页面里的 JSON 是内嵌 JS，不是接口返回（部分验证：只验了两种形态，没验真站）

- **现象（待验证，真站访问不到）**：`https://www.shadertoy.com/view/<id>` 把工程塞在脚本变量里
  （`var gShaderToy = {...};` 或 `gShaderToy = JSON.parse('...')`），还可能带 `&quot;` 之类转义。
- **实测（本机网络被 Cloudflare 拦，S8-8）**：所有路径（含首页与 `/api/v1/shaders/<id>`）都返回
  `HTTP 403` + 11 字节 `Bad request`，`Server: cloudflare`；带全套浏览器头（`sec-ch-ua`/`Sec-Fetch-*`/
  `Accept-Language`/`Referer`）、HTTP/1.1、curl 与 .NET 两条客户端结果一致 —— 不是我们 UA 写错。
  所以"真站页面长什么样"这条**仍待验证**，用下面的对策 + 夹具测试覆盖已知的两种形态。
- **对策（已实现）**：定位 `gShaderToy` → 跳过 `=` → 分两路：
  1. `{`/`[` 开头：**括号配对**（尊重字符串与 `\` 转义），截完再做 HTML 实体反转义；
  2. `JSON.parse(` 开头：按引号配对取 JS 字符串，再解 JS 转义（`\n` `\t` `\uXXXX`、反斜杠续行）；
  两条都不通时才尝试"整个页面实体反转义后重来"（`&quot;` 版本的页面）。
  失败时把原始页面/JSON 落到 `<库>/_failed/<id>-<时间>.html|json`，方便事后比对。
  绝不能"截到 `</script>`"——GLSL 里什么都可能出现。

### S8-2 资源 URL 必须在保存时改写成相对路径（已确认 → 已实现）

- **现象**：`inputs[].filepath` 是站点路径（`/media/a/<hash>.png`，还常写成 `\/media\/a\/...`）。
- **对策**：下载到 `textures/<文件名>`，把 JSON 里那个**字符串值**改成相对路径；同一 URL 只下一次
  （按 URL 去重）；文件名撞车时加 `-2`。夹具测试覆盖"改写后不再含 `/media/a/...`"。

### S8-3 `project.json` 必须原样保存（已确认 → 已实现）

- **做法**：不反序列化重写。`JsonTextRewriter` 只替换"**整段字符串值恰好等于某 URL**"的字面量，
  其余字节（字段顺序、空白、`\n` 转义）原样不动；匹配前会把 `\/` 解码，所以真实导出的转义写法也能命中。
- **实测**：重写后的 `project.json` 仍是单行、仍含字面 `\n`、`/media/a/hash.png` 消失，
  `ShaderToyJsonImporter` 再读回来代码行数与原来一致（`ShaderToyDownloaderTests`）。

### S8-4 下载要带正常 UA，且要限速/重试（已实现）

- `HttpClient` 带浏览器 UA、`Referer`、`Accept`、自动解压、30 秒超时；单作品资源并发 4；
  429 与 5xx 退避重试 2 次（400ms 起）；其余 4xx 直接失败。
- 单资源失败**不中断**：记进 `meta.json` 的 `missingResources`（含原因），作品照常落地。

### S8-5 目录名只能用 ID（已确认 → 已实现）

- 目录固定为 ID 的**小写**形式（`flGyWc` → `shaders/flgywc/`），标题/作者只进 `meta.json`；
  "另存为副本"生成 `<id>-2`、`<id>-3`…；重复下载同 ID 直接复用同一目录（不再产生第二个）。

### S8-6 本地库扫描要容忍"手工放进去的作品"（已确认 → 已实现）

- 扫描以 `project.json` 存在为准；`meta.json` 缺失时用导入器解析 `info.name/username` 并**不回写**；
  没有缩略图就不显示图。
- **实测**：手写一个只有 `project.json` + `textures/` + `meta.json` 的作品目录（无缩略图），
  `--library <dir> --shader p8test` 能按 ID 解析、正常出帧，`--list` 也能列出。

### S8-7 相对路径的资源必须按"作品目录 + 相对路径"解析（已确认，P8 才发现）

- **现象**：下载后 JSON 里是 `textures/x.png`，而 `ShaderToyPipeline.ResolveTexturePath` 原来只做两件事：
  `File.Exists(原路径)`（相对当前工作目录，必落空）与"按文件名在搜索目录里找"（`x.png` vs `textures/x.png`，也落空）。
  不修这条，P8 下载完的作品离线加载会**全黑**，而且只在"下载目录"里复现，很容易被当成下载失败。
- **对策**：先按"搜索目录 + 整条相对路径"找，再退回文件名匹配。**实测**：同样的工程在
  `textures/` 子目录下能加载，日志里不再出现"找不到图片"（`--shader p8test --shot`）。

### S8-8 真站访问被 Cloudflare 拦（已确认，本机环境）

- **现象**：`curl` 与 .NET 客户端对 `www.shadertoy.com`（首页、`/view/<id>`、`/api/v1/...`）一律
  `403` + `Bad request`（`Server: cloudflare`，`CF-RAY` 存在），换 UA/浏览器头/HTTP 版本无效。
- **结论**：本机网络出口被判为机器人/不可信，**下载功能没法端到端实测**。用户侧要看到真实效果，需要
  在能直连该站的网络里跑一次 `--download flGyWc`；代码侧的错误路径已走通
  （`HTTP 403 Forbidden（URL）` + 退出码 1，不再挂起或乱抛异常）。
- 后续若要绕开：上游那套"登录 shadertoy 拿 cookie"的做法（Boost.Asio+OpenSSL）在本项目里没移植，
  真要支持需要单独做一个可配置的 Cookie 注入（`--cookie`），属于 P9 之后的事。

### S8-9 UI 侧的下载是异步的（已确认）

- 下载放在 `Task` 上跑，面板每帧只轮询；进度用自写 `IProgress<string>`（不用 `Progress<T>`——它依赖
  同步上下文，在 GLFW 主循环里回调时机不可控），后台只覆盖一个字符串字段，不碰 ImGui。
- 完成后自动重扫库并把作品交给 `Program` 加载；取消走 `CancellationTokenSource`。
- **注意**：删作品只允许"库根下一级、且含 `project.json`"的目录（平铺布局不支持整目录删，
  右键菜单里该项置灰），防止手滑把库根或示例目录整块删掉（`LocalLibraryTests` 覆盖）。
  入口在列表项的右键菜单（S7-14 之后，主界面不再有选中项详情面板）。

### S8-10 导入本地 JSON：文件名即 ID（已实现）

- **需求**：不联网也要能把作品放进库；文件名就是 ID。实现 `Import/LocalJsonImporter.cs`，
  入口是库面板的「导入 JSON…」和命令行 `--import FILE`。
- **命名**：ID = 文件名（不含扩展名），目录 = `<id 小写>`；文件名里的非法字符只在**目录名**上换成 `_`，
  `meta.json` 里保留原文件名（`project.json` 的 `info.id` 不参与命名，避免出现"文件叫 a、目录叫 b"）。
- **资源跟人走**：JSON 里引用的本地文件按"绝对路径 → 相对 JSON 目录 → 同目录同名文件"顺序解析，
  拷进 `textures/` 并改写 JSON 路径；cubemap 顺带 `_1.._5`。**实测**：
  `我的作品.json` 引用 `pics/logo.png` → `<库>/我的作品/{project.json, meta.json, textures/logo.png}`，
  `project.json` 里已变成 `textures/logo.png`，`f\/iResolution` 这类原文转义保持不变。
- **站点路径留空**：`/media/...`、`http(s)://` 离线拿不到 → 记 `missingResources`，作品仍导入并可播放（黑纹理 + UI 提示）。
- **自我覆盖防护**：源文件在库根下（重复导入自己的 `project.json`）直接拒绝 —— 否则"覆盖模式"会先建目录再自己拷自己。
- **文件对话框**：`comdlg32!GetOpenFileNameW`（`App/FileDialog.cs`）。不引 WinForms（AOT 不友好），
  也不移植上游 nativefiledialog；应用本来就只能在 Windows 跑。对话框是模态的，期间没有 GL 调用。
- **实测**：`--import` 导入中文文件名作品 → `--list` 显示 ID=文件名 → `--shader <文件名>` 按 ID 播放出图；
  UI 出图确认「导入 JSON…」按钮与列表项（标题、ID、作者）。
- **测试**：`LocalJsonImporterTests` 7 个用例（相对/绝对路径改写、站点路径记缺、六面 cubemap、覆盖/另存、坏 JSON、自导入拒绝）。

### S8-11 手工补资源放哪里 + 补齐命令（已实现，推荐用法）

- **位置**：资源放在**作品自己目录**下的 `textures/`，即 `<库>/<作品ID小写>/textures/<文件名>`；
  **不是**库根的 `shaders/textures/`（那里不会被搜索）。
  平铺布局（库根直接放 `xxx.json`，如 `res/shadertoy/stpreset/`）时作品目录就是库根，
  也就是 `<库>/textures/<文件名>`。
- **文件名**必须和 JSON/URL 里的一致（就是 `https://www.shadertoy.com/media/a/<hash>.png` 的最后一段）。
- **路径兜底**：`ResolveTexturePath` 第 3 步会在 `<作品目录>/textures/` 里按文件名找，
  所以 JSON 里还留着 `/media/a/xxx.png` 也能直接用，放完点"重新加载"即可。
- **清标记**：右键"补齐缺失资源"（CLI `--fetch-missing <id|目录>`）——文件已存在就**不联网**，
  只把 JSON 改成 `textures/xxx.png` 并从 `meta.json` 的 `missingResources` 里移除。
  **实测**：手工放好 `textures/<hash>.png` 后跑 `--fetch-missing`，输出"成功 1 个"，JSON 已改写、缺失列表清空。
- **代理**：`--proxy socks5://127.0.0.1:1080`（也接受裸 `host:port`、`none` 直连）；
  优先级 命令行 &gt; `MILKDROP_PROXY` &gt; 设置文件 &gt; 默认 `socks5://127.0.0.1:1080`；
  界面左侧"代理"一栏可改并持久化。**实测**：故意把端口写错会立刻报"积极拒绝"，说明请求确实走代理。
  但即便经代理，站点仍对 curl/.NET 这类客户端返回 403（S8-8，需要登录/浏览器指纹），所以自动下载暂时搁置。

### S8-12 数据源整体改为 `shadertoy.db`（已实现，按需求调整）

- **需求**：不再读 `shaders/` 目录；工程与 pass 代码都从 `shadertoy.db` 拿，**只有图片**从 `media/` 拿。
- **实现**：`Import/SqliteDatabase.cs`（P/Invoke 系统自带 `winsqlite3.dll`，只读、无 NuGet、AOT 友好）+
  `Import/ShaderDatabase.cs`（读 `shaders` / `renderpasses` / `shader_inputs` / `shader_outputs`）；
  `ShaderLibrary.Scan(数据根)` 只查列表需要的几列，`Player.LoadDatabase(db, id)` 才取完整工程，
  `Player.Reload()` 按来源（库/文件/演示）分别重读。
- **工程来源**：优先 `shaders.RawJson`（原样上游文本）；pass 代码**以 `renderpasses.Code` 为准**覆盖，
  但要求同 `PassIndex` **且 pass 类型一致**；`RawJson` 空或坏掉时按三张表重建（工程里带"按表重建"提示）。
- **图片**：JSON 里的 `/media/a/x.png` 保持原样，落到 `<数据根>/media/media/a/x.png`；
  `ResolveTexturePath` 第 1 步"搜索目录 + 相对路径"直接命中，不需要再下载或改写 JSON。
- **下线**：库面板的下载/导入/删除/收藏/占用/补齐资源，CLI 的 `--download`、`--import`、`--fetch-missing`、
  `--proxy`、`--library` 全部移除；`LocalLibrary`（文件布局的删除/占用/最近播放）删除。
  `ShaderToyDownloader` / `LocalJsonImporter` / `MissingResourceFetcher` / `ShaderMeta` 保留给测试与工具，播放侧不引用。
- **坑 1**：缺图统计不能把所有带 `FilePath` 的 input 都算上 —— keyboard/music 的 `FilePath` 是站点预设图
  （`/presets/tex00.jpg`，不在 `media/` 里），按老口径 `73KXRh` 会误报"缺 2 个图"。现只算 texture/cubemap/volume。
- **坑 2**：库里 `renderpasses` 的顺序不保证和 `RawJson` 一致（`73ySRh` 是 image/common/buffer 混排），
  所以覆盖代码前要校验 pass 类型，否则会把 Buffer 的代码塞给 Image。
- **垂直同步默认打开**：`EglContext.CreateForWindow` 的 `vsync` 参数从 `false` 改成默认 `true`
  （`eglSwapInterval(1)`），另加 `--no-vsync` 关闭；启动日志打 `VSync: 开（跟随显示器刷新率）/ 关`。
  **实测**（RTX 5060，`--shot-frames 40`）：开 → 39 帧 / 0.42s（≈93 Hz，贴着本机刷新率）；
  关 → 39 帧 / 0.22s（≈175 FPS，跑满）。`--fixed-dt` 的确定性不受影响（时间步固定，与交换节奏无关）。
- **多显卡选择**：加 `--gpu`（`list` / 编号 / 名字片段 / `luid:xxxxxxxx`）与 `shadertoy.json` 的 `gpu` 字段。
  实现 `Rendering.Gles3/GpuAdapter.cs`：`dxgi.dll` 的 `IDXGIFactory1::EnumAdapters1`（vtable 槽 12）+
  `IDXGIAdapter1::GetDesc1`（槽 10）拿 LUID/名称/显存；`EglContext` 把 LUID 拆成
  `EGL_PLATFORM_ANGLE_DEVICE_ID_HIGH/LOW_ANGLE` 传给 `eglGetPlatformDisplayEXT`。
  **坑**：这两个属性的值是 **0x34D6/0x34D7**，不是 0x348E/0x348F —— `0x348F` 其实是
  `EGL_PLATFORM_ANGLE_NATIVE_PLATFORM_TYPE_ANGLE`，写错时 ANGLE 不报错、直接回落到默认卡（现象：`--gpu 1`
  看着"成功"但 `GL renderer` 仍是核显）。排查手法：临时把 `EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE` 改成
  WARP 看 renderer 会不会变成 `Microsoft Basic Render Driver`（会 → 属性链路没问题，问题在属性名/值）。
  **实测**（本机 AMD 610M + RTX 5060 Laptop）：`--gpu list` 列出 4 项（AMD×2、NVIDIA、Microsoft Basic Render
  Driver[软件]）；`--gpu 1` / `--gpu nvidia` → `GL renderer : ANGLE (NVIDIA, NVIDIA GeForce RTX 5060 Laptop GPU ...)`；
  不带参数 → 仍是 `ANGLE (AMD, AMD Radeon(TM) 610M ...)`（系统默认）；`--gpu 9` → 打印候选 + 回落系统默认。
  `GpuAdaptersTests` 10 个用例覆盖索引/名称（含多命中取大显存）/LUID/错误提示。
- **配置文件改名搬家**：`settings.json` → **exe 目录下的 `shadertoy.json`**（示例：
  `{"dataDir": "F:\\code\\ShaderToySnap\\output"}`）。跟着程序目录走、方便手改；
  `%APPDATA%\MilkDropShaderToy\settings.json` 只作只读兼容（新文件不存在时才读），写回时落到新位置。
- **列表只读名字列（懒加载）**：`ShaderLibrary.Scan` 的 SQL 只有
  `select ShaderId, Name, UserName, CapturedAt from shaders`，不再带两个 count 子查询；
  pass 数 / 缺图 / 标签改由 `ShaderDatabase.TryReadDetails(id)` 在打开作品时查（`--list` 显式命令才逐条补查）。
  回归测试：`Scan_OnlyNeedsShadersTable` 故意只建 `shaders` 表，列表照样能出（细节查询返回 null 而不抛）。
- **左栏底部**去掉"双击播放 / 右键：播放·打开数据目录"提示行，只留数据库路径；列表项不再显示"缺 N 图"标记，
  tooltip 只给标题/ID/作者 —— 详情统一在打开后的右侧主控面板里看。
- **实测**：`--list` 列出 `F:\code\ShaderToySnap\output` 里 8 个作品（含标题/作者/pass 数/抓取时间）；
  `73KXRh`（2 pass + 贴图）与 `73ySRh`（image/common/buffer）都能正常出帧，贴图从 `media/media/a/` 取到、
  控制台无"找不到图片"；`--shot` 截图确认左栏列表单行不换行、底部显示数据库路径。
- **测试**：`ShaderDatabaseTests`（RawJson 路径、表重建、代码覆盖、类型不一致不覆盖、坏 ID/坏文件）与
  `ShaderLibraryTests`（列表/排序/缺图统计/Find/Search/坏库）合计 10 个用例。
