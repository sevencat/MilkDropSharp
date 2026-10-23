namespace MilkDrop.Classic.Render.D3D9;

/// <summary>一个预设 shader 采样器的名字与采样方式（阶段号由装配时按使用情况紧凑分配）。</summary>
/// <param name="Name">HLSL 里的 sampler 名字（含 <c>sampler_</c> 前缀）。</param>
/// <param name="Wrap">是否 wrap（否则 clamp）。</param>
/// <param name="Bilinear">是否双线性过滤（false = point）。</param>
public readonly record struct ShaderSampler(string Name, bool Wrap, bool Bilinear);

/// <summary>
/// 预设 shader 的采样器元数据。
/// </summary>
/// <remarks>
/// 原版通过 <c>ID3DXConstantTable</c> 查每个 sampler 的寄存器号，再按名字决定绑定哪张纹理。
/// 这里没有反射，所以由 <see cref="ShaderPrelude"/> 在生成源码时给 include.fx 里
/// <b>实际用到</b>的 sampler 补上紧凑的 <c>: register(sN)</c>，编译后再由
/// <see cref="ShaderConstantTable"/> 从源码解析回名字 → 阶段号。
/// 只声明用到的 sampler 很重要：ps_2_0 的采样器寄存器只有 s0..s15，
/// 全量声明 14 个会把预设自己声明的纹理 sampler 挤爆。
/// </remarks>
public static class ShaderSamplers
{
    /// <summary>全部已知采样器（顺序决定紧凑分配时的先后）。</summary>
    public static readonly ShaderSampler[] All =
    [
        new("sampler_main", true, true),
        new("sampler_fc_main", false, true),
        new("sampler_pc_main", false, false),
        new("sampler_fw_main", true, true),
        new("sampler_pw_main", true, false),
        new("sampler_noise_lq", true, true),
        new("sampler_noise_lq_lite", true, true),
        new("sampler_noise_mq", true, true),
        new("sampler_noise_hq", true, true),
        new("sampler_noisevol_lq", true, true),
        new("sampler_noisevol_hq", true, true),
        new("sampler_blur1", false, true),
        new("sampler_blur2", false, true),
        new("sampler_blur3", false, true),

        // 兼容层的频谱纹理（512×1），给 get_fft / get_fft_hz 用。
        // 排在最后，保证不改变已有采样器的紧凑编号。
        new("sampler_fft", false, true),
    ];

    public static bool TryGet(string name, out ShaderSampler sampler)
    {
        foreach (var item in All)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                sampler = item;
                return true;
            }
        }

        sampler = default;
        return false;
    }

    /// <summary>
    /// 预设自己声明的纹理 sampler（如 <c>sampler_fw_clouds</c>）→ <c>textures/</c> 里的文件根名 + 采样方式。
    /// </summary>
    /// <param name="RootName">去掉 <c>sampler_</c> 与 <c>XY_</c> 前缀后的文件名（不含扩展名）。</param>
    /// <param name="Wrap">是否 wrap（否则 clamp）。</param>
    /// <param name="Bilinear">是否双线性过滤（false = point）。</param>
    public readonly record struct TextureSampler(string RootName, bool Wrap, bool Bilinear);

    /// <summary>
    /// 解析预设声明的纹理 sampler 名（<c>plugin.cpp:2953-2994</c> 的 <c>CacheParams</c> 规则）。
    /// </summary>
    /// <remarks>
    /// 规则：去掉 <c>sampler_</c> 前缀后，若第 3 个字符是 <c>_</c>，前两位是采样方式标记并剥掉：
    /// <c>FW</c>/<c>WF</c> = 双线性 + wrap、<c>FC</c>/<c>CF</c> = 双线性 + clamp、
    /// <c>PW</c>/<c>WP</c> = point + wrap、<c>PC</c>/<c>CP</c> = point + clamp。
    /// 例：<c>sampler_fw_clouds</c> → <c>clouds</c>（<c>textures/clouds.jpg</c>）、
    /// <c>sampler_MilkDrop3_002b</c> → <c>MilkDrop3_002b</c>、
    /// <c>sampler_pw_noise_lq</c> → <c>noise_lq</c>（复用噪点纹理）。
    /// </remarks>
    public static bool TryResolveTextureSampler(string samplerName, out TextureSampler sampler)
    {
        sampler = default;

        if (string.IsNullOrEmpty(samplerName))
        {
            return false;
        }

        var name = samplerName;
        if (name.StartsWith("sampler_", StringComparison.OrdinalIgnoreCase))
        {
            name = name["sampler_".Length..];
        }

        var wrap = true;
        var bilinear = true;

        if (name.Length > 3 && name[2] == '_')
        {
            var first = char.ToUpperInvariant(name[0]);
            var second = char.ToUpperInvariant(name[1]);

            switch (first, second)
            {
                case ('F', 'W'):
                case ('W', 'F'):
                    break;                                   // 双线性 + wrap（默认）
                case ('F', 'C'):
                case ('C', 'F'):
                    wrap = false;
                    break;
                case ('P', 'W'):
                case ('W', 'P'):
                    bilinear = false;
                    break;
                case ('P', 'C'):
                case ('C', 'P'):
                    bilinear = false;
                    wrap = false;
                    break;
            }

            // 原版无论是否认识这三位前缀都会剥掉它（plugin.cpp:2986-2993）
            name = name[3..];
        }

        if (name.Length == 0)
        {
            return false;
        }

        sampler = new TextureSampler(name, wrap, bilinear);
        return true;
    }
}
