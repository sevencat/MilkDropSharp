namespace HlslParser;

/// <summary>
/// Effect syntax (sampler_state / technique / pass / pipeline / stage).
///
/// TODO(P3c): not ported yet. The Milkdrop preset path never hits these
/// statements (projectM strips sampler_state blocks and presets contain no
/// techniques), so the parser reports a clear error instead of silently
/// mis-parsing. See plan.md (P3c) for the remaining work, which needs the
/// effect state tables from HLSLParser.cpp.
/// </summary>
public partial class HLSLParser
{
    internal bool ParseSamplerState(out HLSLExpression expression)
    {
        expression = null;
        _tokenizer.Error("sampler_state declarations are not supported yet");
        return false;
    }

    internal bool ParseTechnique(out HLSLStatement statement)
    {
        statement = null;
        return false;
    }

    internal bool ParsePipeline(out HLSLStatement statement)
    {
        statement = null;
        return false;
    }

    internal bool ParseStage(out HLSLStatement statement)
    {
        statement = null;
        return false;
    }
}
