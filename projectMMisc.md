# projectM 移植杂记

> 进度/计划见 `projectMPlan.md`，踩坑见 `projectMIssue.md`，本文件放性能实测、语义笔记与待办。

---

## 1. 与原生 SDL 版的性能对比（2026-10-24 实测）

### 1.1 测试环境与方法

| 项 | 值 |
|---|---|
| CPU / GPU | AMD（Radeon 780M iGPU，驱动 26.8.1） |
| 原生侧 | 官方 `res/projectMSDL-2.0.0-win64/projectM-4.dll`（C++，桌面 GL 4.6 compat），直接 P/Invoke |
| C# 侧 | `MilkDrop.ProjectM`（Silk.NET + ANGLE/GLES3→D3D11），Release JIT |
| 预设 | `Dancer/Aurora/Jc - Crystal Shards.milk`（p1）、`$$$ Royal - Mashup (257).milk`（p2） |
| 条件 | 同尺寸、同网格、静音、无 ImGui（`--no-ui`）、关 vsync |

测量脚本在 `artifacts/perf/`：

- `NativeBench.cs` — P/Invoke 探针：建 WGL 上下文 → `glewInit()` → `projectm_create()` →
  `projectm_load_preset_file()` → 循环 `projectm_opengl_render_frame()`，可选每帧 `glFinish()`。
- `run-native-batch.ps1` / `run-native.ps1` — 原生侧批次跑。
- `run-csharp-batch.ps1` — C# 侧用 `--bench`（纯渲染循环，含逐帧 `gl.GetError()`），
  跑 100 帧与 1100 帧两次相减，消掉预设加载时间。
- `run-csharp-smoke.ps1` — C# 侧用 `--smoke 600 --novsync --no-ui`（真实窗口 + `eglSwapBuffers`）。
- `run-load-compare.ps1` — 30 个预设的加载耗时对比。

> **坑（已解决）**：libprojectM 4.x 要求**宿主先调用 `glewInit()`** 再 `projectm_create()`，
> 否则 `projectm_create()` 里会跳到空函数指针，进程直接 AV（异常偏移 0x0）。
> SDL 前端就是这么做的，自己搭宿主时不能漏。
>
> **坑（PowerShell 5.1）**：`.ps1` 无 BOM 时按 ANSI 读，脚本里的中文正则会变成乱码，
> 解析 exe 输出时只能用数字模式匹配；`& exe > file` 对 GUI 子系统程序拿不到输出，
> 必须用 `Start-Process -RedirectStandardOutput`。

### 1.2 每帧耗时（mesh 32×24，libprojectM 默认）

单位 ms/frame。

| 分辨率 | 原生 无同步 | 原生 glFinish（含 GPU） | C# `--bench`（含 glGetError） | C# `--smoke`（真实循环 + swap） |
|---|---|---|---|---|
| 64×64 | 1.48–1.93 | 2.01–2.07 | 3.7–4.0 | — |
| 640×480 | 1.55 | 2.29–2.47 | 4.2 | 3.73（268 FPS） |
| 1280×720 | 1.48–1.54 | 2.96–3.21 | 4.9–5.0 | 3.65（274 FPS） |
| 1920×1080 | 1.47–1.51 | 4.07–4.94 | 5.6–6.4 | 3.70（270 FPS） |
| 320×240 | — | — | — | 3.63（275 FPS） |

### 1.3 每帧耗时（mesh 96×54 —— `projectMSDL.properties` 的默认值）

| 1280×720 | 原生 | C# |
|---|---|---|
| 纯渲染循环（无同步） | 5.11–5.51 | 10.3–12.3 |
| 含 GPU 同步 / 真实循环 | 5.96–6.37 | 9.6–9.7（103 FPS） |

### 1.4 预设加载 / 切换（30 个预设平均，640×480）

| | 原生 | C# |
|---|---|---|
| load（改 VM 前） | 102.6 ms | 89.6 ms |
| load（改 VM 后） | 68.3 ms | 88.6 ms |

两次跑的原生数字差很多（68 vs 103 ms），说明这项测量噪声大，结论只能是**两者基本相当**
（改成字节码后我们这边基本没变化，编译期多降一次树的开销很小）。

### 1.5 结论

1. **改字节码 VM 之前每帧慢约 1.5–2.5×**（960×540 网格 96×54：原生 ~160 FPS，C# ~103 FPS）；
   **改完之后基本追平甚至反超**（见 2.3）。
2. **耗时与分辨率基本无关**（320×240 与 1920×1080 都是 ~3.7 ms），瓶颈在 CPU 侧而非填充率。
3. **网格尺寸是主要杠杆**：mesh 32×24 → 96×54 时，原生 +3.6 ms、C# +6.0 ms。
   单点开销：C# ~1.36 µs/顶点 vs 原生 ~0.48 µs/顶点（≈2.8×），
   即 per-pixel 的 **EEL 表达式求值是第一瓶颈**。
4. 剩下 ~2 ms 固定差额来自：ANGLE 的 GLES→D3D11 命令转译（原生走 AMD 自己的 GL 驱动）
   + 托管包装/GC。这部分不是 C# 语言本身的锅，换 ANGLE 就换不掉。
5. **加载反而更快**：shader 编译走 ANGLE→D3D11 比 AMD 的 GL 编译器快。
6. `--bench` 的逐帧 `gl.GetError()` 在高分辨率下会强制同步，把 C# 数字从 3.7 抬到 6.4 ms；
   量产路径不该开，只留调试。

### 1.6 已做 / 待做

- [x] 实测数据（本文件）
- [x] `MilkDrop.Eval` 从树遍历解释器改成栈式字节码（见第 2 节）
- [ ] 量产出关掉逐帧 `gl.GetError()`（改成分批或仅在 `--debug` 开）
- [ ] `PerPixelMesh` 顶点写入改成连续 `float[]`（现在逐字段写 `MeshVertex`）

> **注意**：`--bench` 用的是 `bin\Release\net10.0\win-x64\` 下的产物（App 开了 `PublishAot`，
> 输出带 RID 子目录）。之前误用了 `net10.0\` 下的旧产物，导致测出来的数字没变化。

---

## 2. `MilkDrop.Eval` 改成委托 / 栈式字节码：工作量评估

### 2.1 现状

| 文件 | 行数 | 作用 |
|---|---|---|
| `Lexer.cs` | 359 | 词法 |
| `Parser.cs` | 357 | 语法 → `CompilerNode` |
| `CompilerBuilder.cs` | 122 | `CompilerNode` → `EvalNode` 树 |
| `IntrinsicFunctions/Math/Memory/Table` | 557 | 内建函数（表 68 条，含别名，不同实现约 45 个） |
| `CompileContext.cs` | 149 | 变量/函数表、megabuf、reg00–99 |
| `EvalNode.cs` / `EvalResult.cs` / `EvalProgram.cs` | ~110 | 执行核心 |

执行模型：`EvalNode.Func` 是 `delegate void EvalFunc(EvalNode, ref EvalResult)`，
每个节点一次**委托间接调用**，`Args` 是 `EvalNode?[]`（每节点一次数组分配 + 边界检查），
值通过 `EvalResult`（值 / 变量引用 / megabuf 引用三态）在节点间传。

### 2.2 三个方案

#### 方案 A：栈式字节码 VM（推荐）

**已落地（2026-10-24）**，新增 / 改动：

| 文件 | 内容 |
|---|---|
| `EvalOpCode.cs` | 约 70 条指令 + `Instr` 结构 + `CompiledCode`（指令流 / 最大栈深） |
| `BytecodeCompiler.cs` | 表达式树 → 线性指令（后序发射，栈深在编译期算好） |
| `EvalVm.cs` | 栈式解释器：`double[]` 值栈 + `long[]` 引用栈，`switch` 分派 |
| `Variable.cs` / `CompileContext.cs` | 变量值集中到 `double[]` 槽位数组，变量对象只留「名字 + 槽号」 |
| `EvalProgram.cs` | 默认走 VM，`ExecuteTree()` 保留树解释器用于对拍 |
| `IntrinsicTable.cs` | 规范名 → 指令的映射表 |

关键设计点：

- **左值语义**：引用编码成 `long`（高 8 位是种类：变量 / regNN / megabuf / gmegabuf，低位是下标），
  与值栈平行的 `long[] _refs` 保存。栈上放不了 `ref double`，这是最省事又不出错的做法。
- **短路语义**：`if` 用 `JzExact`（精确判 0，与 C 版一致）、`_and` / `_or` 用 `JzPush0` / `JnzPush1`，
  保证右侧不会被求值；`while` / `loop` 用 `Run(pc, end)` 递归执行循环体，
  循环计数与结果都在局部变量里，嵌套靠调用栈天然隔离。
- **`MaxLoopCount`**：`loop` 截断次数、`while` 剩余次数都和 C 版一样是 1048576。
- 常量折叠**原本就有**（`CompilerBuilder.CreateExpression`），不需要再做。

**验收**：`tests/MilkDrop.Eval.Tests` 187 个测试全绿（原本 147 + 新增 40 个
`VmDifferentialTests` 对拍用例：同一表达式分别用 VM 和树解释器跑，结果必须逐位相等）；
`MilkDrop.ProjectM.Tests` 195、`MilkDrop.Classic.Tests` 207 全绿。

**实际工作量**：约 1 人日（含对拍与排查）。

#### 方案 B：编译成委托（`Expression.Compile` / `DynamicMethod`）

**不推荐**：

- 与 P10 的 Native AOT 目标**直接冲突**（现在全库 0 处 `Reflection.Emit`，这是 AOT 能过的前提；
  `Expression.Compile` 在 AOT 下不可用或退化）。
- 每个预设都要 Emit：现在 load 90 ms，改成 Emit 后可能 200 ms+，还有 IL 内存增长；
  9795 个预设批量跑会更糟。
- 相对字节码的额外收益只有 ~1.3–1.5×，不值当。

#### 方案 C：不换 VM，只做局部优化（低风险，已部分并入方案 A）

- `Variable` 对象改成 `double[]` 槽 + `int` 索引：**已随方案 A 一起做了**（VM 直接按下标读写）。
- `CompilerBuilder` 里做常量折叠：**原本就有**，不需要补。
- `EvalNode.Args` 改内联字段（`Arg0/Arg1/Arg2`）：**未做**。树解释器现在只用于对拍和回退，
  不是热路径，收益很小；真要动可以后补。

### 2.3 实测收益（改完之后）

微基准（临时探针，同一段 per_pixel 代码跑 20 万次，注意要先预热，否则第一轮是 tier-0）：

| 用例 | 树解释器 | 字节码 VM | 提速 |
|---|---|---|---|
| per_pixel 全量（213 节点） | 1.21 µs/run | 0.66 µs/run | **1.84×** |
| 纯算术（185 节点） | 1.06 µs/run | 0.65 µs/run | 1.63× |
| sin/cos 密集片段 | 0.46 µs/run | 0.25 µs/run | 1.85× |
| `if` / `%` / 整数化片段 | 0.29 µs/run | 0.15 µs/run | 1.86× |

端到端（`--smoke 600 --novsync`，真实窗口 + swap）：

| 配置 | 改前 | 改后 | 提速 |
|---|---|---|---|
| 640×480 mesh 32×24 | 3.73 ms（268 FPS） | **2.48 ms（403 FPS）** | 1.50× |
| 1280×720 mesh 32×24 | 3.65 ms（274 FPS） | **2.11 ms（474 FPS）** | 1.73× |
| 1280×720 mesh 96×54 | 9.70 ms（103 FPS） | **5.47 ms（183 FPS）** | 1.77× |
| 1920×1080 mesh 32×24 | 3.70 ms（270 FPS） | **2.31 ms（432 FPS）** | 1.60× |

对照原生：mesh 96×54 @720p 原生 `glFinish` 口径是 5.96–6.37 ms，我们现在 5.22–5.47 ms，
**已经略微快过原生 C++ 版**（原生那组数字还不含窗口/输入/SwapBuffers）。
mesh 32×24 下原生仍是 1.5 ms 左右、我们 2.1–2.5 ms，差距主要在 ANGLE 的命令转译开销上。

> **踩坑**：微基准第一轮 VM 反而慢 30%（1.69 vs 1.21 µs），是 JIT 分层编译没热起来；
> 同一个程序跑第二轮就是 1.84×。以后测这类东西要跑两轮取第二轮。

### 2.4 遗留

1. `EvalNode.Args` 内联（方案 C 剩下的那一条）——只对回退的树解释器有意义，优先级低。
2. 对拍目前是单元测试级的 40 条表达式；更彻底的做法是拿 9795 个预设的
   per_frame / per_vertex / wave / shape 代码逐条编译并比对两个后端（要处理 `rand`、
   `gmegabuf` 这类跨上下文共享状态，得先隔离）。
3. 方案 B（编译成委托）已放弃：与 Native AOT 冲突。

---

## 3. 其他待办

- 逐像素 A/B 与原生 `projectMSDL.exe` 的像素级比对仍未自动化（原生 exe 不支持指定预设文件），
  步骤见 `projectMPlan.md` 第 7 节。
- `projectMPlan.md` D10 把 Q 变量扩到 64，全量语料里没人用 `q33+`，再扩需要改 bank 命名。
