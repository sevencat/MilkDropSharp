using System.Globalization;
using System.Runtime.InteropServices;

namespace MilkDrop.Rendering.Gles3;

/// <summary>
/// 一块 D3D 适配器（也就是"一块显卡"）：ANGLE 走 D3D11 后端时按 LUID 选卡。
/// </summary>
/// <param name="Index">DXGI 枚举顺序（0 = 系统默认那块，通常是驱动着主显示器的那块）。</param>
/// <param name="Description">名称，如 <c>NVIDIA GeForce RTX 4060 Laptop GPU</c>。</param>
/// <param name="VendorId">PCI 厂商 ID（0x10DE = NVIDIA，0x1002 = AMD，0x8086 = Intel）。</param>
/// <param name="DeviceId">PCI 设备 ID。</param>
/// <param name="DedicatedVideoMemory">专用显存（字节；核显一般是 0 或很小）。</param>
/// <param name="LuidLow">适配器 LUID 低 32 位。</param>
/// <param name="LuidHigh">适配器 LUID 高 32 位。</param>
/// <param name="IsSoftware">是不是软件适配器（WARP / 基本渲染驱动）。</param>
public sealed record GpuAdapter(
    int Index,
    string Description,
    uint VendorId,
    uint DeviceId,
    long DedicatedVideoMemory,
    uint LuidLow,
    int LuidHigh,
    bool IsSoftware)
{
    /// <summary>完整 LUID（高 32 位在前），日志/比较用。</summary>
    public long Luid => ((long)LuidHigh << 32) | LuidLow;

    /// <summary>显存显示文本（MB / GB）。</summary>
    public string MemoryText => DedicatedVideoMemory >= 1024L * 1024 * 1024
        ? $"{DedicatedVideoMemory / (1024.0 * 1024 * 1024):0.#} GB"
        : $"{DedicatedVideoMemory / (1024 * 1024)} MB";

    /// <summary>一行描述：<c>[0] NVIDIA GeForce ... (10DE:2882, 8 GB)</c>。</summary>
    public override string ToString() =>
        $"[{Index}] {Description}  ({VendorId:X4}:{DeviceId:X4}, {MemoryText}){(IsSoftware ? "  [软件]" : string.Empty)}";
}

/// <summary>
/// 用 DXGI 枚举本机 D3D 适配器，并按"索引 / 名字片段"选中一块，供 ANGLE 建 display 时指定。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要枚举：ANGLE 的 D3D11 后端接受 <c>EGL_PLATFORM_ANGLE_DEVICE_ID_HIGH/LOW_ANGLE</c>
/// 两个属性（值就是适配器 LUID 的高/低 32 位），而 LUID 只能从 DXGI 拿。
/// </para>
/// <para>
/// 这里不引 NuGet（Silk.NET.DXGI 会多带一堆绑定），直接用 <c>dxgi.dll</c> +
/// 手写 vtable 调用：只用到 <c>IDXGIFactory1::EnumAdapters1</c> 与 <c>IDXGIAdapter1::GetDesc1</c>
/// 两个方法，比引一整套 COM 互操作省事，AOT 下也不用开内建 COM 支持。
/// </para>
/// <para>
/// 没指定就用系统默认（等价于现在的行为）：D3D11 传空适配器，由 Windows 决定 —— 会跟随
/// "设置 → 系统 → 显示 → 图形"里给这个 exe 配的"高性能/省电"偏好。
/// </para>
/// </remarks>
public static class GpuAdapters
{
    private const string DxgiLibrary = "dxgi.dll";

    /// <summary>IID_IDXGIFactory1。</summary>
    private static readonly Guid IidIdxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    // vtable 槽位（IUnknown 3 个 + 各接口自己的方法）
    private const int ReleaseSlot = 2;
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;

    /// <summary>
    /// 枚举所有适配器（DXGI 顺序，0 在最前）。枚举失败（非 Windows / 没有 dxgi.dll）返回空表。
    /// </summary>
    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var adapters = new List<GpuAdapter>();

        if (!OperatingSystem.IsWindows())
        {
            return adapters;
        }

        IntPtr factory = IntPtr.Zero;

        try
        {
            if (CreateDXGIFactory1(in IidIdxgiFactory1, out factory) != 0 || factory == IntPtr.Zero)
            {
                return adapters;
            }

            var enumAdapters1 = GetMethod<EnumAdapters1Proc>(factory, EnumAdapters1Slot);

            for (var index = 0; index < 16; index++)
            {
                if (enumAdapters1(factory, (uint)index, out IntPtr adapter) != 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var getDesc1 = GetMethod<GetDesc1Proc>(adapter, GetDesc1Slot);

                    if (getDesc1(adapter, out AdapterDescription1 description) != 0)
                    {
                        continue;
                    }

                    adapters.Add(new GpuAdapter(
                        index,
                        description.Description ?? $"适配器 {index}",
                        description.VendorId,
                        description.DeviceId,
                        (long)description.DedicatedVideoMemory,
                        description.LuidLowPart,
                        description.LuidHighPart,
                        (description.Flags & AdapterFlagSoftware) != 0));
                }
                finally
                {
                    GetMethod<ReleaseProc>(adapter, ReleaseSlot)(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // 没有 dxgi.dll 就算了：返回空表，调用方按"默认显卡"继续
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                GetMethod<ReleaseProc>(factory, ReleaseSlot)(factory);
            }
        }

        return adapters;
    }

    /// <summary>
    /// 按选择器挑一块卡。
    /// </summary>
    /// <remarks>
    /// 选择器支持三种写法（大小写不敏感）：
    /// <list type="bullet">
    /// <item><c>0</c> / <c>1</c>：DXGI 索引（<c>--gpu list</c> 里的编号）；</item>
    /// <item><c>nvidia</c> / <c>radeon</c> / <c>intel</c>：按名称片段匹配，命中多块时取专用显存最大的；</item>
    /// <item><c>luid:1a2b3c4d</c>：直接给 LUID（低 32 位十六进制）。</item>
    /// </list>
    /// </remarks>
    /// <returns>没匹配到返回 null，原因写进 <paramref name="error"/>，并把候选列表带上。</returns>
    public static GpuAdapter? Match(IReadOnlyList<GpuAdapter> adapters, string selector, out string? error)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        error = null;
        string text = selector.Trim();

        if (text.Length == 0)
        {
            error = "GPU 选择器为空。";
            return null;
        }

        if (adapters.Count == 0)
        {
            error = "没有枚举到 D3D 适配器（非 Windows 或 dxgi 不可用）。";
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            GpuAdapter? byIndex = adapters.FirstOrDefault(adapter => adapter.Index == index);

            if (byIndex is null)
            {
                error = $"没有编号为 {index} 的适配器。";
                return null;
            }

            return byIndex;
        }

        if (text.StartsWith("luid:", StringComparison.OrdinalIgnoreCase))
        {
            string hex = text["luid:".Length..].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);

            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint luid))
            {
                error = $"LUID 写法不对：{selector}（示例 luid:1a2b3c4d）。";
                return null;
            }

            GpuAdapter? byLuid = adapters.FirstOrDefault(adapter => adapter.LuidLow == luid);

            if (byLuid is null)
            {
                error = $"没有 LUID 低 32 位为 {luid:X} 的适配器。";
                return null;
            }

            return byLuid;
        }

        GpuAdapter[] byName = adapters
            .Where(adapter => adapter.Description.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (byName.Length == 1)
        {
            return byName[0];
        }

        if (byName.Length > 1)
        {
            // 命中多块（比如两台机器都有 "Intel"）：优先非软件、专用显存大的那块
            return byName
                .OrderBy(adapter => adapter.IsSoftware)
                .ThenByDescending(adapter => adapter.DedicatedVideoMemory)
                .First();
        }

        error = $"没有名称里含 \"{text}\" 的适配器。";
        return null;
    }

    /// <summary>把枚举结果拼成多行文本（<c>--gpu list</c> 与错误提示共用）。</summary>
    public static string Describe(IReadOnlyList<GpuAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (adapters.Count == 0)
        {
            return "（没有枚举到 D3D 适配器）";
        }

        return string.Join(Environment.NewLine, adapters.Select(adapter => "  " + adapter));
    }

    // ---- DXGI 互操作（只读枚举，不建 D3D 设备）----

    private const uint AdapterFlagSoftware = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Proc(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Proc(IntPtr self, out AdapterDescription1 description);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReleaseProc(IntPtr self);

    [DllImport(DxgiLibrary, ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    /// <summary>取 COM 对象的第 <paramref name="slot"/> 个 vtable 方法并转成委托。</summary>
    private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr entry = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }
}
