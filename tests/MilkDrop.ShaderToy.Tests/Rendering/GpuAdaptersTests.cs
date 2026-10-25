using MilkDrop.Rendering.Gles3;

using Xunit;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// 显卡选择器（<c>--gpu</c> / <c>shadertoy.json</c> 的 <c>gpu</c>）的纯逻辑部分：
/// 只测"从候选表里挑一块"，不碰 DXGI/EGL。
/// </summary>
public sealed class GpuAdaptersTests
{
    private static GpuAdapter Amd() => new(
        Index: 0,
        Description: "AMD Radeon(TM) 610M",
        VendorId: 0x1002,
        DeviceId: 0x164E,
        DedicatedVideoMemory: 512L * 1024 * 1024,
        LuidLow: 0xFE57,
        LuidHigh: 0,
        IsSoftware: false);

    private static GpuAdapter Nvidia() => new(
        Index: 1,
        Description: "NVIDIA GeForce RTX 5060 Laptop GPU",
        VendorId: 0x10DE,
        DeviceId: 0x2D19,
        DedicatedVideoMemory: 8L * 1024 * 1024 * 1024,
        LuidLow: 0xFB1D,
        LuidHigh: 0,
        IsSoftware: false);

    private static GpuAdapter Warp() => new(
        Index: 3,
        Description: "Microsoft Basic Render Driver",
        VendorId: 0x1414,
        DeviceId: 0x008C,
        DedicatedVideoMemory: 0,
        LuidLow: 0x0001,
        LuidHigh: 0,
        IsSoftware: true);

    private static IReadOnlyList<GpuAdapter> All() => [Amd(), Nvidia(), Warp()];

    [Fact]
    public void Match_ByIdIndex()
    {
        GpuAdapter? adapter = GpuAdapters.Match(All(), "1", out string? error);

        Assert.Null(error);
        Assert.Equal("NVIDIA GeForce RTX 5060 Laptop GPU", adapter?.Description);
    }

    [Fact]
    public void Match_ByIndexOutOfRange_ReportsCandidates()
    {
        GpuAdapter? adapter = GpuAdapters.Match(All(), "9", out string? error);

        Assert.Null(adapter);
        Assert.Contains("9", error);
    }

    [Fact]
    public void Match_ByNameFragment_IsCaseInsensitive()
    {
        Assert.Equal("NVIDIA GeForce RTX 5060 Laptop GPU", GpuAdapters.Match(All(), "nvidia", out _)?.Description);
        Assert.Equal("AMD Radeon(TM) 610M", GpuAdapters.Match(All(), "RADEON", out _)?.Description);
    }

    [Fact]
    public void Match_MultipleNameHits_PrefersHardwareWithMoreVram()
    {
        var adapters = new List<GpuAdapter>
        {
            new(0, "Intel(R) UHD Graphics", 0x8086, 0x9A60, 128L * 1024 * 1024, 0x10, 0, false),
            new(1, "Intel(R) Arc(TM) A770", 0x8086, 0x56A0, 8L * 1024 * 1024 * 1024, 0x11, 0, false),
            new(2, "Intel(R) Basic Render", 0x1414, 0x008C, 0, 0x12, 0, true),
        };

        Assert.Equal("Intel(R) Arc(TM) A770", GpuAdapters.Match(adapters, "intel", out _)?.Description);
    }

    [Fact]
    public void Match_ByNameNotFound_Fails()
    {
        Assert.Null(GpuAdapters.Match(All(), "matrox", out string? error));
        Assert.Contains("matrox", error);
    }

    [Fact]
    public void Match_ByLuid()
    {
        GpuAdapter? adapter = GpuAdapters.Match(All(), "luid:fb1d", out string? error);

        Assert.Null(error);
        Assert.Equal("NVIDIA GeForce RTX 5060 Laptop GPU", adapter?.Description);
    }

    [Fact]
    public void Match_ByLuidWithPrefixAndBadFormat()
    {
        Assert.Equal("AMD Radeon(TM) 610M", GpuAdapters.Match(All(), "LUID:0xFE57", out _)?.Description);

        Assert.Null(GpuAdapters.Match(All(), "luid:zzz", out string? error));
        Assert.Contains("LUID", error);
    }

    [Fact]
    public void Match_EmptySelectorOrNoAdapters_Fails()
    {
        Assert.Null(GpuAdapters.Match(All(), "  ", out string? emptyError));
        Assert.NotNull(emptyError);

        Assert.Null(GpuAdapters.Match([], "0", out string? noAdapterError));
        Assert.NotNull(noAdapterError);
    }

    [Fact]
    public void Describe_ListsEveryAdapterWithIndex()
    {
        string text = GpuAdapters.Describe(All());

        Assert.Contains("[0] AMD Radeon(TM) 610M", text);
        Assert.Contains("[1] NVIDIA GeForce RTX 5060 Laptop GPU", text);
        Assert.Contains("[软件]", text);
        Assert.Equal("（没有枚举到 D3D 适配器）", GpuAdapters.Describe([]));
    }

    [Fact]
    public void Adapter_FormatsMemoryAndLuid()
    {
        GpuAdapter nvidia = Nvidia();

        Assert.Equal("8 GB", nvidia.MemoryText);
        Assert.Equal(0xFB1DL, nvidia.Luid);
        Assert.Contains("8 GB", nvidia.ToString());
        Assert.Contains("10DE:2D19", nvidia.ToString());
    }
}
