using System.Numerics;
using MilkDrop.Classic.Render;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>shader 里 <c>texsize*</c> 系列常量需要的纹理尺寸。</summary>
/// <param name="Width">主纹理宽（<c>m_nTexSizeX</c>）。</param>
/// <param name="Height">主纹理高（<c>m_nTexSizeY</c>）。</param>
/// <param name="NoiseLq">noise_lq 边长。</param>
/// <param name="NoiseLqLite">noise_lq_lite 边长。</param>
/// <param name="NoiseMq">noise_mq 边长。</param>
/// <param name="NoiseHq">noise_hq 边长。</param>
/// <param name="NoiseVolLq">noisevol_lq 边长。</param>
/// <param name="NoiseVolHq">noisevol_hq 边长。</param>
public readonly record struct ShaderTextureSizes(
    int Width,
    int Height,
    int NoiseLq = 32,
    int NoiseLqLite = 32,
    int NoiseMq = 64,
    int NoiseHq = 256,
    int NoiseVolLq = 32,
    int NoiseVolHq = 32);

/// <summary>
/// <c>CPlugin::ApplyShaderParams</c>（milkdropfs.cpp:3819-3965）里"写常量"部分的移植。
/// </summary>
/// <remarks>
/// 只负责按名字写常量（<c>_c0.._c13</c>、<c>_qa.._qh</c>、20 个按状态旋转的矩阵 + 4 个纯随机矩阵、
/// <c>rand_frame/rand_preset</c>、<c>texsize_*</c>）；纹理与采样器绑定属渲染 pass。
/// 寄存器号由 <c>IShaderProgram</c> 自己从源码注解里查，所以这里只认名字 —— 名称不存在就跳过，
/// 与 C 版 <c>if (h[i])</c> 的判空等价。
/// </remarks>
public static class ShaderParams
{
    /// <summary>按 include.fx 的顺序排的 20 个"状态矩阵"名字（rot_s1..rot_uf4）。</summary>
    private static readonly string[] StateMatrixNames =
    [
        "rot_s1", "rot_s2", "rot_s3", "rot_s4",
        "rot_d1", "rot_d2", "rot_d3", "rot_d4",
        "rot_f1", "rot_f2", "rot_f3", "rot_f4",
        "rot_vf1", "rot_vf2", "rot_vf3", "rot_vf4",
        "rot_uf1", "rot_uf2", "rot_uf3", "rot_uf4",
    ];

    private static readonly string[] RandomMatrixNames =
        ["rot_rand1", "rot_rand2", "rot_rand3", "rot_rand4"];

    /// <summary>
    /// 把本帧的常量写进 shader。
    /// </summary>
    /// <param name="program">目标 shader（warp 或 comp）。</param>
    /// <param name="state">当前预设状态（提供 q、blur min/max、旋转矩阵随机种子）。</param>
    /// <param name="context">本帧上下文（时间、fps、帧号、progress、声音电平）。</param>
    /// <param name="sizes">纹理尺寸。</param>
    /// <param name="randFrame">本帧随机数 <c>m_rand_frame</c>（0..1）。</param>
    /// <param name="random">给 4 个纯随机矩阵用的随机源（对应 C 的 <c>FRAND</c>）。</param>
    public static void Apply(
        IShaderProgram program,
        PresetState state,
        in PerFrameContext context,
        ShaderTextureSizes sizes,
        Vector4 randFrame,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(state);

        var aspectX = 1f;
        var aspectY = 1f;
        if (sizes.Width > sizes.Height)
        {
            aspectY = sizes.Height / (float)sizes.Width;
        }
        else
        {
            aspectX = sizes.Width / (float)sizes.Height;
        }

        GetSafeBlurMinMax(state, out var blurMin, out var blurMax);

        var time = context.Time;
        var sincePresetStart = time - state.PresetStartTime;
        var sincePresetStartWrapped = sincePresetStart - (int)(sincePresetStart / 10000f) * 10000f;

        SetVector(program, "rand_frame", randFrame);
        SetVector(program, "rand_preset", state.RandPreset);

        // 兼容层：`#define mouse _mouse` 展开到这个常量（只有用到 mouse 的 shader 才会声明它）
        SetVector(program, "_mouse", context.Mouse);

        SetVector(program, "_c0", aspectX, aspectY, 1f / aspectX, 1f / aspectY);
        SetVector(program, "_c1", 0f, 0f, 0f, 0f);
        SetVector(program, "_c2", sincePresetStartWrapped, context.Fps, (float)context.Frame, (float)context.Progress);

        var imm = context.MySound.ImmRel;
        var avg = context.MySound.AvgRel;
        SetVector(program, "_c3", imm[0], imm[1], imm[2], 0.3333f * (imm[0] + imm[1] + imm[2]));
        SetVector(program, "_c4", avg[0], avg[1], avg[2], 0.3333f * (avg[0] + avg[1] + avg[2]));

        SetVector(
            program,
            "_c5",
            blurMax[0] - blurMin[0],
            blurMin[0],
            blurMax[1] - blurMin[1],
            blurMin[1]);
        SetVector(
            program,
            "_c6",
            blurMax[2] - blurMin[2],
            blurMin[2],
            blurMin[0],
            blurMax[0]);

        var texW = sizes.Width;
        SetVector(program, "_c7", texW, sizes.Height, 1f / texW, 1f / sizes.Height);

        // 四个不同频率的 cos/sin 噪声（milkdropfs.cpp:3892-3911）
        SetVector(
            program,
            "_c8",
            0.5f + (0.5f * MathF.Cos((time * 0.329f) + 1.2f)),
            0.5f + (0.5f * MathF.Cos((time * 1.293f) + 3.9f)),
            0.5f + (0.5f * MathF.Cos((time * 5.070f) + 2.5f)),
            0.5f + (0.5f * MathF.Cos((time * 20.051f) + 5.4f)));
        SetVector(
            program,
            "_c9",
            0.5f + (0.5f * MathF.Sin((time * 0.329f) + 1.2f)),
            0.5f + (0.5f * MathF.Sin((time * 1.293f) + 3.9f)),
            0.5f + (0.5f * MathF.Sin((time * 5.070f) + 2.5f)),
            0.5f + (0.5f * MathF.Sin((time * 20.051f) + 5.4f)));
        SetVector(
            program,
            "_c10",
            0.5f + (0.5f * MathF.Cos((time * 0.0050f) + 2.7f)),
            0.5f + (0.5f * MathF.Cos((time * 0.0085f) + 5.3f)),
            0.5f + (0.5f * MathF.Cos((time * 0.0133f) + 4.5f)),
            0.5f + (0.5f * MathF.Cos((time * 0.0217f) + 3.8f)));
        SetVector(
            program,
            "_c11",
            0.5f + (0.5f * MathF.Sin((time * 0.0050f) + 2.7f)),
            0.5f + (0.5f * MathF.Sin((time * 0.0085f) + 5.3f)),
            0.5f + (0.5f * MathF.Sin((time * 0.0133f) + 4.5f)),
            0.5f + (0.5f * MathF.Sin((time * 0.0217f) + 3.8f)));

        // 注意：原版这里 mip_y 也用了 GetWidth()（milkdropfs.cpp:3868，疑似笔误），照抄
        var mipX = MathF.Log2(texW);
        var mipY = MathF.Log2(texW);
        SetVector(program, "_c12", mipX, mipY, 0.5f * (mipX + mipY), 0f);
        SetVector(program, "_c13", blurMin[1], blurMax[1], blurMin[2], blurMax[2]);

        // q1..q32 打包成 8 个 float4
        var q = state.PerFrame.Q;
        for (var i = 0; i < 8; i++)
        {
            SetVector(
                program,
                "_q" + (char)('a' + i),
                (float)q[(i * 4) + 0].Value,
                (float)q[(i * 4) + 1].Value,
                (float)q[(i * 4) + 2].Value,
                (float)q[(i * 4) + 3].Value);
        }

        // 20 个状态矩阵：Rx*(平移)*Rz*Ry（行向量约定，与 D3DX 的乘法顺序一致）
        Span<float> packed = stackalloc float[12];
        for (var i = 0; i < StateMatrixNames.Length; i++)
        {
            if (!program.HasConstant(StateMatrixNames[i]))
            {
                continue;
            }

            var matrix = BuildMatrix(
                state.RotBase[i].X + (state.RotSpeed[i].X * time),
                state.RotBase[i].Y + (state.RotSpeed[i].Y * time),
                state.RotBase[i].Z + (state.RotSpeed[i].Z * time),
                state.Xlate[i]);
            PackMatrix(matrix, packed);
            program.SetVectors(StateMatrixNames[i], packed);
        }

        // 4 个纯随机矩阵（milkdropfs.cpp:3947-3963）
        foreach (var name in RandomMatrixNames)
        {
            if (!program.HasConstant(name))
            {
                continue;
            }

            var matrix = BuildMatrix(
                NextFloat(random) * 6.28f,
                NextFloat(random) * 6.28f,
                NextFloat(random) * 6.28f,
                new Vector3(NextFloat(random), NextFloat(random), NextFloat(random)));
            PackMatrix(matrix, packed);
            program.SetVectors(name, packed);
        }

        // texsize_XYZ（milkdropfs.cpp:3855-3861）
        SetTexSize(program, "texsize_noise_lq", sizes.NoiseLq);
        SetTexSize(program, "texsize_noise_lq_lite", sizes.NoiseLqLite);
        SetTexSize(program, "texsize_noise_mq", sizes.NoiseMq);
        SetTexSize(program, "texsize_noise_hq", sizes.NoiseHq);
        SetTexSize(program, "texsize_noisevol_lq", sizes.NoiseVolLq);
        SetTexSize(program, "texsize_noisevol_hq", sizes.NoiseVolHq);
    }

    /// <summary><c>CPlugin::GetSafeBlurMinMax</c>（milkdropfs.cpp:1468-1499）。</summary>
    public static void GetSafeBlurMinMax(PresetState state, out float[] blurMin, out float[] blurMax)
    {
        blurMin =
        [
            (float)state.PerFrame["blur1_min"].Value,
            (float)state.PerFrame["blur2_min"].Value,
            (float)state.PerFrame["blur3_min"].Value,
        ];
        blurMax =
        [
            (float)state.PerFrame["blur1_max"].Value,
            (float)state.PerFrame["blur2_max"].Value,
            (float)state.PerFrame["blur3_max"].Value,
        ];

        // 精度不能浪费在后面的 blur pass（min-max 间距不能越变越小）；太近就撑开
        const float MinDistance = 0.1f;

        if (blurMax[0] - blurMin[0] < MinDistance)
        {
            var average = (blurMin[0] + blurMax[0]) * 0.5f;
            // 原版两行都写成了 avg - 0.05（milkdropfs.cpp:1482-1483），照抄
            blurMin[0] = average - (MinDistance * 0.5f);
            blurMax[0] = average - (MinDistance * 0.5f);
        }

        blurMax[1] = MathF.Min(blurMax[0], blurMax[1]);
        blurMin[1] = MathF.Max(blurMin[0], blurMin[1]);
        if (blurMax[1] - blurMin[1] < MinDistance)
        {
            var average = (blurMin[1] + blurMax[1]) * 0.5f;
            blurMin[1] = average - (MinDistance * 0.5f);
            blurMax[1] = average - (MinDistance * 0.5f);
        }

        blurMax[2] = MathF.Min(blurMax[1], blurMax[2]);
        blurMin[2] = MathF.Max(blurMin[1], blurMin[2]);
        if (blurMax[2] - blurMin[2] < MinDistance)
        {
            var average = (blurMin[2] + blurMax[2]) * 0.5f;
            blurMin[2] = average - (MinDistance * 0.5f);
            blurMax[2] = average - (MinDistance * 0.5f);
        }
    }

    /// <summary>旋转 X/Y/Z + 平移，乘法顺序与 <c>D3DXMatrixMultiply</c> 链一致。</summary>
    public static Matrix4x4 BuildMatrix(float rotX, float rotY, float rotZ, Vector3 translation)
    {
        var mx = Matrix4x4.CreateRotationX(rotX);
        var my = Matrix4x4.CreateRotationY(rotY);
        var mz = Matrix4x4.CreateRotationZ(rotZ);
        var translate = Matrix4x4.CreateTranslation(translation);

        return mx * translate * mz * my;
    }

    /// <summary>
    /// 把 4x4 压成 <c>float4x3</c> 需要的 12 个 float（前三行）。
    /// </summary>
    public static void PackMatrix(Matrix4x4 matrix, Span<float> destination)
    {
        destination[0] = matrix.M11;
        destination[1] = matrix.M12;
        destination[2] = matrix.M13;
        destination[3] = matrix.M14;
        destination[4] = matrix.M21;
        destination[5] = matrix.M22;
        destination[6] = matrix.M23;
        destination[7] = matrix.M24;
        destination[8] = matrix.M31;
        destination[9] = matrix.M32;
        destination[10] = matrix.M33;
        destination[11] = matrix.M34;
    }

    private static void SetVector(IShaderProgram program, string name, Vector4 value)
        => SetVector(program, name, value.X, value.Y, value.Z, value.W);

    private static void SetVector(IShaderProgram program, string name, float x, float y, float z, float w)
    {
        if (program.HasConstant(name))
        {
            program.SetVector(name, x, y, z, w);
        }
    }

    private static void SetTexSize(IShaderProgram program, string name, int size)
    {
        if (program.HasConstant(name))
        {
            program.SetVector(name, size, size, 1f / size, 1f / size);
        }
    }

    private static float NextFloat(Random random) => (float)random.NextDouble();
}
