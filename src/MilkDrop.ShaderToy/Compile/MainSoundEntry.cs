namespace MilkDrop.ShaderToy.Compile;

/// <summary><c>mainSound</c> 的形参种类。</summary>
public enum MainSoundParameter
{
    /// <summary>当前时刻（秒）：<c>float time</c> / <c>float t</c>。</summary>
    Time,

    /// <summary>绝对采样序号：<c>int samp</c> / <c>int s</c>。</summary>
    SampleIndex,
}

/// <summary>
/// <c>mainSound</c> 的签名（按形参顺序）。
/// </summary>
/// <remarks>
/// 网页端现行写法是 <c>vec2 mainSound(float time)</c>，早期/导出文件常见
/// <c>vec2 mainSound(int samp, float time)</c>（仓库里的 <c>res/shadertoy/sound.txt</c> 就是后者）。
/// GLSL 没有重载，wrapper 只能按实际签名生成 —— 猜错就是编译失败或采样错位
/// （见 <c>shaderToyIssue.md</c> S6-1）。
/// </remarks>
public sealed class MainSoundSignature
{
    public MainSoundSignature(IReadOnlyList<MainSoundParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Parameters = parameters;
    }

    /// <summary>形参种类，按源码里的顺序。</summary>
    public IReadOnlyList<MainSoundParameter> Parameters { get; }

    /// <summary>是否给了绝对采样序号。</summary>
    public bool HasSampleIndex => Parameters.Contains(MainSoundParameter.SampleIndex);

    /// <summary>是否给了当前时刻。</summary>
    public bool HasTime => Parameters.Contains(MainSoundParameter.Time);

    /// <summary>形参个数。</summary>
    public int Count => Parameters.Count;

    /// <summary>可读的签名描述（日志/UI 用）。</summary>
    public string Describe() => "(" + string.Join(", ", Parameters.Select(
        p => p == MainSoundParameter.Time ? "float time" : "int samp")) + ")";
}

/// <summary>
/// <c>mainSound</c> 的签名探测与入口包装生成。
/// </summary>
/// <remarks>
/// 与 <see cref="ShaderToyTranspiler"/> 共用"先去掉注释再判断"的规则：注释里出现
/// <c>mainSound</c> 不能被当成真实定义（同 S3-7 的 <c>void main</c> 坑）。
/// </remarks>
public static class MainSoundEntry
{
    /// <summary>本批第一个样本的绝对序号（uniform）。</summary>
    public const string SampleIndexUniform = "ds_sampleIndex";

    /// <summary>批次目标宽度（uniform，决定 <c>gl_FragCoord</c> → 样本序号的换算）。</summary>
    public const string BatchWidthUniform = "ds_batchWidth";

    /// <summary>采样率（uniform）。</summary>
    public const string SampleRateUniform = "ds_sampleRate";

    /// <summary>包装里的局部变量名。</summary>
    public const string SampleIndexVariable = "ds_index";

    /// <summary>包装里的局部变量名。</summary>
    public const string SampleTimeVariable = "ds_time";

    private const string FunctionName = "mainSound";

    /// <summary>扫代码里的 <c>mainSound{...}</c>，按形参类型推出签名。</summary>
    /// <param name="code">GLSL 源码（可以带注释）。</param>
    /// <param name="signature">探测结果。</param>
    /// <param name="problem">失败原因。</param>
    public static bool TryDetect(string code, out MainSoundSignature? signature, out string? problem)
    {
        signature = null;
        problem = null;

        if (string.IsNullOrEmpty(code))
        {
            problem = "代码为空";
            return false;
        }

        string searchable = ShaderToyTranspiler.StripComments(code);

        int index = IndexOfIdentifier(searchable, FunctionName);
        if (index < 0)
        {
            problem = "代码里没有 mainSound";
            return false;
        }

        int cursor = index + FunctionName.Length;
        while (cursor < searchable.Length && char.IsWhiteSpace(searchable[cursor]))
        {
            cursor++;
        }

        if (cursor >= searchable.Length || searchable[cursor] != '(')
        {
            problem = "mainSound 后面不是参数表";
            return false;
        }

        int argsStart = cursor + 1;
        int argsEnd = FindClosingParenthesis(searchable, argsStart);
        if (argsEnd < 0)
        {
            problem = "mainSound 的参数表括号不配对";
            return false;
        }

        List<string> arguments = SplitTopLevel(searchable[argsStart..argsEnd]);
        if (arguments.Count == 0)
        {
            problem = "mainSound 没有形参";
            return false;
        }

        var parameters = new List<MainSoundParameter>(arguments.Count);
        foreach (string argument in arguments)
        {
            // 形参带 in/highp/const 修饰都不影响判断，只看类型里的 int/uint
            parameters.Add(ContainsIntType(argument) ? MainSoundParameter.SampleIndex : MainSoundParameter.Time);
        }

        signature = new MainSoundSignature(parameters);
        return true;
    }

    /// <summary>
    /// 生成 sound pass 的 <c>main()</c>：一个片元 = 一个采样点，输出 <c>(left, right, 0, 1)</c>。
    /// </summary>
    /// <remarks>
    /// 样本序号 = <c>ds_sampleIndex + y * ds_batchWidth + x</c>；时刻 = 序号 / 采样率。
    /// 这正是网页端"每个采样点算一次 <c>mainSound</c>"的语义，只是我们批量在 GPU 上算。
    /// </remarks>
    public static string BuildEntryPoint(MainSoundSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        string arguments = string.Join(", ", signature.Parameters.Select(p =>
            p == MainSoundParameter.Time ? SampleTimeVariable : SampleIndexVariable));

        return $$"""
            // ---- sound pass 包装（P6）：每个片元 = 一个采样点 ----
            uniform int {{SampleIndexUniform}};
            uniform int {{BatchWidthUniform}};
            uniform float {{SampleRateUniform}};

            void main()
            {
                int ds_offset = int(gl_FragCoord.y) * {{BatchWidthUniform}} + int(gl_FragCoord.x);
                int {{SampleIndexVariable}} = {{SampleIndexUniform}} + ds_offset;
                float {{SampleTimeVariable}} = float({{SampleIndexVariable}}) / {{SampleRateUniform}};

                vec2 ds_sample = {{FunctionName}}({{arguments}});

                // 网页端同样限幅：超范围直接爆音
                {{ShaderToyTranspiler.FragmentOutputName}} = vec4(clamp(ds_sample, -1.0, 1.0), 0.0, 1.0);
            }
            """;
    }

    /// <summary>没有 <c>mainSound</c> 时的静音入口。</summary>
    public static string BuildSilentEntryPoint() => $$"""
        void main()
        {
            {{ShaderToyTranspiler.FragmentOutputName}} = vec4(0.0, 0.0, 0.0, 1.0);
        }
        """;

    private static bool ContainsIntType(string parameter)
    {
        // "int" / "uint" / "highp int" 都算；"point" 之类不会出现在形参类型里
        var index = 0;
        while (index < parameter.Length)
        {
            if (!char.IsLetter(parameter[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < parameter.Length && char.IsLetterOrDigit(parameter[index]))
            {
                index++;
            }

            string token = parameter[start..index];
            if (token is "int" or "uint" or "ivec2")
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfIdentifier(string code, string identifier)
    {
        var index = 0;
        while (index < code.Length)
        {
            if (!char.IsLetter(code[index]) && code[index] != '_')
            {
                index++;
                continue;
            }

            int start = index;
            while (index < code.Length && (char.IsLetterOrDigit(code[index]) || code[index] == '_'))
            {
                index++;
            }

            if (code.AsSpan(start, index - start).SequenceEqual(identifier))
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>从 <paramref name="start"/>（左括号之后）找配对的右括号。</summary>
    private static int FindClosingParenthesis(string code, int start)
    {
        var depth = 1;
        for (int i = start; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>按顶层逗号切分（忽略括号/数组里的逗号）。</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddPart(parts, text[start..i]);
                start = i + 1;
            }
        }

        AddPart(parts, text[start..]);
        return parts;

        static void AddPart(List<string> target, string part)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                target.Add(trimmed);
            }
        }
    }
}
