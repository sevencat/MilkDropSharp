using System.Numerics;

namespace MilkDrop.Classic.Render;

/// <summary>混合模式（对应 D3D9 的 ALPHABLENDENABLE + SRC/DESTBLEND 组合）。</summary>
public enum BlendMode
{
    /// <summary>不混合（Replace）。</summary>
    Replace,

    /// <summary>SrcAlpha / InvSrcAlpha（<c>milkdropfs.cpp:1931-1946</c> 的常规路径）。</summary>
    Alpha,

    /// <summary>SrcAlpha / One（加法，如 <c>milkdropfs.cpp:2648</c> 的波形叠加）。</summary>
    Additive,

    /// <summary>SrcColor / InvSrcColor（sprite 的 blendmode=3，<c>milkdropfs.cpp:3702-3703</c>）。</summary>
    SourceColor,

    /// <summary>
    /// <c>dst *= dst</c>（平方滤镜）。对应 <c>SRCBLEND=ZERO, DESTBLEND=DESTCOLOR</c>，
    /// 固定管线合成里 darken / brighten 的中段（<c>milkdropfs.cpp:4218-4242</c>）。
    /// </summary>
    MultiplyDest,

    /// <summary>
    /// <c>dst = 1 - dst</c>（反相）。对应 <c>SRCBLEND=INVDESTCOLOR, DESTBLEND=ZERO</c>
    /// （<c>milkdropfs.cpp:4213-4282</c> 的 invert，也是 brighten 的首尾两遍）。
    /// </summary>
    InvertDest,

    /// <summary>
    /// solarize 第一遍：<c>dst = 1 - dst</c>（<c>SRCBLEND=ZERO, DESTBLEND=INVDESTCOLOR</c>，
    /// <c>milkdropfs.cpp:4261-4263</c>）。
    /// </summary>
    SolarizeInvert,

    /// <summary>
    /// solarize 第二遍：<c>dst = src*dst + dst</c>（<c>SRCBLEND=DESTCOLOR, DESTBLEND=ONE</c>，
    /// <c>milkdropfs.cpp:4265-4267</c>）。顶点色为白色时相当于 <c>dst *= 2</c>。
    /// </summary>
    SolarizeAdd,
}

/// <summary>
/// 第 0 级纹理的 alpha 来源（对应 <c>D3DTSS_ALPHAOP/ALPHAARG1</c>）。
/// </summary>
/// <remarks>
/// 默认是 <see cref="Texture"/>（预设管线要纹理 alpha）；sprite 在
/// <c>milkdropfs.cpp:3452-3453</c> 明确改成顶点色 alpha，否则 <c>a</c> 变量完全不起作用。
/// </remarks>
public enum AlphaSource
{
    /// <summary>只取纹理 alpha。</summary>
    Texture,

    /// <summary>只取顶点色 alpha（<c>D3DTOP_SELECTARG1(D3DTA_DIFFUSE)</c>）。</summary>
    Vertex,

    /// <summary>顶点色 alpha × 纹理 alpha（<c>D3DTOP_MODULATE(DIFFUSE, TEXTURE)</c>，sprite 的 blendmode=4）。</summary>
    VertexTimesTexture,
}

/// <summary>
/// 渲染顶点。坐标是<b>像素空间</b>（左上原点、Y 向下），由后端转换为裁剪空间；
/// 布局对应 D3D9 的 <c>FVF = XYZ | DIFFUSE | TEX3</c>（40 字节，三个 float2 纹理坐标）。
/// </summary>
/// <remarks>
/// 第一个纹理坐标是主 UV；<see cref="UvOrig"/> 只在预设 warp shader 里用到
/// （对应原版 <c>MYVERTEX.tu_orig/tv_orig</c>）；<see cref="RadAng"/> 是
/// 供 shader 使用的极坐标（<c>rad</c>/<c>ang</c>）。
/// </remarks>
public readonly struct MilkVertex(Vector3 position, uint color, Vector2 texCoord, Vector2 uvOrig = default, Vector2 radAng = default)
{
    public readonly Vector3 Position = position;

    /// <summary>0xAARRGGBB。</summary>
    public readonly uint Color = color;

    /// <summary>主 UV（对应 <c>TEXCOORD0</c>）。</summary>
    public readonly Vector2 TexCoord = texCoord;

    /// <summary>未变形的 UV（对应 <c>TEXCOORD1</c>）。</summary>
    public readonly Vector2 UvOrig = uvOrig;

    /// <summary>极坐标 <c>rad</c>/<c>ang</c>（对应 <c>TEXCOORD2</c>）。</summary>
    public readonly Vector2 RadAng = radAng;
}

/// <summary>不透明纹理句柄。</summary>
public interface ITexture
{
    int Width { get; }

    int Height { get; }
}

public readonly record struct PixelRect(float X, float Y, float Width, float Height)
{
    public static PixelRect FromLtrb(float left, float top, float right, float bottom)
        => new(left, top, right - left, bottom - top);
}

/// <summary>
/// 着色器程序。常量按名字写入（内部映射到 D3D9 的 <c>c#</c> 寄存器），
/// 名字与寄存器的对应关系来自 shader 源码里的 <c>: register(cN)</c> 注解。
/// </summary>
public interface IShaderProgram : IDisposable
{
    string Name { get; }

    bool HasConstant(string name);

    /// <summary>查询 sampler 的寄存器阶段号（来自装配时写入的 <c>: register(sN)</c>）。</summary>
    bool TryGetSamplerStage(string name, out int stage);

    /// <summary>
    /// 源码里带 <c>: register(sN)</c> 的全部 sampler 名（含 include 的与预设自己声明的纹理 sampler）。
    /// </summary>
    IReadOnlyCollection<string> SamplerNames { get; }

    void SetFloat(string name, float value);

    void SetVector(string name, float x, float y, float z, float w);

    /// <summary>写多个 float4（矩阵用）：长度必须是 4 的倍数，每 4 个 float 占一个寄存器。</summary>
    void SetVectors(string name, ReadOnlySpan<float> packedFloats);
}

/// <summary>
/// 渲染后端抽象。**只暴露语义原语，不暴露 D3D9 API**，
/// 这样将来若要做 D3D11/Vulkan 后端，不需要改动 <c>MilkDrop.Classic.Visualizer</c>。
/// 设计依据见 <c>cbplan.md</c> 附录 B.3。
/// </summary>
public interface IRenderBackend : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>开始一帧（内部 BeginScene）。</summary>
    void BeginFrame();

    /// <summary>结束本帧的绘制（EndScene）。UI 叠加层画完后再调 <see cref="Present"/>。</summary>
    void EndFrame();

    /// <summary>呈现到窗口（Present）。</summary>
    void Present();

    void Clear(uint argb);

    /// <summary>只清颜色（离屏 RT 没有深度缓冲，不能带 ZBuffer 标志）。</summary>
    void ClearTarget(uint argb);

    /// <summary>画一张带纹理的四边形（对应 <c>BlitSurface*</c>/2D 绘制）。</summary>
    void DrawTexturedQuad(ITexture? texture, in PixelRect dest, uint color, BlendMode blend);

    /// <summary>三角形扇（对应固定功能 <c>D3DPT_TRIANGLEFAN</c>，用于 shape）。</summary>
    void DrawTriangleFan(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend);

    /// <summary>折线（对应 <c>D3DPT_LINESTRIP</c>，用于波形轮廓/shape 边框）。</summary>
    void DrawLineStrip(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend);

    /// <summary>
    /// 独立线段集合（对应 <c>D3DPT_LINELIST</c>）：第 0/1 个顶点是一段，2/3 是一段……
    /// motion vector 场用它（<c>milkdropfs.cpp:1301</c>），折线会把相邻线段错误地连起来。
    /// </summary>
    void DrawLineList(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend);

    /// <summary>
    /// 三角形条带（对应 <c>D3DPT_TRIANGLESTRIP</c>）。sprite 用它：四个角各带自己的 UV，
    /// repeat 会把 UV 推到 [0,1] 之外，所以不能用 <see cref="DrawTexturedQuad"/>。
    /// </summary>
    void DrawTriangleStrip(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend);

    /// <summary>设置第 0 级纹理的 alpha 来源（sprite 绘制前后切换，<c>milkdropfs.cpp:3452/3761</c>）。</summary>
    void SetAlphaSource(AlphaSource source);

    /// <summary>点列（对应 <c>D3DPT_POINTLIST</c> + <c>POINTSIZE</c>，用于波形点模式）。</summary>
    void DrawAdditivePoints(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, float pointSize, BlendMode blend);

    /// <summary>
    /// warp 网格（M4 实现，对应 <c>WarpedBlit_Shaders</c>/<c>WarpedBlit_NoShaders</c>）。
    /// </summary>
    /// <param name="bindTexture">
    /// 是否把 <paramref name="texture"/> 绑到采样器 0。走像素着色器时应传 false：
    /// 采样器 0 可能不是 <c>sampler_main</c>（装配时按用到的采样器紧凑分配），
    /// 由调用方先用 <see cref="SetTexture"/> 绑定好。
    /// </param>
    void DrawWarpGrid(ReadOnlySpan<MilkVertex> vertices, int gridX, int gridY, ITexture? texture, BlendMode blend, bool bindTexture = true);

    /// <summary>
    /// 绑定纹理到指定采样器阶段，并设置 wrap/filter（对应 <c>ApplyShaderParams</c> 里的
    /// <c>SetTexture</c> + <c>SetSamplerState</c>）。
    /// </summary>
    void SetTexture(int stage, ITexture? texture, bool wrap, bool bilinear);

    /// <summary>创建一张可被采样的纹理（噪点纹理等）；像素为 BGRA 顺序。</summary>
    ITexture CreateTexture(int width, int height, ReadOnlySpan<byte> bgraPixels);

    /// <summary>
    /// 创建一张 <c>size</c>³ 的 volume 纹理（内置噪声体纹理 <c>sampler_noisevol_lq/hq</c>）；
    /// 像素为 BGRA 顺序，按 z/y/x 排列。
    /// </summary>
    ITexture CreateVolumeTexture(int size, ReadOnlySpan<byte> bgraPixels);

    /// <summary>
    /// 覆盖整张纹理的像素（每帧变化的动态纹理，如 <c>sampler_fft</c> 的频谱）；像素为 BGRA 顺序，
    /// 尺寸必须与创建时一致。
    /// </summary>
    void UpdateTexture(ITexture texture, ReadOnlySpan<byte> bgraPixels);

    // ---- 着色器与离屏目标（Spike S1 / M5 需要）----

    /// <summary>创建离屏渲染目标（可被当作纹理采样）。</summary>
    ITexture CreateRenderTarget(int width, int height);

    /// <summary>把后续绘制重定向到离屏目标（并切换 viewport）。</summary>
    void PushRenderTarget(ITexture target);

    /// <summary>恢复到后备缓冲。</summary>
    void PopRenderTarget();

    IShaderProgram? CreatePixelShader(string name, string source, out string? error, string entryPoint = "PS", string profile = "ps_2_0");

    IShaderProgram? CreateVertexShader(string name, string source, out string? error, string entryPoint = "VS", string profile = "vs_2_0");

    /// <summary>绑定着色器（null = 固定功能管线）。</summary>
    void SetShaders(IShaderProgram? vertexShader, IShaderProgram? pixelShader);

    /// <summary>
    /// 用当前绑定的着色器画一张覆盖整个渲染目标的四边形。
    /// 顶点直接使用裁剪空间（配合直通 VS），因此不受"像素坐标"约定影响。
    /// </summary>
    void DrawFullscreenQuad(ITexture? texture, BlendMode blend);
}
