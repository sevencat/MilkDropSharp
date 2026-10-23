namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// Milkdrop 预设工厂：支持 <c>.milk</c>/<c>.prjm</c>，以及 <c>idle://</c> 伪协议。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/Factory.hpp</c> 与 <c>Factory.cpp</c>。</remarks>
public sealed class MilkdropPresetFactory : PresetFactory
{
    /// <inheritdoc />
    public override string SupportedExtensions => ".milk .prjm";

    /// <inheritdoc />
    public override Preset? LoadPresetFromFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        string protocol = Protocol(filename, out string path);

        if (protocol == "idle")
        {
            return IdlePresets.Allocate();
        }

        if (protocol.Length == 0 || protocol == "file")
        {
            return new MilkdropPreset(path);
        }

        // 与 C++ 一样：不支持的协议返回 null（由 Engine 当作"没有预设"处理）。
        return null;
    }

    /// <inheritdoc />
    public override Preset LoadPresetFromStream(Stream data) => new MilkdropPreset(data);
}
