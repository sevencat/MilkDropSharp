using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 一个纹理采样器对象（wrap + filter）。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/Sampler.hpp:24-85</c> 与 <c>Sampler.cpp:6-67</c>。</para>
/// <para>
/// projectM 里每个纹理可以带多个不同 wrap/filter 组合的采样器；因为不用 mipmap，
/// min 与 mag 用同一个过滤模式。
/// </para>
/// </remarks>
public sealed class Sampler : IDisposable
{
    private uint _samplerId;
    private int _wrapMode;
    private int _filterMode;

    /// <param name="wrapMode">GL wrap 模式，例如 <c>GL_CLAMP_TO_EDGE</c>。</param>
    /// <param name="filterMode">GL 过滤模式，例如 <c>GL_NEAREST</c>。</param>
    public Sampler(int wrapMode, int filterMode)
    {
        _wrapMode = wrapMode;
        _filterMode = filterMode;

        var gl = GlContext.Gl;
        _samplerId = gl.GenSamplers(1);
        gl.SamplerParameter(_samplerId, GLEnum.TextureMinFilter, filterMode);
        gl.SamplerParameter(_samplerId, GLEnum.TextureMagFilter, filterMode);
        gl.SamplerParameter(_samplerId, GLEnum.TextureWrapS, wrapMode);
        gl.SamplerParameter(_samplerId, GLEnum.TextureWrapT, wrapMode);
    }

    /// <summary>采样器对象的 GL 名字。</summary>
    public uint SamplerId => _samplerId;

    /// <summary>把采样器绑到指定纹理单元。</summary>
    public void Bind(uint unit) => GlContext.Gl.BindSampler(unit, _samplerId);

    /// <summary>解绑指定纹理单元上的采样器。</summary>
    public static void Unbind(uint unit) => GlContext.Gl.BindSampler(unit, 0);

    public int WrapMode
    {
        get => _wrapMode;
        set
        {
            if (value == _wrapMode)
            {
                return;
            }

            _wrapMode = value;
            var gl = GlContext.Gl;
            gl.SamplerParameter(_samplerId, GLEnum.TextureWrapS, value);
            gl.SamplerParameter(_samplerId, GLEnum.TextureWrapT, value);
        }
    }

    public int FilterMode
    {
        get => _filterMode;
        set
        {
            if (value == _filterMode)
            {
                return;
            }

            _filterMode = value;
            var gl = GlContext.Gl;
            gl.SamplerParameter(_samplerId, GLEnum.TextureMinFilter, value);
            gl.SamplerParameter(_samplerId, GLEnum.TextureMagFilter, value);
        }
    }

    public void Dispose()
    {
        if (_samplerId == 0)
        {
            return;
        }

        GlContext.Gl.DeleteSamplers([_samplerId]);
        _samplerId = 0;
    }
}
