using System.Numerics;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Framebuffer = MilkDrop.ProjectM.Renderer.Framebuffer;
using Sampler = MilkDrop.ProjectM.Renderer.Sampler;
using Shader = MilkDrop.ProjectM.Renderer.Shader;
using Texture = MilkDrop.ProjectM.Renderer.Texture;
using TextureSamplerDescriptor = MilkDrop.ProjectM.Renderer.TextureSamplerDescriptor;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 主纹理的多级模糊链：用两趟（水平 / 垂直）可分离的高斯近似，逐级把上一级再模糊一半。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/BlurTexture.hpp:26-114</c> 与 <c>BlurTexture.cpp</c>。</para>
/// <para>
/// 模糊纹理不进 <c>TextureManager</c>：每个预设（包括过渡期间的两个预设）都要有自己独立的一份。
/// 6 张纹理里只有奇数下标的那 3 张是用户可见的（<c>sampler_blur1..3</c>），偶数下标是中间结果。
/// </para>
/// </remarks>
public sealed class BlurTexture : RenderItem
{
    /// <summary>最大模糊趟数 / 纹理个数（3 级 × 2 趟）。</summary>
    public const int NumBlurTextures = 6;

    private static readonly float[] Weights = [4.0f, 3.8f, 3.5f, 2.9f, 1.9f, 1.2f, 0.7f, 0.3f];

    private readonly Shader _blur1Shader = new();
    private readonly Shader _blur2Shader = new();
    private readonly Framebuffer _blurFramebuffer = new(1);
    private readonly Sampler _blurSampler = new((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);
    private readonly Texture[] _blurTextures = new Texture[NumBlurTextures];

    private int _sourceTextureWidth;
    private int _sourceTextureHeight;
    private BlurLevel _blurLevel = BlurLevel.None;

    public BlurTexture()
    {
        MilkdropStaticShaders staticShaders = MilkdropStaticShaders.Get();

        _blur1Shader.CompileProgram(staticShaders.GetBlurVertexShader(), staticShaders.GetBlur1FragmentShader());
        _blur2Shader.CompileProgram(staticShaders.GetBlurVertexShader(), staticShaders.GetBlur2FragmentShader());

        _blurFramebuffer.CreateColorAttachment(0, 0);

        // 全屏 quad（两个三角形带顶点：pos.xy + uv.xy）。
        Init();

        // 先用空纹理占位，Update() 时按主纹理尺寸重建。
        for (int i = 0; i < NumBlurTextures; i++)
        {
            string textureName = i % 2 == 1 ? "blur" + (i / 2 + 1) : string.Empty;
            _blurTextures[i] = new Texture(textureName, 0u, GLEnum.Texture2D, 0, 0, false);
        }
    }

    /// <summary>当前要求的模糊级别（只升不降）。</summary>
    public BlurLevel CurrentBlurLevel => _blurLevel;

    /// <summary>只升不降地设置所需模糊级别（对应 <c>SetRequiredBlurLevel</c>）。</summary>
    public void SetRequiredBlurLevel(BlurLevel level)
        => _blurLevel = (BlurLevel)Math.Max((int)level, (int)_blurLevel);

    /// <summary>
    /// 给指定级别返回需要绑定的描述符（对应 <c>GetDescriptorsForBlurLevel</c>）。
    /// 纹理此时可以还没有内容，着色器声明只需要名字。
    /// </summary>
    public List<TextureSamplerDescriptor> GetDescriptorsForBlurLevel(BlurLevel blurLevel)
    {
        var descriptors = new List<TextureSamplerDescriptor>();

        if (blurLevel == BlurLevel.Blur3)
        {
            descriptors.Add(Descriptor(1));
            descriptors.Add(Descriptor(3));
            descriptors.Add(Descriptor(5));
        }

        if (blurLevel == BlurLevel.Blur2)
        {
            descriptors.Add(Descriptor(1));
            descriptors.Add(Descriptor(3));
        }

        if (blurLevel == BlurLevel.Blur1)
        {
            descriptors.Add(Descriptor(1));
        }

        return descriptors;
    }

    /// <summary>用主纹理生成所需的模糊级别（对应 <c>Update</c>）。</summary>
    public void Update(Texture sourceTexture, PerFrameContext perFrameContext)
    {
        ArgumentNullException.ThrowIfNull(sourceTexture);
        ArgumentNullException.ThrowIfNull(perFrameContext);

        if (_blurLevel == BlurLevel.None)
        {
            return;
        }

        if (sourceTexture.Width == 0 || sourceTexture.Height == 0)
        {
            return;
        }

        AllocateTextures(sourceTexture);

        int passes = (int)_blurLevel * 2;
        float blur1EdgeDarken = (float)perFrameContext.Blur1EdgeDarken.Value;

        Span<float> blurMin = stackalloc float[3];
        Span<float> blurMax = stackalloc float[3];
        GetSafeBlurMinMaxValues(perFrameContext, blurMin, blurMax);

        Span<float> scale = stackalloc float[3];
        Span<float> bias = stackalloc float[3];

        // 逐级算出把 [min..max] 映射到下一级区间所需的 scale/bias。
        scale[0] = 1.0f / (blurMax[0] - blurMin[0]);
        bias[0] = -blurMin[0] * scale[0];
        float tempMin = (blurMin[1] - blurMin[0]) / (blurMax[0] - blurMin[0]);
        float tempMax = (blurMax[1] - blurMin[0]) / (blurMax[0] - blurMin[0]);
        scale[1] = 1.0f / (tempMax - tempMin);
        bias[1] = -tempMin * scale[1];
        tempMin = (blurMin[2] - blurMin[1]) / (blurMax[1] - blurMin[1]);
        tempMax = (blurMax[2] - blurMin[1]) / (blurMax[1] - blurMin[1]);
        scale[2] = 1.0f / (tempMax - tempMin);
        bias[2] = -tempMin * scale[2];

        var gl = GlContext.Gl;

        int originalReadFramebuffer = gl.GetInteger(GLEnum.ReadFramebufferBinding);
        int originalDrawFramebuffer = gl.GetInteger(GLEnum.DrawFramebufferBinding);

        _blurFramebuffer.Bind(0);

        gl.BlendFunc(GLEnum.One, GLEnum.Zero);
        gl.BindVertexArray(VaoId);

        for (int pass = 0; pass < passes; pass++)
        {
            Texture target = _blurTextures[pass];
            if (target.TextureId == 0)
            {
                continue;
            }

            Shader blurShader = pass % 2 == 0 ? _blur1Shader : _blur2Shader;
            blurShader.Bind();
            blurShader.SetUniformInt("texture_sampler", 0);

            gl.Viewport(0, 0, (uint)target.Width, (uint)target.Height);

            // 源纹理：第一趟是主纹理，之后是上一趟的结果。
            if (pass == 0)
            {
                sourceTexture.Bind(0);
                blurShader.SetUniformInt("flipVertical", 1);
            }
            else
            {
                _blurTextures[pass - 1].Bind(0);
                blurShader.SetUniformInt("flipVertical", 0);
            }

            _blurSampler.Bind(0);

            float sourceWidth = pass == 0 ? sourceTexture.Width : _blurTextures[pass - 1].Width;
            float sourceHeight = pass == 0 ? sourceTexture.Height : _blurTextures[pass - 1].Height;

            var texSize = new Vector4(sourceWidth, sourceHeight, 1.0f / sourceWidth, 1.0f / sourceHeight);
            float scaleNow = scale[pass / 2];
            float biasNow = bias[pass / 2];

            if (pass % 2 == 0)
            {
                // 水平长 pass
                float w1 = Weights[0] + Weights[1];
                float w2 = Weights[2] + Weights[3];
                float w3 = Weights[4] + Weights[5];
                float w4 = Weights[6] + Weights[7];
                float d1 = 0f + 2f * Weights[1] / w1;
                float d2 = 2f + 2f * Weights[3] / w2;
                float d3 = 4f + 2f * Weights[5] / w3;
                float d4 = 6f + 2f * Weights[7] / w4;
                float wDiv = 0.5f / (w1 + w2 + w3 + w4);

                blurShader.SetUniformFloat4("_c0", texSize);
                blurShader.SetUniformFloat4("_c1", new Vector4(w1, w2, w3, w4));
                blurShader.SetUniformFloat4("_c2", new Vector4(d1, d2, d3, d4));
                blurShader.SetUniformFloat4("_c3", new Vector4(scaleNow, biasNow, wDiv, 0f));
            }
            else
            {
                // 垂直短 pass
                float w1 = Weights[0] + Weights[1] + Weights[2] + Weights[3];
                float w2 = Weights[4] + Weights[5] + Weights[6] + Weights[7];
                float d1 = 0f + 2f * ((Weights[2] + Weights[3]) / w1);
                float d2 = 2f + 2f * ((Weights[6] + Weights[7]) / w2);
                float wDiv = 1.0f / ((w1 + w2) * 2f);

                blurShader.SetUniformFloat4("_c0", texSize);
                blurShader.SetUniformFloat4("_c5", new Vector4(w1, w2, d1, d2));

                // 只在第一级的第一趟压暗边缘，否则高模糊级别会出现黑边。
                blurShader.SetUniformFloat4("_c6", pass == 1
                    ? new Vector4(wDiv, 1f - blur1EdgeDarken, blur1EdgeDarken, 5f)
                    : new Vector4(wDiv, 1f, 0f, 5f));
            }

            gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);

            // 直接把 framebuffer 内容拷进模糊纹理。
            target.Bind(0);
            gl.CopyTexSubImage2D(GLEnum.Texture2D, 0, 0, 0, 0, 0, (uint)target.Width, (uint)target.Height);
            target.Unbind(0);
        }

        gl.BindVertexArray(0);
        gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        gl.BindFramebuffer(GLEnum.ReadFramebuffer, (uint)originalReadFramebuffer);
        gl.BindFramebuffer(GLEnum.DrawFramebuffer, (uint)originalDrawFramebuffer);
        gl.Viewport(0, 0, (uint)sourceTexture.Width, (uint)sourceTexture.Height);

        Shader.Unbind();
    }

    /// <summary>
    /// 把用户可见的模糊纹理绑到从 <paramref name="unit"/> 开始的纹理单元（对应 <c>Bind</c>）。
    /// </summary>
    public void Bind(ref int unit, Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        int count = (int)_blurLevel * 2;
        for (int i = 0; i < count; i++)
        {
            if (i % 2 == 1)
            {
                _blurTextures[i].Bind(unit, _blurSampler);
                shader.SetUniformInt("sampler_blur" + (i / 2 + 1), unit);
                unit++;
            }
        }
    }

    /// <summary>
    /// 取经过范围检查的 blur min/max（对应 <c>GetSafeBlurMinMaxValues</c>）。
    /// </summary>
    /// <remarks>
    /// 与 4.1.8 逐行一致，包括 <c>blurMax[i] = avg - fMinDist * 0.5f</c> 这个看起来该是加号的地方——
    /// 为了让画面与原生一致，不在这里"修"（见 projectMIssue.md I-27）。
    /// </remarks>
    public static void GetSafeBlurMinMaxValues(PerFrameContext perFrameContext, Span<float> blurMin, Span<float> blurMax)
    {
        ArgumentNullException.ThrowIfNull(perFrameContext);

        blurMin[0] = (float)perFrameContext.Blur1Min.Value;
        blurMin[1] = (float)perFrameContext.Blur2Min.Value;
        blurMin[2] = (float)perFrameContext.Blur3Min.Value;
        blurMax[0] = (float)perFrameContext.Blur1Max.Value;
        blurMax[1] = (float)perFrameContext.Blur2Max.Value;
        blurMax[2] = (float)perFrameContext.Blur3Max.Value;

        // 后面几级的区间不能比前一级宽；区间太窄就撑开，避免精度被浪费。
        const float minDistance = 0.1f;

        if (blurMax[0] - blurMin[0] < minDistance)
        {
            float avg = (blurMin[0] + blurMax[0]) * 0.5f;
            blurMin[0] = avg - minDistance * 0.5f;
            blurMax[0] = avg - minDistance * 0.5f;
        }

        blurMax[1] = Math.Min(blurMax[0], blurMax[1]);
        blurMin[1] = Math.Max(blurMin[0], blurMin[1]);
        if (blurMax[1] - blurMin[1] < minDistance)
        {
            float avg = (blurMin[1] + blurMax[1]) * 0.5f;
            blurMin[1] = avg - minDistance * 0.5f;
            blurMax[1] = avg - minDistance * 0.5f;
        }

        blurMax[2] = Math.Min(blurMax[1], blurMax[2]);
        blurMin[2] = Math.Max(blurMin[1], blurMin[2]);
        if (blurMax[2] - blurMin[2] < minDistance)
        {
            float avg = (blurMin[2] + blurMax[2]) * 0.5f;
            blurMin[2] = avg - minDistance * 0.5f;
            blurMax[2] = avg - minDistance * 0.5f;
        }
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        ReadOnlySpan<float> points =
        [
            -1.0f, -1.0f, 0.0f, 0.0f,
            1.0f, -1.0f, 1.0f, 0.0f,
            -1.0f, 1.0f, 0.0f, 1.0f,
            1.0f, 1.0f, 1.0f, 1.0f,
        ];

        gl.BufferData<float>(GLEnum.ArrayBuffer, points, GLEnum.StaticDraw);

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)0);
        gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            foreach (Texture texture in _blurTextures)
            {
                texture.Dispose();
            }

            _blurSampler.Dispose();
            _blur1Shader.Dispose();
            _blur2Shader.Dispose();
            _blurFramebuffer.Dispose();
        }

        base.Dispose();
    }

    private TextureSamplerDescriptor Descriptor(int index)
        => new(_blurTextures[index], _blurSampler, _blurTextures[index].Name, string.Empty);

    private void AllocateTextures(Texture sourceTexture)
    {
        int width = sourceTexture.Width;
        int height = sourceTexture.Height;

        if (width > 0 && height > 0 && width == _sourceTextureWidth && height == _sourceTextureHeight)
        {
            return;
        }

        for (int i = 0; i < NumBlurTextures; i++)
        {
            // 主纹理 = 1024 时：blur0=512、blur1=256（用户看到的 blur1）、blur2=128、
            // blur3=128（blur2）、blur4=64、blur5=64（blur3）。
            if ((i & 1) == 0 || i < 2)
            {
                width = Math.Max(16, width / 2);
                height = Math.Max(16, height / 2);
            }

            int alignedWidth = (width + 3) / 16 * 16;
            int alignedHeight = (height + 3) / 4 * 4;

            if (i == 0)
            {
                // FBO 只需要能容纳模糊纹理的尺寸。
                _blurFramebuffer.SetSize(alignedWidth, alignedHeight);
            }

            string textureName = i % 2 == 1 ? "blur" + (i / 2 + 1) : string.Empty;
            _blurTextures[i].Dispose();
            _blurTextures[i] = new Texture(textureName, alignedWidth, alignedHeight, false);
        }

        _sourceTextureWidth = sourceTexture.Width;
        _sourceTextureHeight = sourceTexture.Height;
    }
}
