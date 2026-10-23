using System.Numerics;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// 能直接作为 D3D9 采样源绑定的纹理（2D 纹理与 volume 纹理共用一条绑定路径）。
/// </summary>
internal interface ID3D9TextureSource
{
    IDirect3DBaseTexture9 BaseTexture { get; }
}

/// <summary>D3D9 纹理句柄。</summary>
public sealed class D3D9Texture : ITexture, IDisposable, ID3D9TextureSource
{
    private IDirect3DSurface9? _renderTargetSurface;

    public D3D9Texture(IDirect3DTexture9 texture, int width, int height, bool isRenderTarget = false)
    {
        Texture = texture;
        Width = width;
        Height = height;
        IsRenderTarget = isRenderTarget;
        if (isRenderTarget)
        {
            _renderTargetSurface = texture.GetSurfaceLevel(0);
        }
    }

    IDirect3DBaseTexture9 ID3D9TextureSource.BaseTexture => Texture;

    public IDirect3DTexture9 Texture { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsRenderTarget { get; }

    public IDirect3DSurface9 RenderTargetSurface
        => _renderTargetSurface ?? throw new InvalidOperationException("该纹理不是渲染目标。");

    public void Dispose()
    {
        _renderTargetSurface?.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// D3D9 volume（3D）纹理句柄：内置噪声体纹理 <c>sampler_noisevol_lq/hq</c> 用它
/// （对应 <c>AddNoiseVol</c> 里的 <c>CreateVolumeTexture</c>）。
/// </summary>
public sealed class D3D9VolumeTexture : ITexture, IDisposable, ID3D9TextureSource
{
    public D3D9VolumeTexture(IDirect3DVolumeTexture9 texture, int width, int height, int depth)
    {
        VolumeTexture = texture;
        Width = width;
        Height = height;
        Depth = depth;
    }

    public IDirect3DVolumeTexture9 VolumeTexture { get; }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    IDirect3DBaseTexture9 ID3D9TextureSource.BaseTexture => VolumeTexture;

    public void Dispose() => VolumeTexture.Dispose();
}

/// <summary>
/// 固定功能管线的 D3D9 后端。这是 v1 唯一的后端（见 <c>cbplan.md</c> §2.1）。
/// </summary>
/// <remarks>
/// 坐标约定：<see cref="MilkVertex"/> 传入的是像素坐标（左上原点，Y 向下），
/// 这里统一转换为裁剪空间，因此全程不需要 <c>SetTransform</c>。
/// </remarks>
public sealed class D3D9RenderBackend : IRenderBackend
{
    private readonly D3D9Context _context;

    /// <summary>逐行绘制 warp 网格时复用的顶点缓冲（避免每帧分配）。</summary>
    private MilkVertex[] _warpRowScratch = [];

    public D3D9RenderBackend(D3D9Context context) => _context = context;

    public D3D9Context Context => _context;

    public int Width => _context.Width;

    public int Height => _context.Height;

    public void BeginFrame()
    {
        _context.BeginScene();
        _context.SetViewport();
        ApplyCommonState();
    }

    public void EndFrame() => _context.EndScene();

    public void Present() => _context.Present();

    public void Clear(uint argb) => _context.Clear(argb);

    public void ClearTarget(uint argb)
        => _context.Device.Clear(ClearFlags.Target, new Color(argb), 1f, 0);

    public void DrawTexturedQuad(ITexture? texture, in PixelRect dest, uint color, BlendMode blend)
    {
        ApplyBlend(blend);
        BindTexture(texture);

        var x1 = dest.X / Width * 2f - 1f;
        var y1 = 1f - dest.Y / Height * 2f;
        var x2 = (dest.X + dest.Width) / Width * 2f - 1f;
        var y2 = 1f - (dest.Y + dest.Height) / Height * 2f;
        var z = 0f;

        Span<MilkVertex> quad =
        [
            new(new Vector3(x1, y1, z), color, new Vector2(0f, 0f)),
            new(new Vector3(x2, y1, z), color, new Vector2(1f, 0f)),
            new(new Vector3(x1, y2, z), color, new Vector2(0f, 1f)),
            new(new Vector3(x2, y2, z), color, new Vector2(1f, 1f)),
        ];

        DrawPrimitiveUp(PrimitiveType.TriangleStrip, quad, 2);
    }

    public void DrawTriangleFan(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend)
    {
        if (vertices.Length < 3)
        {
            return;
        }

        ApplyBlend(blend);
        BindTexture(texture);
        DrawPixelSpacePrimitive(PrimitiveType.TriangleFan, vertices, (uint)(vertices.Length - 2));
    }

    public void DrawLineStrip(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend)
    {
        if (vertices.Length < 2)
        {
            return;
        }

        ApplyBlend(blend);
        BindTexture(texture);
        DrawPixelSpacePrimitive(PrimitiveType.LineStrip, vertices, (uint)(vertices.Length - 1));
    }

    public void DrawLineList(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend)
    {
        if (vertices.Length < 2)
        {
            return;
        }

        ApplyBlend(blend);
        BindTexture(texture);
        DrawPixelSpacePrimitive(PrimitiveType.LineList, vertices, (uint)(vertices.Length / 2));
    }

    public void DrawTriangleStrip(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, BlendMode blend)
    {
        if (vertices.Length < 3)
        {
            return;
        }

        ApplyBlend(blend);
        BindTexture(texture);
        DrawPixelSpacePrimitive(PrimitiveType.TriangleStrip, vertices, (uint)(vertices.Length - 2));
    }

    public void SetAlphaSource(AlphaSource source)
    {
        var device = _context.Device;
        switch (source)
        {
            case AlphaSource.Vertex:
                device.SetTextureStageState(0, TextureStage.AlphaOperation, (int)TextureOperation.SelectArg1);
                device.SetTextureStageState(0, TextureStage.AlphaArg1, (int)TextureArgument.Diffuse);
                break;

            case AlphaSource.VertexTimesTexture:
                device.SetTextureStageState(0, TextureStage.AlphaOperation, (int)TextureOperation.Modulate);
                device.SetTextureStageState(0, TextureStage.AlphaArg1, (int)TextureArgument.Diffuse);
                device.SetTextureStageState(0, TextureStage.AlphaArg2, (int)TextureArgument.Texture);
                break;

            default:
                // 与 ApplyCommonState 的"安全默认"一致
                device.SetTextureStageState(0, TextureStage.AlphaOperation, (int)TextureOperation.SelectArg1);
                device.SetTextureStageState(0, TextureStage.AlphaArg1, (int)TextureArgument.Texture);
                break;
        }
    }

    public void DrawAdditivePoints(ReadOnlySpan<MilkVertex> vertices, ITexture? texture, float pointSize, BlendMode blend)
    {
        if (vertices.Length == 0)
        {
            return;
        }

        ApplyBlend(blend);
        BindTexture(texture);
        _context.Device.SetRenderState(RenderState.PointSize, pointSize);
        DrawPixelSpacePrimitive(PrimitiveType.PointList, vertices, (uint)vertices.Length);
    }

    /// <summary>
    /// 对应 <c>WarpedBlit_NoShaders</c>（milkdropfs.cpp:1866 起）里的网格绘制：
    /// 顶点按行拆成三角形条带（每行 <c>gridX*2</c> 个三角形）。
    /// </summary>
    /// <remarks>
    /// UV 已经在 <c>WarpMesh</c> 里算好，这里只负责像素坐标 → 裁剪空间。
    /// 每行一次 <c>DrawPrimitiveUP</c> 是有意的：网格只有 64x64，
    /// 换 index buffer 反而要多维护一份索引。
    /// </remarks>
    public void DrawWarpGrid(ReadOnlySpan<MilkVertex> vertices, int gridX, int gridY, ITexture? texture, BlendMode blend, bool bindTexture = true)
    {
        if (gridX < 1 || gridY < 1)
        {
            return;
        }

        var needed = (gridX + 1) * (gridY + 1);
        if (vertices.Length < needed)
        {
            throw new ArgumentException($"warp 网格需要 {needed} 个顶点", nameof(vertices));
        }

        ApplyBlend(blend);
        if (bindTexture)
        {
            BindTexture(texture);
        }

        var rowLength = (gridX + 1) * 2;
        if (_warpRowScratch.Length < rowLength)
        {
            _warpRowScratch = new MilkVertex[rowLength];
        }

        var row = _warpRowScratch.AsSpan(0, rowLength);
        var w = Width;
        var h = Height;

        for (var y = 0; y < gridY; y++)
        {
            var top = y * (gridX + 1);
            var bottom = top + gridX + 1;

            for (var x = 0; x <= gridX; x++)
            {
                row[x * 2] = ToClip(vertices[top + x], w, h);
                row[(x * 2) + 1] = ToClip(vertices[bottom + x], w, h);
            }

            DrawPrimitiveUp(PrimitiveType.TriangleStrip, row, (uint)(gridX * 2));
        }
    }

    public void SetTexture(int stage, ITexture? texture, bool wrap, bool bilinear)
    {
        var device = _context.Device;
        device.SetTexture(stage, texture is ID3D9TextureSource source ? source.BaseTexture : null);

        if (texture is null)
        {
            return;
        }

        var address = wrap ? TextureAddress.Wrap : TextureAddress.Clamp;
        var filter = bilinear ? TextureFilter.Linear : TextureFilter.Point;

        device.SetSamplerState(stage, SamplerState.AddressU, (int)address);
        device.SetSamplerState(stage, SamplerState.AddressV, (int)address);
        device.SetSamplerState(stage, SamplerState.AddressW, (int)address);
        device.SetSamplerState(stage, SamplerState.MagFilter, (int)filter);
        device.SetSamplerState(stage, SamplerState.MinFilter, (int)filter);
        device.SetSamplerState(stage, SamplerState.MipFilter, (int)filter);
    }

    public ITexture CreateTexture(int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        var texture = _context.CreateTextureFromBgra(width, height, bgraPixels);
        return new D3D9Texture(texture, width, height);
    }

    public ITexture CreateVolumeTexture(int size, ReadOnlySpan<byte> bgraPixels)
    {
        var texture = _context.CreateVolumeTextureFromBgra(size, bgraPixels);
        return new D3D9VolumeTexture(texture, size, size, size);
    }

    public void UpdateTexture(ITexture texture, ReadOnlySpan<byte> bgraPixels)
    {
        if (texture is not D3D9Texture d3d9)
        {
            throw new ArgumentException("必须传入本后端创建的纹理。", nameof(texture));
        }

        _context.UpdateTextureFromBgra(d3d9.Texture, d3d9.Width, d3d9.Height, bgraPixels);
    }

    public ITexture CreateRenderTarget(int width, int height)
    {
        var texture = _context.CreateRenderTargetTexture(width, height);
        return new D3D9Texture(texture, width, height, isRenderTarget: true);
    }

    public void PushRenderTarget(ITexture target)
    {
        if (target is not D3D9Texture { IsRenderTarget: true } d3d9)
        {
            throw new ArgumentException("必须传入 CreateRenderTarget 返回的纹理。", nameof(target));
        }

        _context.SetRenderTarget(d3d9.RenderTargetSurface, d3d9.Width, d3d9.Height);
    }

    public void PopRenderTarget() => _context.SetRenderTarget(null, Width, Height);

    public IShaderProgram? CreatePixelShader(string name, string source, out string? error, string entryPoint = "PS", string profile = "ps_2_0")
        => D3D9ShaderProgram.CreatePixel(_context.Device, name, source, out error, entryPoint, profile);

    public IShaderProgram? CreateVertexShader(string name, string source, out string? error, string entryPoint = "VS", string profile = "vs_2_0")
        => D3D9ShaderProgram.CreateVertex(_context.Device, name, source, out error, entryPoint, profile);

    public void SetShaders(IShaderProgram? vertexShader, IShaderProgram? pixelShader)
    {
        if (vertexShader is D3D9ShaderProgram vs)
        {
            vs.Bind();
        }
        else
        {
            _context.Device.VertexShader = null;
        }

        if (pixelShader is D3D9ShaderProgram ps)
        {
            ps.Bind();
        }
        else
        {
            _context.Device.PixelShader = null;
        }
    }

    public void DrawFullscreenQuad(ITexture? texture, BlendMode blend)
    {
        ApplyBlend(blend);
        BindTexture(texture);

        // 裁剪空间：D3D9 的 y 轴向上，所以左上角是 (-1, 1)。
        Span<MilkVertex> quad =
        [
            new(new Vector3(-1f, 1f, 0f), 0xFFFFFFFF, new Vector2(0f, 0f)),
            new(new Vector3(1f, 1f, 0f), 0xFFFFFFFF, new Vector2(1f, 0f)),
            new(new Vector3(-1f, -1f, 0f), 0xFFFFFFFF, new Vector2(0f, 1f)),
            new(new Vector3(1f, -1f, 0f), 0xFFFFFFFF, new Vector2(1f, 1f)),
        ];

        DrawPrimitiveUp(PrimitiveType.TriangleStrip, quad, 2);
    }

    public void Dispose() => _context.Dispose();

    // ---- 内部 ----

    private void ApplyCommonState()
    {
        var device = _context.Device;

        device.SetRenderState(RenderState.ZEnable, false);
        device.SetRenderState(RenderState.ZWriteEnable, false);
        device.SetRenderState(RenderState.CullMode, Cull.None);
        device.SetRenderState(RenderState.Lighting, false);
        device.SetRenderState(RenderState.SpecularEnable, false);
        device.SetRenderState(RenderState.AlphaBlendEnable, true);
        device.SetRenderState(RenderState.AlphaTestEnable, false);
        device.SetRenderState(RenderState.ScissorTestEnable, false);
        device.SetRenderState(RenderState.PointScaleEnable, false);

        device.SetSamplerState(0, SamplerState.MinFilter, (int)TextureFilter.Linear);
        device.SetSamplerState(0, SamplerState.MagFilter, (int)TextureFilter.Linear);
        device.SetSamplerState(0, SamplerState.MipFilter, (int)TextureFilter.None);
        device.SetSamplerState(0, SamplerState.AddressU, (int)TextureAddress.Wrap);
        device.SetSamplerState(0, SamplerState.AddressV, (int)TextureAddress.Wrap);

        // 对应 D3DTSS_COLOROP = MODULATE(TEXTURE, DIFFUSE)：
        // MilkDrop 的 decay 正是靠"顶点色 × 纹理"实现的（milkdropfs.cpp:1898-1900）。
        device.SetTextureStageState(0, TextureStage.ColorOperation, (int)TextureOperation.Modulate);
        device.SetTextureStageState(0, TextureStage.ColorArg1, (int)TextureArgument.Texture);
        device.SetTextureStageState(0, TextureStage.ColorArg2, (int)TextureArgument.Diffuse);
        device.SetTextureStageState(0, TextureStage.AlphaOperation, (int)TextureOperation.SelectArg1);
        device.SetTextureStageState(0, TextureStage.AlphaArg1, (int)TextureArgument.Texture);

        device.VertexFormat = VertexFormat.Position | VertexFormat.Diffuse | VertexFormat.Texture1 | VertexFormat.Texture2 | VertexFormat.Texture3;
    }

    private void ApplyBlend(BlendMode blend)
    {
        var device = _context.Device;
        switch (blend)
        {
            case BlendMode.Replace:
                device.SetRenderState(RenderState.AlphaBlendEnable, false);
                break;

            case BlendMode.Additive:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
                device.SetRenderState(RenderState.DestinationBlend, Blend.One);
                break;

            case BlendMode.SourceColor:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.SourceColor);
                device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceColor);
                break;

            // 固定管线合成的三个"按目标像素做运算"的滤镜（milkdropfs.cpp:4213-4267）：
            // 顶点色恒为白、不绑纹理，所以结果只取决于目标缓冲的当前像素。
            case BlendMode.MultiplyDest:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.Zero);
                device.SetRenderState(RenderState.DestinationBlend, Blend.DestinationColor);
                break;

            case BlendMode.InvertDest:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.InverseDestinationColor);
                device.SetRenderState(RenderState.DestinationBlend, Blend.Zero);
                break;

            case BlendMode.SolarizeInvert:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.Zero);
                device.SetRenderState(RenderState.DestinationBlend, Blend.InverseDestinationColor);
                break;

            case BlendMode.SolarizeAdd:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.DestinationColor);
                device.SetRenderState(RenderState.DestinationBlend, Blend.One);
                break;

            default:
                device.SetRenderState(RenderState.AlphaBlendEnable, true);
                device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
                device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
                break;
        }
    }

    private void BindTexture(ITexture? texture)
        => _context.Device.SetTexture(0, texture is D3D9Texture d3d9 ? d3d9.Texture : null);

    /// <summary>把像素坐标顶点转成裁剪空间后绘制。</summary>
    private void DrawPixelSpacePrimitive(PrimitiveType primitiveType, ReadOnlySpan<MilkVertex> source, uint primitiveCount)
    {
        // 注：M4 之后这里会换成预分配的持久缓冲区（避免每帧分配）。
        var w = Width;
        var h = Height;
        var scratch = new MilkVertex[source.Length];

        for (var i = 0; i < source.Length; i++)
        {
            scratch[i] = ToClip(source[i], w, h);
        }

        DrawPrimitiveUp(primitiveType, scratch, primitiveCount);
    }

    /// <summary>像素空间（左上原点、Y 向下）→ 裁剪空间（D3D9 的 Y 向上）。</summary>
    private static MilkVertex ToClip(in MilkVertex v, int width, int height)
        => new(
            new Vector3((v.Position.X / width * 2f) - 1f, 1f - (v.Position.Y / height * 2f), v.Position.Z),
            v.Color,
            v.TexCoord,
            v.UvOrig,
            v.RadAng);

    private unsafe void DrawPrimitiveUp(PrimitiveType primitiveType, ReadOnlySpan<MilkVertex> vertices, uint primitiveCount)
    {
        fixed (MilkVertex* p = vertices)
        {
            _context.Device.DrawPrimitiveUP(primitiveType, primitiveCount, (nint)p, (uint)sizeof(MilkVertex));
        }
    }
}
