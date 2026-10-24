# 把 MilkDrop 搬进 .NET：9795 个预设、ANGLE/GLES3，以及一次「AI 许愿式」移植

> 一句话概括：把 projectM 4.1.8 和 MilkDrop3 这两套 C++ 音乐可视化引擎，用 C# 重新实现了一遍，
> 跑在 Windows x64 上，9795 个真实预设能跑通 9728 个，剩下的 67 个是预设文件自己坏了。
> 代码几乎全部由大模型按自然语言需求生成，人类只负责提需求、跑验收、指出哪里不对。

仓库：<https://github.com/sevencat/MilkDropSharp>（GPL-3.0）

---

## 一、起因

MilkDrop 大概是「音乐可视化」这个领域最经典的东西：Winamp 时代的一个插件，靠 warp/comp shader
把一帧画面反复变形成下一帧，再加上波形、频谱、自定义形状，做出那种跟着鼓点流动的迷幻效果。
它有两个活着的后代：

- **projectM**：跨平台重写，4.x 用 GLSL，带完整的 C API 和 SDL 前端；
- **MilkDrop3**：Windows 上的原教旨版本，D3D9 + HLSL 汇编着色器。

两者都是 C++。而我想在 .NET 里跑——不是为了取代原版，纯粹是想看看：
**把一套二十年前就开始演化的图形/音频代码，整体翻译成人能读的托管代码，会发生什么。**

于是就有了这个仓库。它现在包含两条互相独立的移植线，共用一个表达式引擎和一个 HLSL 转译器。

---

## 二、项目长什么样

| 移植线 | 对应上游 | 渲染 | 入口 |
|---|---|---|---|
| **ProjectM** | libprojectM 4.1.8 + `frontend-sdl-cpp` | ANGLE → OpenGL ES 3.0（Silk.NET） | `src/MilkDrop.ProjectM.App` |
| **Classic** | MilkDrop3（Milkdrop2 插件语义） | Direct3D 9（Vortice）+ HLSL 汇编 | `src/MilkDrop.Classic.App` |

两个共用库：

- `src/MilkDrop.Eval`：`projectm-eval` 的移植，ns-eel 表达式解释器（`regNN` 槽、megabuf、全套内建函数）；
- `src/HlslParser`：`hlslparser` 的移植，把预设里的 HLSL 片段转成 GLSL 330 / GLES 300。

再外加一个实验性的 **ShaderToy 播放器**（`src/MilkDrop.ShaderToy.App`）：直接把 shadertoy 的导出 JSON +
贴图目录丢进去播放，带本地库管理、在线下载、缺失资源补齐。

![ShaderToy 播放器 - Seascape](https://raw.githubusercontent.com/sevencat/MilkDropSharp/main/docs/shadertoy-seascape.png)

跑起来是这样（动图）：

![projectM - crystal palace](https://raw.githubusercontent.com/sevencat/MilkDropSharp/main/docs/projectm-crystal-palace.gif)

![projectM - neon space](https://raw.githubusercontent.com/sevencat/MilkDropSharp/main/docs/projectm-neon-space.gif)

Classic 线（D3D9）的画面：

![MilkDrop3 Classic](https://raw.githubusercontent.com/sevencat/MilkDropSharp/main/docs/classic.png)

---

## 三、四个真正难啃的地方

### 1. EEL 表达式引擎：不能碰 `Reflection.Emit`

projectM 的预设逻辑（每帧的 `per_frame_*`、`per_pixel_*`、`per_vertex` 代码）都是 ns-eel 表达式，
原版是自己写了个字节码解释器。C++ 里这很自然，挪到 C# 里最省事的做法是
「解析成表达式树 + `Expression.Compile()`」，性能也好。

但那样就**把自己钉死在 JIT 上**——Native AOT 下 `Expression.Compile()` 直接不可用。

于是只能老老实实移植解释器：栈式求值、`reg00`..`reg99` 槽位、megabuf/gmegabuf、几十个内建函数
（`sin`/`atan2`/`pow`/`if`/`loop`/`assign`/`exec2`/`exec3`…）。听起来枯燥，好处是：

- AOT 安全；
- 每个内建函数都能单独写测试（现在 98 个）；
- 语义可以 1:1 对照上游，出问题好定位。

代价当然也有：每帧每个顶点都要走一遍解释器，这在后面性能一节会体现出来。

### 2. HLSL → GLSL：`tex2D`、`register(...)` 和一堆历史包袱

ProjectM 线跑在 GLES3 上，而预设里写的是 HLSL 片段（warp/comp shader）。所以需要一个转译器。

`hlslparser` 这个 C++ 库本来就是把 HLSL 转 GLSL 的，我们照搬它的结构重写了一遍：

- 词法/语法分析 → AST → 生成 GLSL；
- 目标版本可选 `Version330`（桌面）或 `Version300Es`（GLES3）；
- `tex2D` / `tex3D` / `texCUBE`、`register(c0)`、`sampler_*` 声明这些老写法都要认；
- 65 个单元测试 + `tests/golden` 里的输入/输出对照文件。

这部分是整个项目里最「编译器工程」的地方，也是最容易出细节 bug 的地方——比如
矩阵是列主序还是行主序、`mul(a, b)` 展开成什么、结构体里能不能放 sampler。

### 3. 渲染管线：ANGLE、MRT、以及每帧几十次状态切换

ProjectM 线的渲染路径是 **ANGLE → D3D11**。选它不是因为性能，而是因为跨平台 + 稳定：
ANGLE 把 GLES3 翻译到 D3D11，省掉了自己写 D3D11 后端的功夫。

实际踩到的坑：

- **Silk.NET 自带的 ANGLE 原生包在 `runtimes/win-x64` 下是 x86 二进制**，塞进 x64 进程直接
  `BadImageFormatException`。最后用的是 Avalonia 构建的 `av_libglesv2.dll`（真 x64，EGL 入口带 `EGL_` 前缀）。
- buffer pass 需要 **MRT**（RGBA8 + RG16F 同时输出），得探测 `EXT_color_buffer_float` 之类的能力位。
- 每帧要跑完整 pass 图：buffer → buffer → image，中间还要处理 ping-pong、mesh 分辨率、宽高比校正。

实测下来，C# 版比原生慢一倍多（3.7ms vs 1.5ms/帧），但瓶颈**不在 GPU**，而在 CPU 侧：
每帧几十次 GL 状态切换 + 逐顶点的 EEL 求值。这个数字反而很有说服力——它说明移植没有偷工减料，
该做的事都做了。

### 4. Classic 线：D3D9 那条老路

MilkDrop3 是 D3D9 时代的产物：`ps_2_0`/`ps_3_0`、HLSL 汇编、sprite、shape、wave 代码、`include.fx`。

C# 这边用 **Vortice.Windows** 包 D3D9，加上 ImGui 的 DX9 后端。真正花时间的是「语义」而不是 API：

- 预设里的 shader 是**汇编**，得自己拼常量表、指令槽；
- 一些老驱动才容忍的写法（宏名遮蔽、`double`、非有限字面量）需要兼容层；
- `get_fft`、mouse 状态这些内置变量要在每帧灌进去。

这部分现在有 187 个测试，覆盖预设语义、sprite、混合、EEL 预处理等。

---

## 四、怎么证明「没跑歪」

移植最怕的是「能跑，但跑出来的东西不对」。这个项目用三种方式兜底：

1. **全量预设跑通**：`res/` 里有 projectM 官方发行版的 **9795 个 `.milk`**。批量跑一遍，
   记录加载失败、编译失败、帧耗时，出 CSV。失败 67 个（0.68%），逐个看下来都是预设文件自身的
   语法/寄存器错误，不是移植问题。
2. **逐像素 A/B**：把原生 `projectMSDL.exe` 和 C# 版用同样的固定时间步长渲染同一帧，
   读回像素做差。AOT 版 vs JIT 版平均差 **0.89/255**（基本就是浮点舍入和驱动差异）。
3. **单元测试**：Eval 98 个、HlslParser 65 个、ProjectM 126 个、Classic 187 个。
   部分渲染测试会建**离屏 GL 上下文（32×24）**真实执行 shader，所以纯无显卡 CI 跑不了。

覆盖率不等于正确性，但至少能把「大改一处、别处悄悄坏掉」这种事挡在提交之前。

---

## 五、性能

Release JIT、ANGLE/GLES3、mesh 32×24、静音、无 UI、关 vsync，单位 ms/帧：

| 分辨率 | 原生 libprojectM 4 | C# 版 |
|---|---|---|
| 640×480 | 1.55 | 3.73（268 FPS） |
| 1280×720 | 1.48–1.54 | 3.65（274 FPS） |
| 1920×1080 | 1.47–1.51 | 3.70（270 FPS） |

几点观察：

- 耗时几乎与分辨率无关 → **瓶颈在 CPU**（状态切换 + 逐顶点 EEL）。
- 少数预设特别慢，是因为**自定义形状实例数**（例如 3584 个 shape/帧 → 单帧 17ms），不是填充率。
- 2560×1600 全屏会翻转为**填充率瓶颈**：warp shader 每像素多次采样，约 66 FPS。

另外，ProjectM App 可以 Native AOT 发布成不依赖 .NET 运行时的单 exe（约 11MB，冷启动 0.46s）。
但有个硬限制：**NAudio 的 WASAPI 在 AOT 下不可用**（`MMDeviceEnumeratorComObject` 的构造被 ILC
编成必定抛异常的桩），音频会自动降级成静音，画面照常。需要声音就用 JIT 版。

---

## 六、踩坑清单（节选）

- ANGLE 的 EGL 入口是 `EGL_` 前缀，Silk.NET 的绑定名对不上，得自己 `GetProcAddress`。
- `glGetError` 在 ANGLE + MRT 下偶尔会因为 framebuffer 不完整而报警，要按能力位分支。
- ImGui 中文字体用 `msyh.ttc`，字号按 DPI 缩放；否则高 DPI 下字小得看不清。
- `imgui.ini` 会记住窗口布局，改分辨率后旧布局会把面板挤扁，所以尺寸一变就强制重排一次。
- GLES3 里 sampler 的 min filter 不能和 `textureGrad` 随便混用；mipmap 必须显式生成。
- 预设 JSON 常被编辑器/网页「硬换行」搞坏，字符串里出现裸 CR/LF，还有被打断的 `\ /`。
  解析器要能按上下文修复后再交给 `JsonDocument`。

---

## 七、已知限制

- **仅 Windows x64**：Classic 绑死 D3D9，ProjectM 绑死 ANGLE 的 D3D11 后端 + WASAPI 环回。
- ProjectM 线**不包含** projectM 的 C API 层、`ProjectMCWrapper`、SDL 前端；
  预设里的 user sprite 与歌曲标题动画未移植。
- AOT 版无音频。
- `--fullscreen` 以显示器当前模式为准，`--width/--height` 不生效。

---

## 八、下载 / 自己编译

不想装 .NET SDK 的话，这里有一个 **Windows x64 免安装包**（三个 App + 预设 + 纹理 + 示例 shader，
解压即用）：

- 百度网盘：<https://pan.baidu.com/s/1QTe2AXRftQvoQH7nxpUiMA?pwd=gbj7>
- 提取码：`gbj7`

自己编译（需要 .NET 10 SDK）：

```powershell
# projectM 线：预设目录必须显式给，或者放一份 presets\ 在 exe 旁边
dotnet run --project src\MilkDrop.ProjectM.App -c Release -- ^
  --preset-dir "res\projectMSDL-2.0.0-win64\presets\presets-cream-of-the-crop"

# Classic 线：资源目录会自动向上找仓库根
dotnet run --project src\MilkDrop.Classic.App -c Release -- --preset random

# Native AOT（ProjectM，约 11MB）
dotnet publish src\MilkDrop.ProjectM.App -c Release -r win-x64 -p:PublishAot=true -o artifacts\p10\aot
```

常用快捷键：`Space` 暂停、`N/P` 切预设、`R` 随机、`L` 锁定、`S` 截图、`F11` 全屏、
`F` 显示/隐藏界面。

---

## 九、最后

这个项目最大的感受不是「AI 能不能写代码」，而是**验收比生成更费劲**：

- 生成一段能跑的渲染代码很容易，生成一段**和上游逐像素一致**的渲染代码很难；
- 一次「这里跑不动」背后，往往要翻上游 C++ 源码对照语义，再决定是改代码还是改测试；
- 9795 个预设的批量跑 + A/B 截图对比，本质上是在给「许愿」找一个可量化的验收标准。

所以仓库 README 一开头就写着：**这是 AI 许愿式产物，适合当可运行的参考实现 / 学习材料 / 折腾玩具，
直接上生产请自己加固。** 这句话不是免责声明，是这个项目真实的状态。

如果你也对「把老图形代码翻译成托管代码」感兴趣，欢迎翻源码；能跑起来并且画面对得上，
已经比想象中难很多了。

---

*图片若加载不出来，仓库 `docs/` 目录下有原图；动图约 1MB 一张，加载需要一点时间。*
