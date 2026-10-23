using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Vortice.Direct3D9;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// ImGui 的 D3D9 渲染后端（ImGui.NET 不带 DX9 后端，见 <c>cbplan.md</c> §2.5）。
/// 用固定功能管线 + 顶点色 × 纹理 绘制，坐标在 CPU 侧转到裁剪空间。
/// </summary>
public sealed class ImguiDx9Renderer : IDisposable
{
    private const int InitialVertexCapacity = 5000;
    private const int InitialIndexCapacity = 10000;

    private readonly IDirect3DDevice9 _device;

    private IDirect3DVertexBuffer9? _vertexBuffer;
    private IDirect3DIndexBuffer9? _indexBuffer;
    private IDirect3DTexture9? _fontTexture;
    private int _vertexCapacity;
    private int _indexCapacity;


    public ImguiDx9Renderer(IDirect3DDevice9 device) => _device = device;

    [StructLayout(LayoutKind.Sequential)]
    private struct UiVertex
    {
        public Vector3 Position;
        public uint Color;
        public Vector2 TexCoord;
    }

    /// <summary>创建 VB/IB 与字体纹理（设备创建后、设备重置后都要调用一次）。</summary>
    public void CreateResources()
    {
        _vertexCapacity = InitialVertexCapacity;
        _indexCapacity = InitialIndexCapacity;

        _vertexBuffer = _device.CreateVertexBuffer(
            (uint)(_vertexCapacity * Marshal.SizeOf<UiVertex>()),
            Usage.Dynamic | Usage.WriteOnly,
            VertexFormat.Position | VertexFormat.Diffuse | VertexFormat.Texture1,
            Pool.Default);

        _indexBuffer = _device.CreateIndexBuffer(
            (uint)(_indexCapacity * sizeof(ushort)),
            Usage.Dynamic | Usage.WriteOnly,
            true,
            Pool.Default);

        CreateFontTexture();
    }

    /// <summary>释放 <c>Pool.Default</c> 资源（设备 Reset 前调用）。</summary>
    public void InvalidateResources()
    {
        _fontTexture?.Dispose();
        _fontTexture = null;
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _indexBuffer?.Dispose();
        _indexBuffer = null;
    }

    public void Render()
    {
        if (_vertexBuffer is null || _indexBuffer is null)
        {
            return;
        }

        var drawData = ImGui.GetDrawData();
        if (!drawData.Valid || drawData.CmdListsCount == 0)
        {
            return;
        }

        if (drawData.TotalVtxCount > _vertexCapacity || drawData.TotalIdxCount > _indexCapacity)
        {
            GrowBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
        }

        SetupRenderState(drawData);

        var displayPos = drawData.DisplayPos;
        var displaySize = drawData.DisplaySize;

        for (var listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var drawList = drawData.CmdLists[listIndex];
            var vertexCount = drawList.VtxBuffer.Size;
            var indexCount = drawList.IdxBuffer.Size;

            UploadVertices(drawList, vertexCount, displayPos, displaySize);
            UploadIndices(drawList, indexCount);

            for (var cmdIndex = 0; cmdIndex < drawList.CmdBuffer.Size; cmdIndex++)
            {
                var cmd = drawList.CmdBuffer[cmdIndex];
                if (cmd.ElemCount == 0)
                {
                    continue;
                }

                var clip = cmd.ClipRect;
                var left = (int)MathF.Max(0f, clip.X);
                var top = (int)MathF.Max(0f, clip.Y);
                var right = (int)MathF.Min(displaySize.X, clip.Z);
                var bottom = (int)MathF.Min(displaySize.Y, clip.W);
                if (right <= left || bottom <= top)
                {
                    continue;
                }

                _device.ScissorRect = new Rect(left, top, right, bottom);
                BindTexture(cmd.TextureId);
                _device.DrawIndexedPrimitive(
                    PrimitiveType.TriangleList,
                    0,
                    0u,
                    (uint)vertexCount,
                    cmd.IdxOffset,
                    cmd.ElemCount / 3);
            }
        }

        _device.SetTexture(0, null);
        _device.SetRenderState(RenderState.ScissorTestEnable, false);
    }


    public void Dispose() => InvalidateResources();

    private void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);

        // ImGui 给的是 RGBA32；D3D9 的 A8R8G8B8 内存序是 BGRA，所以这里换 R/B。
        var bg = new byte[width * height * 4];
        unsafe
        {
            var src = (byte*)pixels;
            fixed (byte* dst = bg)
            {
                for (var i = 0; i < width * height; i++)
                {
                    dst[i * 4 + 0] = src[i * 4 + 2];
                    dst[i * 4 + 1] = src[i * 4 + 1];
                    dst[i * 4 + 2] = src[i * 4 + 0];
                    dst[i * 4 + 3] = src[i * 4 + 3];
                }
            }
        }

        _fontTexture = _device.CreateTexture((uint)width, (uint)height, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
        var rect = _fontTexture.LockRect(0, LockFlags.Discard);
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(bg, y * width * 4, rect.DataPointer + (y * rect.Pitch), width * 4);
        }

        _fontTexture.UnlockRect(0);
        io.Fonts.SetTexID(_fontTexture.NativePointer);
    }

    private void GrowBuffers(int vertexCount, int indexCount)
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();

        _vertexCapacity = Math.Max(InitialVertexCapacity, vertexCount + 5000);
        _indexCapacity = Math.Max(InitialIndexCapacity, indexCount + 10000);

        _vertexBuffer = _device.CreateVertexBuffer(
            (uint)(_vertexCapacity * Marshal.SizeOf<UiVertex>()),
            Usage.Dynamic | Usage.WriteOnly,
            VertexFormat.Position | VertexFormat.Diffuse | VertexFormat.Texture1,
            Pool.Default);

        _indexBuffer = _device.CreateIndexBuffer(
            (uint)(_indexCapacity * sizeof(ushort)),
            Usage.Dynamic | Usage.WriteOnly,
            true,
            Pool.Default);
    }

    private void SetupRenderState(ImDrawDataPtr drawData)
    {
        // 可视化器上一帧可能还绑着 warp/composite 的 pixel shader，
        // 不清掉的话 ImGui 会被那个 shader 着色（表现为面板泛红、文字发花）。
        // ImGui 走的是固定功能管线。
        _device.VertexShader = null;
        _device.PixelShader = null;

        _device.SetRenderState(RenderState.ZEnable, false);
        _device.SetRenderState(RenderState.ZWriteEnable, false);
        _device.SetRenderState(RenderState.CullMode, Cull.None);
        _device.SetRenderState(RenderState.Lighting, false);
        _device.SetRenderState(RenderState.AlphaBlendEnable, true);
        _device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
        _device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
        _device.SetRenderState(RenderState.ScissorTestEnable, true);
        _device.SetRenderState(RenderState.PointSize, 1f);
        _device.SetRenderState(RenderState.PointScaleEnable, false);

        _device.SetSamplerState(0, SamplerState.MinFilter, (int)TextureFilter.Linear);
        _device.SetSamplerState(0, SamplerState.MagFilter, (int)TextureFilter.Linear);
        _device.SetSamplerState(0, SamplerState.MipFilter, (int)TextureFilter.None);
        _device.SetSamplerState(0, SamplerState.AddressU, (int)TextureAddress.Clamp);
        _device.SetSamplerState(0, SamplerState.AddressV, (int)TextureAddress.Clamp);

        _device.SetTextureStageState(0, TextureStage.ColorOperation, (int)TextureOperation.Modulate);
        _device.SetTextureStageState(0, TextureStage.ColorArg1, (int)TextureArgument.Texture);
        _device.SetTextureStageState(0, TextureStage.ColorArg2, (int)TextureArgument.Diffuse);
        _device.SetTextureStageState(0, TextureStage.AlphaOperation, (int)TextureOperation.Modulate);
        _device.SetTextureStageState(0, TextureStage.AlphaArg1, (int)TextureArgument.Texture);
        _device.SetTextureStageState(0, TextureStage.AlphaArg2, (int)TextureArgument.Diffuse);

        _device.VertexFormat = VertexFormat.Position | VertexFormat.Diffuse | VertexFormat.Texture1;

        _device.Viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = (int)drawData.DisplaySize.X,
            Height = (int)drawData.DisplaySize.Y,
            MinZ = 0f,
            MaxZ = 1f,
        };

        _device.Indices = _indexBuffer;
        _device.SetStreamSource(0, _vertexBuffer, 0, (uint)Marshal.SizeOf<UiVertex>());
    }

    private unsafe void UploadVertices(ImDrawListPtr drawList, int vertexCount, Vector2 displayPos, Vector2 displaySize)
    {
        var vertices = drawList.VtxBuffer;
        var span = _vertexBuffer!.Lock<UiVertex>(0, 0, LockFlags.Discard);

        for (var i = 0; i < vertexCount; i++)
        {
            var v = vertices[i];
            var x = (v.pos.X - displayPos.X - 0.5f) / displaySize.X * 2f - 1f;
            var y = 1f - (v.pos.Y - displayPos.Y - 0.5f) / displaySize.Y * 2f;

            span[i] = new UiVertex
            {
                Position = new Vector3(x, y, 0f),
                Color = ToD3DColor(v.col),
                TexCoord = v.uv,
            };
        }

        _vertexBuffer.Unlock();
    }

    private void UploadIndices(ImDrawListPtr drawList, int indexCount)
    {
        var indices = drawList.IdxBuffer;
        var span = _indexBuffer!.Lock<ushort>(0, 0, LockFlags.Discard);
        for (var i = 0; i < indexCount; i++)
        {
            span[i] = indices[i];
        }

        _indexBuffer.Unlock();
    }

    private void BindTexture(nint textureId)
    {
        if (textureId == 0 && _fontTexture is not null)
        {
            _device.SetTexture(0, _fontTexture);
            return;
        }

        _device.SetTexture(0, textureId == 0 ? null : new IDirect3DBaseTexture9(textureId));
    }

    /// <summary>
    /// ImGui 的颜色是 <c>IM_COL32(R,G,B,A)</c>，在小端机上按 uint 读出来是 <b>0xAABBGGRR</b>；
    /// D3DCOLOR 是 <b>0xAARRGGBB</b>。所以要真正交换 R 与 B（A 和 G 位置本来就对）。
    /// </summary>
    /// <remarks>
    /// 之前写成 <c>(c &amp; 0xFF) &lt;&lt; 24</c> 会把 R 当 alpha、B 当红色，
    /// 表现为"面板半透明且发蓝、红字变蓝字"。
    /// </remarks>
    private static uint ToD3DColor(uint imguiColor)
    {
        var r = imguiColor & 0x000000FF;
        var g = imguiColor & 0x0000FF00;
        var b = imguiColor & 0x00FF0000;
        var a = imguiColor & 0xFF000000;

        return a | (r << 16) | g | (b >> 16);
    }
}
