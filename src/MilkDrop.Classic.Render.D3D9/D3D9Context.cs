using Vortice.Direct3D9;
using Vortice.Mathematics;
using D3DFactory = Vortice.Direct3D9.D3D9;
using D9Viewport = Vortice.Direct3D9.Viewport;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// D3D9 设备生命周期。创建设备参数对齐原版 <c>Milkdrop2PcmVisualizer.cpp:66-100</c>
/// （窗口化、A8R8G8B8、D3DSWAPEFFECT_COPY、D24X8 深度缓冲）。
/// </summary>
public sealed class D3D9Context : IDisposable
{
    private readonly IDirect3D9 _d3d;
    private readonly nint _hwnd;
    private PresentParameters _presentParameters;
    private IDirect3DSurface9? _backBufferSurface;
    private IDirect3DSurface9? _depthStencilSurface;
    private bool _inScene;

    private D3D9Context(IDirect3D9 d3d, IDirect3DDevice9 device, nint hwnd, PresentParameters presentParameters, int width, int height)
    {
        _d3d = d3d;
        _hwnd = hwnd;
        _presentParameters = presentParameters;
        Device = device;
        Width = width;
        Height = height;
        _backBufferSurface = device.GetBackBuffer(0, 0, BackBufferType.Mono);
        _depthStencilSurface = device.DepthStencilSurface;
    }

    public IDirect3DDevice9 Device { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool VSync { get; set; } = true;

    /// <summary>后备缓冲表面（渲染目标切回它用）。</summary>
    public IDirect3DSurface9 BackBufferSurface
        => _backBufferSurface ?? throw new ObjectDisposedException(nameof(D3D9Context));

    public static D3D9Context Create(nint hwnd, int width, int height, bool vsync = true, bool hardware = true)
    {
        var d3d = D3DFactory.Direct3DCreate9();

        var presentParameters = new PresentParameters
        {
            BackBufferWidth = (uint)Math.Max(1, width),
            BackBufferHeight = (uint)Math.Max(1, height),
            BackBufferFormat = Format.A8R8G8B8,
            BackBufferCount = 1,
            MultiSampleType = MultisampleType.None,
            MultiSampleQuality = 0,
            SwapEffect = SwapEffect.Copy,
            DeviceWindowHandle = hwnd,
            Windowed = true,
            EnableAutoDepthStencil = true,
            AutoDepthStencilFormat = Format.D24X8,
            PresentationInterval = vsync ? PresentInterval.One : PresentInterval.Immediate,
        };

        var deviceType = hardware ? DeviceType.Hardware : DeviceType.Reference;
        var createFlags = hardware ? CreateFlags.HardwareVertexProcessing : CreateFlags.SoftwareVertexProcessing;

        var device = d3d.CreateDevice(0, deviceType, hwnd, createFlags, presentParameters);

        return new D3D9Context(d3d, device, hwnd, presentParameters, width, height);
    }

    public void BeginScene()
    {
        if (_inScene)
        {
            return;
        }

        Device.BeginScene();
        _inScene = true;
    }

    public void EndScene()
    {
        if (!_inScene)
        {
            return;
        }

        Device.EndScene();
        _inScene = false;
    }

    public void Clear(uint argb)
        => Device.Clear(ClearFlags.Target | ClearFlags.ZBuffer, new Color(argb), 1.0f, 0);

    public bool Present()
    {
        var result = Device.Present();
        return result.Success;
    }

    /// <summary>窗口尺寸变化后重建后台缓冲（调用方需先释放所有 <c>Pool.Default</c> 资源）。</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        _presentParameters.BackBufferWidth = (uint)width;
        _presentParameters.BackBufferHeight = (uint)height;

        EndScene();

        // 后备缓冲表面在 Reset 后失效，必须先释放再重新获取。
        _backBufferSurface?.Dispose();
        _backBufferSurface = null;
        _depthStencilSurface?.Dispose();
        _depthStencilSurface = null;

        Device.Reset(ref _presentParameters);

        Width = width;
        Height = height;
        _backBufferSurface = Device.GetBackBuffer(0, 0, BackBufferType.Mono);
        _depthStencilSurface = Device.DepthStencilSurface;
    }

    public void SetViewport()
        => Device.Viewport = new D9Viewport { X = 0, Y = 0, Width = Width, Height = Height, MinZ = 0f, MaxZ = 1f };

    public void SetViewport(int width, int height)
        => Device.Viewport = new D9Viewport { X = 0, Y = 0, Width = Math.Max(1, width), Height = Math.Max(1, height), MinZ = 0f, MaxZ = 1f };

    /// <summary>把渲染目标切到指定表面（null = 后备缓冲），并同步 viewport。</summary>
    public void SetRenderTarget(IDirect3DSurface9? surface, int width, int height)
    {
        Device.SetRenderTarget(0, surface ?? BackBufferSurface);

        // 渲染到离屏表面时不需要深度缓冲（MilkDrop 全程 ZEnable = false）。
        Device.DepthStencilSurface = surface is null ? _depthStencilSurface : null;
        SetViewport(width, height);
    }

    /// <summary>创建离屏渲染目标纹理（<c>Usage.RenderTarget</c> + <c>Pool.Default</c>，Reset 前必须释放）。</summary>
    public IDirect3DTexture9 CreateRenderTargetTexture(int width, int height, Format format = Format.A8R8G8B8)
        => Device.CreateTexture((uint)Math.Max(1, width), (uint)Math.Max(1, height), 1, Usage.RenderTarget, format, Pool.Default);

    /// <summary>创建一张 A8R8G8B8 纹理并上传 BGRA 像素（ImGui 字体、sprite 用）。</summary>
    public IDirect3DTexture9 CreateTextureFromBgra(int width, int height, ReadOnlySpan<byte> bgraPixels, Pool pool = Pool.Managed)
    {
        var texture = Device.CreateTexture((uint)width, (uint)height, 1, Usage.None, Format.A8R8G8B8, pool);
        UpdateTextureFromBgra(texture, width, height, bgraPixels);
        return texture;
    }

    /// <summary>
    /// 创建一张 <c>size</c>³ 的 volume 纹理并上传 BGRA 体素（对应 <c>AddNoiseVol</c> 的
    /// <c>CreateVolumeTexture</c> + <c>LockBox</c>）。
    /// </summary>
    /// <remarks>
    /// 必须用 <c>D3DUSAGE_DYNAMIC</c> + <c>D3DPOOL_DEFAULT</c>（原版就是这么建的）：
    /// volume 纹理在这个驱动上用 <c>Pool.Managed</c> 会直接 <c>D3DERR_INVALIDCALL</c>。
    /// 代价是设备 Reset 后要重建 —— <c>Visualizer.OnDeviceLost</c> 会把它一起释放。
    /// </remarks>
    public unsafe IDirect3DVolumeTexture9 CreateVolumeTextureFromBgra(int size, ReadOnlySpan<byte> bgraPixels)
    {
        var texture = Device.CreateVolumeTexture(
            (uint)size,
            (uint)size,
            (uint)size,
            1,
            Usage.Dynamic,
            Format.A8R8G8B8,
            Pool.Default);

        var box = texture.LockBox(0, LockFlags.Discard);
        var sliceBytes = size * size * 4;

        for (var z = 0; z < size; z++)
        {
            var slicePointer = box.DataPointer + (z * box.SlicePitch);

            for (var y = 0; y < size; y++)
            {
                var dstRow = new Span<byte>((void*)(slicePointer + (y * box.RowPitch)), size * 4);
                bgraPixels.Slice((z * sliceBytes) + (y * size * 4), size * 4).CopyTo(dstRow);
            }
        }

        texture.UnlockBox(0);
        return texture;
    }

    /// <summary>整张覆盖上传 BGRA 像素（每帧变化的动态纹理，如频谱纹理）。</summary>
    public unsafe void UpdateTextureFromBgra(IDirect3DTexture9 texture, int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        var rect = texture.LockRect(0, LockFlags.Discard);
        var src = bgraPixels;
        for (var y = 0; y < height; y++)
        {
            var dstRow = new Span<byte>((void*)(rect.DataPointer + y * rect.Pitch), width * 4);
            src.Slice(y * width * 4, width * 4).CopyTo(dstRow);
        }

        texture.UnlockRect(0);
    }

    public void Dispose()
    {
        EndScene();
        Device.Dispose();
        _d3d.Dispose();
        _ = _hwnd;
    }
}
