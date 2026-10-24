namespace HlslParser;

public enum GlslTarget
{
    VertexShader,
    FragmentShader,
}

public enum GlslVersion
{
    Version110,
    Version120,
    Version140,
    Version150,
    Version330,
    Version100Es,
    Version300Es,
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

public sealed class TranslationRequest
{
    public required string Source { get; init; }
    public string FileName { get; init; } = "";
    public string EntryPoint { get; init; } = "PS";
    public GlslTarget Target { get; init; } = GlslTarget.FragmentShader;
    public GlslVersion Version { get; init; } = GlslVersion.Version330;
    public GlslFlags Flags { get; init; }
    public bool Preprocess { get; init; } = true;
    public string ConstantBufferPrefix { get; init; } = "";

    /// <summary>预处理结果是否保留在结果中（便于排查 preset）。</summary>
    public bool ReturnPreprocessedSource { get; init; }
}

public sealed class TranslationResult
{
    public bool Success { get; init; }
    public string Glsl { get; init; }
    public string PreprocessedSource { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
}

/// <summary>
/// Upper layer convenience wrapper: preprocess → parse → generate, mirroring what
/// projectM's <c>MilkdropShader::TranspileHLSLShader</c> drives through the C++
/// hlslparser API. The preset specific text surgery (dropping <c>sampler_*</c>/
/// <c>texsize_*</c> declarations, rewriting <c>shader_body</c>) is the caller's
/// job in both implementations, so it is intentionally not done here.
/// </summary>
public sealed class HlslToGlslTranslator
{
    /// <summary>
    /// 转译期间独占的锁。
    /// </summary>
    /// <remarks>
    /// 从 C++ hlslparser 移植过来的这套代码里有若干进程级静态表（保留字、内建函数表、类型表），
    /// 并发转译会偶发 "HLSL parsing failed"（xunit 并行跑测试时踩到过）。
    /// 单次转译只有几毫秒，串行化的代价可以忽略，换掉这个偶发问题值得。
    /// </remarks>
    private static readonly object TranslationGate = new();

    public TranslationResult Translate(TranslationRequest request)
    {
        lock (TranslationGate)
        {
            return TranslateCore(request);
        }
    }

    private TranslationResult TranslateCore(TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        var diagnostics = new CollectingDiagnosticSink();

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var parser = new HLSLParser(allocator, tree, diagnostics);

        string preprocessed = request.Source;
        if (request.Preprocess)
        {
            if (!parser.ApplyPreprocessor(request.FileName ?? "", request.Source, out preprocessed))
            {
                return Failure(diagnostics, request.ReturnPreprocessedSource ? preprocessed : null);
            }
        }

        if (!parser.Parse(request.FileName ?? "", preprocessed))
        {
            return Failure(diagnostics, request.ReturnPreprocessedSource ? preprocessed : null);
        }

        var generator = new GLSLGenerator(diagnostics);
        var options = new GLSLGenerator.Options((int)request.Flags)
        {
            ConstantBufferPrefix = request.ConstantBufferPrefix ?? "",
        };

        if (!generator.Generate(tree, ToGeneratorTarget(request.Target), ToGeneratorVersion(request.Version), request.EntryPoint, options))
        {
            return Failure(diagnostics, request.ReturnPreprocessedSource ? preprocessed : null);
        }

        return new TranslationResult
        {
            Success = true,
            Glsl = generator.GetResult(),
            PreprocessedSource = request.ReturnPreprocessedSource ? preprocessed : null,
            Diagnostics = diagnostics.Diagnostics.ToArray(),
        };
    }

    private static TranslationResult Failure(CollectingDiagnosticSink diagnostics, string preprocessed)
    {
        return new TranslationResult
        {
            Success = false,
            Glsl = null,
            PreprocessedSource = preprocessed,
            Diagnostics = diagnostics.Diagnostics.ToArray(),
        };
    }

    private static GLSLGenerator.Target ToGeneratorTarget(GlslTarget target)
    {
        return target == GlslTarget.VertexShader
            ? GLSLGenerator.Target.Target_VertexShader
            : GLSLGenerator.Target.Target_FragmentShader;
    }

    private static GLSLGenerator.Version ToGeneratorVersion(GlslVersion version)
    {
        return version switch
        {
            GlslVersion.Version110 => GLSLGenerator.Version.Version_110,
            GlslVersion.Version120 => GLSLGenerator.Version.Version_120,
            GlslVersion.Version140 => GLSLGenerator.Version.Version_140,
            GlslVersion.Version150 => GLSLGenerator.Version.Version_150,
            GlslVersion.Version330 => GLSLGenerator.Version.Version_330,
            GlslVersion.Version100Es => GLSLGenerator.Version.Version_100_ES,
            GlslVersion.Version300Es => GLSLGenerator.Version.Version_300_ES,
            _ => GLSLGenerator.Version.Version_330,
        };
    }
}
