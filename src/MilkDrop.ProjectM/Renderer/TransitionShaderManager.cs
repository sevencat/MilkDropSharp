namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 编译并持有 6 个内置转场着色器，按需随机挑一个（对应 <c>Renderer/TransitionShaderManager</c>）。
/// </summary>
public sealed class TransitionShaderManager : IDisposable
{
    private readonly List<Shader> _transitionShaders = [];
    private readonly Random _random = new();

    public TransitionShaderManager()
    {
        _transitionShaders.Add(CompileTransitionShader(TransitionShaderSources.Circle));
        _transitionShaders.Add(CompileTransitionShader(TransitionShaderSources.Plasma));
        _transitionShaders.Add(CompileTransitionShader(TransitionShaderSources.SimpleBlend));
        _transitionShaders.Add(CompileTransitionShader(TransitionShaderSources.Sweep));
        _transitionShaders.Add(CompileTransitionShader(TransitionShaderSources.Warp));
        _transitionShaders.Add(CompileTransitionShader(TransitionShaderSources.ZoomBlur));
    }

    /// <summary>已成功编译的转场数量。</summary>
    public int Count => _transitionShaders.Count;

    /// <summary>随机取一个转场着色器；一个都没编译成功时返回 null。</summary>
    public Shader? RandomTransition()
    {
        if (_transitionShaders.Count == 0)
        {
            return null;
        }

        return _transitionShaders[_random.Next(_transitionShaders.Count)];
    }

    public void Dispose()
    {
        foreach (Shader shader in _transitionShaders)
        {
            shader.Dispose();
        }

        _transitionShaders.Clear();
    }

    /// <summary>
    /// 把公共头 + <c>mainImage</c> 实现 + 尾巴拼成完整片元着色器并编译
    /// （对应 <c>CompileTransitionShader</c>）。
    /// </summary>
    private static Shader CompileTransitionShader(string shaderBodyCode)
    {
        string fragmentShaderSource = TransitionShaderSources.VersionHeader +
                                      TransitionShaderSources.Header + "\n" +
                                      shaderBodyCode + "\n" +
                                      TransitionShaderSources.Main;

        var shader = new Shader();
        shader.CompileProgram(TransitionShaderSources.VersionHeader + TransitionShaderSources.VertexShader,
            fragmentShaderSource);
        return shader;
    }
}
