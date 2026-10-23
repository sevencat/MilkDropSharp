# HlslParser (C#)

`vendor/hlslparser`（projectM 内置的 Unknown Worlds hlslparser fork）的 C# 移植。目标是把
Milkdrop preset 的伪 HLSL（D3D9 语法）翻译成 GLSL，行为与 C++ 实现**逐字节一致**（包括空白与换行），
以便替换 projectM 里的原生实现或在托管环境里复用。

- 目标框架：`net9.0`（库工程 `Nullable` 关闭，与 C++ 的宽松空指针语义保持一致）
- 依赖：无（仅 BCL）
- 参考实现：`vendor/hlslparser/src/*.cpp`
- 移植计划与阶段记录：仓库根目录 `plan.md`

---

## 1. 快速开始

```csharp
using HlslParser;

var request = new TranslationRequest
{
    Source = File.ReadAllText("warp.hlsl"),
    FileName = "warp.hlsl",
    EntryPoint = "PS",
    Target = GlslTarget.FragmentShader,
    Version = GlslVersion.Version330,
    Flags = GlslFlags.AlternateNanPropagation,   // projectM 的 preset shader 用这一组
};

TranslationResult result = new HlslToGlslTranslator().Translate(request);

if (!result.Success)
{
    foreach (Diagnostic diagnostic in result.Diagnostics)
    {
        Console.Error.WriteLine($"{diagnostic.FileName}:{diagnostic.Line}: {diagnostic.Message}");
    }
    return;
}

File.WriteAllText("warp.frag", result.Glsl);
```

`Translate` 依次执行：预处理（`ApplyPreprocessor`）→ 解析（`Parse`）→ 生成（`GLSLGenerator.Generate`），
任一步失败即返回 `Success = false`，并把已产生的诊断放进 `Diagnostics`。

### 调用方职责（不在库内）

与 C++ 版一致，Milkdrop 特有的文本改造仍属调用方：

1. 从 `.milk` 抽出 `warp_N`/`comp_N` 行、去掉行首反引号；
2. `sampler_state { ... }` 处理、`shader_body` → 入口函数签名、`{` 插入 `float3 ret = 0;`、末尾补 `_return_value = ...;`；
3. 剔除 preset 自带的 `sampler_*` 声明与 `float4 texsize_*` 声明，再按实际纹理插入 `uniform sampler2D/3D sampler_x;`、
   `uniform float4 texsize_x;`；
4. 拼接 preset header（uniform 群 + `time/bass/q1..q32` 等宏）。

参考实现：`src/libprojectM/MilkdropPreset/MilkdropShader.cpp`（`PreprocessPresetShader` / `TranspileHLSLShader`）、
`PresetFileParser::GetCode`。本仓库里的正式移植是
`src/MilkDrop.ProjectM/MilkdropPreset/MilkdropShader.cs`（P3），测试工程里的 `MilkdropPresetCorpus.cs`
是这套逻辑的测试专用复刻（供 golden 语料生成用）。

---

## 2. API

### 2.1 高层

```csharp
public sealed class HlslToGlslTranslator
{
    public TranslationResult Translate(TranslationRequest request);
}

public sealed class TranslationRequest
{
    public required string Source { get; init; }
    public string FileName { get; init; } = "";
    public string EntryPoint { get; init; } = "PS";
    public GlslTarget Target { get; init; } = GlslTarget.FragmentShader;
    public GlslVersion Version { get; init; } = GlslVersion.Version330;
    public GlslFlags Flags { get; init; }
    public bool Preprocess { get; init; } = true;          // false = 输入已预处理过
    public string ConstantBufferPrefix { get; init; } = ""; // EmulateConstantBuffer 时的前缀
    public bool ReturnPreprocessedSource { get; init; }     // 结果里带上预处理文本，便于排查
}

public sealed class TranslationResult
{
    public bool Success { get; init; }
    public string Glsl { get; init; }                // 失败时为 null
    public string PreprocessedSource { get; init; }  // 仅在 ReturnPreprocessedSource 时非 null
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; }
}

public enum GlslTarget { VertexShader, FragmentShader }

public enum GlslVersion
{
    Version110, Version120, Version140, Version150, Version330, Version100Es, Version300Es
}

[Flags]
public enum GlslFlags
{
    None = 0,
    FlipPositionOutput = 1 << 0,
    EmulateConstantBuffer = 1 << 1,
    PackMatrixRowMajor = 1 << 2,
    LowerMatrixMultiplication = 1 << 3,
    AlternateNanPropagation = 1 << 4,
}
```

`GlslFlags` 与 C++ 的 `GLSLGenerator::Flags` 一一对应：

| C# | C++ | 作用 |
| --- | --- | --- |
| `FlipPositionOutput` | `Flag_FlipPositionOutput` | 顶点着色器输出 `gl_Position` 时翻转 Y、把 Z 从 0..1 映射到 -1..1（D3D → GL） |
| `EmulateConstantBuffer` | `Flag_EmulateConstantBuffer` | cbuffer 改成 `uniform vec4 buf[N]` + 分量拼装，替代 `layout(std140)` |
| `PackMatrixRowMajor` | `Flag_PackMatrixRowMajor` | 保留标志位，生成器当前未使用（与 C++ 相同） |
| `LowerMatrixMultiplication` | `Flag_LowerMatrixMultiplication` | `mul` 展开成逐列乘加的辅助函数（便于 glsl-optimizer 折叠） |
| `AlternateNanPropagation` | `Flag_AlternateNanPropagation` | 乘法改用非 IEEE-754 NaN 传播规则的辅助函数（模拟 DX9/SM3） |

### 2.2 低层（与 C++ 逐函数对照，便于排查与单测）

```csharp
var allocator = new Allocator();
var tree = new HLSLTree(allocator);
var diagnostics = new CollectingDiagnosticSink();
var parser = new HLSLParser(allocator, tree, diagnostics);

parser.ApplyPreprocessor(fileName, source, out string preprocessed);
parser.Parse(fileName, preprocessed);

var generator = new GLSLGenerator(diagnostics);
var options = new GLSLGenerator.Options((int)GLSLGenerator.Flags.Flag_AlternateNanPropagation)
{
    ConstantBufferPrefix = "",
};
generator.Generate(tree, GLSLGenerator.Target.Target_FragmentShader,
                   GLSLGenerator.Version.Version_330, "PS", options);
string glsl = generator.GetResult();
```

注意：低层 `GLSLGenerator` 与 C++ 一样**不跨多次 `Generate` 调用重置状态**（输出缓冲与错误标志累积），
需要多次翻译请每次新建实例——`HlslToGlslTranslator` 内部就是这样做的。

### 2.3 诊断

```csharp
public readonly record struct Diagnostic(string FileName, int Line, string Message);

public interface IDiagnosticSink { void Error(Diagnostic diagnostic); }

NullDiagnosticSink.Instance        // 默认：全部丢弃（与 C++ 的 Log_Error 空实现一致）
new CollectingDiagnosticSink()     // 收集到 List<Diagnostic>
```

- 词法/解析错误带 `FileName` 与 `Line`。
- `GLSLGenerator` 的错误（入口缺失、fragment 未输出颜色、顶点未输出位置、cbuffer 布局不支持的类型等）
  只有消息，`FileName` 为 `null`、`Line` 为 `0`（C++ 同样不携带位置）。
- 生成器命中第一个错误后不再记录后续错误（C++ 的 `m_error` 行为），但会继续走完生成流程以便定位。

---

## 3. GLSL 版本差异

同一份 HLSL 在不同 `Version` 下生成的差异（实现见 `Generation/GLSLGenerator.cs`）：

| 项目 | 110 | 120 | 140 / 150 / 330 | 100 ES | 300 ES |
| --- | --- | --- | --- | --- | --- |
| `#version` 行 | `#version 110` | `#version 120` | `#version 140/150/330`（140 额外输出 5 条 NVIDIA `#pragma optionNV`） | `#version 100` + `precision highp float;` | `#version 300 es` + `precision highp float;` + `precision highp sampler3D;` |
| 顶点输入/输出限定符 | `attribute` / `varying` | 同 110 | `in` / `out` | `attribute` / `varying` | `in` / `out` |
| 片元输出 | `gl_FragData[N]`（内置，不声明） | 同 110 | 生成 `out vec4 rast_FragData[N];` 并赋值 | `gl_FragData[N]` | 生成 `out vec4 rast_FragData[N];` |
| 纹理采样 | `texture2D` / `texture3D` / `textureCube`；`tex2Dproj` → `texture2DProj` | 同 110 | `texture` / `textureProj` | 同 110 | `texture` |
| `tex2Dlod` | `#extension GL_ARB_shader_texture_lod` + `texture2DLod` | 同 110 | `textureLod` | `#extension GL_EXT_shader_texture_lod` + `texture2DLodEXT` | `textureLod` |
| `tex2Dgrad` | `texture2DGradARB`（+ARB 扩展） | 同 110 | `textureGrad` | `texture2DGradEXT`（+EXT 扩展） | `textureGrad` |
| `texCUBElod` | `textureCubeLod`（需 110/120 才加扩展） | 同 110 | `textureLod` | `textureCubeLodEXT` | `textureLod` |
| `tex2Dbias` / `tex3Dbias` / `texCUBEbias` | 片元：`texture2D/3D/Cube(..., bias)`；顶点：去掉 bias 的 `texture2D/3D/Cube` | 同 110 | `texture(..., bias)`；顶点去掉 bias | 同 110 | 同 330 |
| 非方阵 `mat2x3` 等 `matrix_row` 重载 | 不生成（GLSL 1.10 无此类型） | 生成（1.20 起支持） | 生成 | 不生成（ES 1.00 无此类型） | 生成 |
| `uint/uvec` 的标量 swizzle 辅助重载 | 不生成 | 不生成 | 生成 | 不生成 | 生成 |
| 整数 `%` | 不支持，回退为 `floatN(mod(floatN(a), floatN(b)))` | 同 110 | 直接 `%` | 同 110 | 直接 `%` |
| `modf` | 手写 `ip = int(x); return x - ip;` | 同 110 | 原生 `modf(x, ip)` | 同 110 | 原生 `modf` |
| `float3x3(mat4)` 转换 | 用 `matrix_ctor` 辅助函数逐元素构造 | 同 110 | 直接 `mat3` 构造 | 直接构造 | 直接构造 |
| `ddx` / `ddy` | `dFdx` / `dFdy` | 同 110 | `dFdx` / `dFdy` | `#extension GL_OES_standard_derivatives : require` | `dFdx` / `dFdy` |

与版本无关、但值得注意的生成规则：

- 浮点 `%`：HLSL 允许浮点取模，GLSL 的 `%` 只接受整数，因此一律生成 `mod(a, b)`（同号操作数语义等价）。
- 向量比较：`<`/`>`/`<=`/`>=`/`==`/`!=` 在任一操作数为向量时生成 `lessThan/greaterThan/lessThanEqual/greaterThanEqual/equal/notEqual`。
- 标量 swizzle（`float3 x; x.xxx`）：生成 `m_scalar_swizzle3N(x)`。
- 矩阵行访问：HLSL `m[r][c]` ↔ GLSL `m[c][r]`，非数组矩阵下标生成 `matrix_rowN(m, i)`。
- 向量三目：条件为向量时生成 `bvecTernaryN(cond, a, b)`。
- 内建函数重映射：`tex2D/tex3D/texCUBE/tex2Dproj/tex2Dlod/tex2Dbias/tex2Dgrad/tex2DArray/tex3Dlod/tex3Dbias/texCUBEbias/texCUBElod`、
  `clip`、`saturate`→`clamp`、`mul`→`dot` 或乘法、`ldexp`→`x*exp2(e)`、`atan2`→`atan`、`lerp`→`mix`、`frac`→`fract`、
  `fmod`→`mod`、`sincos`、`modf`、`acos`/`asin`（边界外多项式近似）。
- 驱动兼容性：`rsqrt`/`sqrt`/`log`/`log2`/`log10`/`pow` 包裹 `abs()`（DX9 SM3 对负参数不返回 NaN）；`pow(x, 字面量 1)` 直接返回 `x`。
- 9 个 GLSL 保留字（`output`、`input`、`mod`、`mix`、`fract`、`dFdx`、`dFdy`、`filter`、`main`）会被改名为 `名字N`，
  其余辅助函数名（`matrix_rowN`、`bvecTernaryN` 等）通过 `ChooseUniqueName` 避开与 shader 内符号冲突。

---

## 4. 已知差异清单

判定原则：默认要求与 C++ **逐字节相等**；下列差异均为「语义等价 + 有测试覆盖」，或「C++ 侧是未定义行为，C# 做了保护」。

### 4.1 语言/运行时导致的差异

| # | 差异 | 说明 |
| --- | --- | --- |
| D1 | `ChooseUniqueName` 不再受 64 字节缓冲限制 | C++ 用 `char[64]`，超长名字会被截断；C# 用 `string`。仅当生成的辅助名超过 63 字节时才可能出现不同（现实输入不会触发） |
| D2 | `HLSLType?` 取代 `const HLSLType*` | C++ 的 `NULL` 语义（如 `OutputExpression` 的 `dstType`）对应 `null`，行为一致 |
| D3 | `CodeWriter` 只写 `\n` | C++ 也是 `\n`；若误用 `Environment.NewLine` 会导致与 golden 全线不一致，已由测试锁定 |
| D4 | 字符串"指针相等"改为值相等 | C++ 依赖字符串池驻留做 `const char*` 比较（宏别名、buffer 字段查找）；C# 用 `ReferenceEquals` + `String_.Equal`，语义等价 |
| D5 | C++ 未定义行为处的保护 | 预处理里 `value[0]`（空值/单字符）、`#endif` 多弹栈、`erase(len-1)` 等，C# 加了长度/`Count` 保护。正常输入下输出一致，异常输入下 C# 返回失败而不是崩溃/UB |
| D6 | 错误记录方式 | C++ `Log_Error` 是空实现（错误被吞掉）；C# 默认行为相同，但可注入 `IDiagnosticSink` 收集。生成器错误不带文件名/行号（与 C++ 一致） |
| D7 | 字符扫描按 UTF-16 `char` | C++ 按字节扫描 UTF-8。ASCII 输入完全一致；非 ASCII 标识符/注释的边界行为未保证（语料统一使用 UTF-8 无 BOM 的 ASCII 内容） |
| D8 | `#if` 仅支持整数字面量 | 与 C++ 相同的限制；`#if FOO` 会失败，失败点一致 |

### 4.2 与 C++ 版本同步的修复（两侧现在一致）

移植期间发现并**同时修复了 C++ 参考实现**，因此两边的行为是"修复后"的版本；若拿旧版 C++ 对比会看到差异：

| # | 问题 | 修复 |
| --- | --- | --- |
| F1 | `tex3Dlod` 缺少 `OutputIdentifier` 映射 | 生成的调用会输出未定义的 `tex3Dlod(...)`；现已映射到 `tex3DlodN` 包装函数 |
| F2 | `tex3Dbias` 完全没有实现 | 新增包装函数与名字映射（片元带 bias，顶点丢 bias） |
| F3 | `Version_110` 时 `Generate` 必定失败 | `#version` 分支的第二个 `if` 少写 `else`，110 会落进 `Unrecognized target version`；已改为 `else if` |
| F4 | legacy 顶点着色器的 bias 包装用了 `texture()` | `texture()` 是 GLSL 1.30+ 才有；110/120/100ES 改为 `texture2D`/`texture3D`/`textureCube` |
| F5 | GLSL 1.10 / ES 1.00 无条件输出非方阵 `matrix_row` 重载 | 这两版没有 `mat2x3` 等类型；现按版本裁剪（120 仍输出，行序不变，故 330/120 输出零变化） |
| F6 | ES 1.00 的 `tex2Dlod`/`tex2Dgrad` 选错扩展 | 判断顺序先看 `m_versionLegacy`（含 100_ES），导致 ES 1.00 走 `GL_ARB_shader_texture_lod` + `texture2DLod`；已改为先判 100_ES，走 `GL_EXT_shader_texture_lod` + `texture2DLodEXT`/`texture2DGradEXT` |

### 4.3 环境限制（不是移植差异）

| # | 限制 | 说明 |
| --- | --- | --- |
| L1 | ES 1.00 没有 `sampler3D` / `texture3D` | `Version100Es` 下 `tex3D*` 无法生成有效 GLSL；调用方应避免该组合 |
| L2 | 片元输出语义必须能映射到内置 | 非 `COLOR*` / `SV_Target*` 的片元 out 语义会报错（`Output attribute ... does not map to any built-ins`），与 C++ 相同 |
| L3 | `layout (std140)` 需要 GLSL 140+ | 未开启 `EmulateConstantBuffer` 且目标版本低于 140 时，生成的 `layout(std140) uniform` 不被旧版本接受；需调用方改用 `EmulateConstantBuffer` |
| L4 | `sampler2DMS` / `texelFetch` 需要 150+ / ES 3.10 | `tex2DMSfetch` 包装函数按原样生成，不按版本裁剪（与 C++ 相同） |

---

## 5. 测试与基线

```powershell
dotnet test csharp/HlslParser.sln
```

覆盖四类基线（全部与 C++ 产物逐字节比对）：

| 测试 | 基线位置 | 重新生成方式 |
| --- | --- | --- |
| token 流 | `tests/golden/tokens/*.hlsl` + `*.tokens` | `hlslgolden --in x.hlsl --tokens --out x.tokens` |
| 预处理 | `tests/golden/preprocess/*.hlsl` + `expected/*.glsl` | `hlslgolden --in x.hlsl --preprocess-only --out expected/x.glsl` |
| GLSL 生成 | `tests/golden/glsl/*.hlsl` + `*.args` + `expected/*.glsl` | `hlslgolden --in x.hlsl <args 里的参数> --out expected/x.glsl` |
| Milkdrop preset 语料 | `tests/golden/preset/*.hlsl` + `expected/*.glsl` | 见下 |

`hlslgolden` 是只依赖 `vendor/hlslparser` 的 C++ 命令行工具：

```powershell
powershell -File tools/hlslgolden/build.ps1      # 产出 tools/hlslgolden/bin/hlslgolden.exe
```

preset 语料的两步流程（抽取逻辑在测试工程里，见 `MilkdropPresetCorpus.cs`）：

```powershell
$env:HLSL_CORPUS_OUT="<repo>\csharp\tests\golden\preset"
dotnet test csharp/HlslParser.sln --filter DumpCorpusSources

Get-ChildItem csharp/tests/golden/preset/*.hlsl | ForEach-Object {
    tools\hlslgolden\bin\hlslgolden.exe --in $_.FullName --skip-preprocess `
        --entry PS --target fragment --version 330 --flags AlternateNanPropagation `
        --out ("csharp/tests/golden/preset/expected/" + $_.BaseName + ".glsl")
}

dotnet test csharp/HlslParser.sln --filter MilkdropPresetCorpusTests
```

`--skip-preprocess` 用于喂入"已被调用方预处理过"的文本，避免二次预处理造成差异。

当前状态：`dotnet test` 89 项全绿；GLSL 生成 6 个手工用例（330 / 110 / 120 / 100 ES，含 `tex3D*`、取模、`tex2Dlod`/`tex2Dgrad`）
+ 2 个真实 Milkdrop preset（`260-compshader-noise_lq`、`261-compshader-noisevol_lq`）与 C++ 逐字节一致。

---

## 6. 未实现范围

以下 C++ 代码**未移植**，因为它们不在 `Parse` → `Generate` 的转译链路上（详见 `plan.md` P8）：

- effect/technique 系统（`HLSLTechnique`/`HLSLPass`/annotation）仅作为 AST 节点存在，没有对应生成器；
- `namespace M4` 的语法树变换：`PruneTree`、`SortTree`、`GroupParameters`、`HideUnusedArguments`、
  `EmulateAlphaTest`、`FlattenExpressions`（仅 `ReplaceUniformsAssignments` 在生成路径上，已移植并接入 `Generate`）。

如后续需要"单文件多入口合并"、"模拟 D3D9 alpha test"或"绕开优化器嵌套表达式问题"，再按需补充。
