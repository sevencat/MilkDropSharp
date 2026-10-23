using System.Numerics;
using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Preset;
using MilkDrop.Classic.Visualizer.Sound;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>ApplyShaderParams</c>（milkdropfs.cpp:3819-3965）的常量取值：
/// aspect/时间/progress、q1..q32 打包、blur min/max、24 个矩阵、texsize_*。
/// </summary>
public sealed class ShaderParamsTests
{
    private sealed class RecordingProgram : IShaderProgram
    {
        public Dictionary<string, float[]> Vectors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public string Name => "recording";

        public bool HasConstant(string name) => !Missing.Contains(name);

        public bool TryGetSamplerStage(string name, out int stage)
        {
            stage = -1;
            return false;
        }

        public IReadOnlyCollection<string> SamplerNames => [];

        public void SetFloat(string name, float value) => Vectors[name] = [value];

        public void SetVector(string name, float x, float y, float z, float w) => Vectors[name] = [x, y, z, w];

        public void SetVectors(string name, ReadOnlySpan<float> packedFloats) => Vectors[name] = packedFloats.ToArray();

        public void Dispose()
        {
        }
    }

    private static PerFrameContext Context(
        float time = 10f,
        float fps = 60f,
        long frame = 42,
        float progress = 0.5f,
        float bass = 0.2f,
        float mid = 0.3f,
        float treb = 0.4f)
    {
        var sound = new MySoundData();
        sound.ImmRel[0] = bass;
        sound.ImmRel[1] = mid;
        sound.ImmRel[2] = treb;
        sound.AvgRel[0] = bass / 2f;
        sound.AvgRel[1] = mid / 2f;
        sound.AvgRel[2] = treb / 2f;

        return new PerFrameContext(time, fps, sound, frame, progress, 48, 36, 640, 480, 1f, 1f);
    }

    private static PresetState NewState(float presetStartTime = 4f)
    {
        var state = new PresetState(new Random(1234));
        state.PresetStartTime = presetStartTime;
        return state;
    }

    private static void AssertVector(float[] expected, float[]? actual, float tolerance = 1e-5f)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                MathF.Abs(expected[i] - actual[i]) <= tolerance,
                $"第 {i} 个分量: 期望 {expected[i]}, 实际 {actual[i]}");
        }
    }

    [Fact]
    public void WritesAspectAndTimingConstants()
    {
        var state = NewState();
        var program = new RecordingProgram();

        ShaderParams.Apply(program, state, Context(), new ShaderTextureSizes(640, 480), new Vector4(0.1f, 0.2f, 0.3f, 0.4f), new Random(1));

        // 宽 > 高：aspectY = 480/640 = 0.75
        AssertVector([1f, 0.75f, 1f, 1f / 0.75f], program.Vectors["_c0"]);

        // time - presetStartTime = 6；fps/frame/progress 原样
        AssertVector([6f, 60f, 42f, 0.5f], program.Vectors["_c2"]);

        // 声音：(bass, mid, treb, 平均)
        AssertVector([0.2f, 0.3f, 0.4f, 0.3333f * 0.9f], program.Vectors["_c3"], 1e-4f);
        AssertVector([0.1f, 0.15f, 0.2f, 0.3333f * 0.45f], program.Vectors["_c4"], 1e-4f);

        // texsize / mip
        AssertVector([640f, 480f, 1f / 640f, 1f / 480f], program.Vectors["_c7"]);
        Assert.Equal(MathF.Log2(640f), program.Vectors["_c12"][0], 5);
        Assert.Equal(MathF.Log2(640f), program.Vectors["_c12"][1], 5);

        // rand
        AssertVector([0.1f, 0.2f, 0.3f, 0.4f], program.Vectors["rand_frame"], 1e-6f);
        Assert.Equal(4, program.Vectors["rand_preset"].Length);
    }

    [Fact]
    public void WritesPackedQAndNoiseSizes()
    {
        var state = NewState();
        state.PerFrame.Q[0].Value = 0.5;
        state.PerFrame.Q[4].Value = 2.0;
        state.PerFrame.Q[31].Value = -1.0;

        var program = new RecordingProgram();
        ShaderParams.Apply(program, state, Context(), new ShaderTextureSizes(1024, 768, 32, 16, 64, 256, 8, 4), Vector4.Zero, new Random(1));

        AssertVector([0.5f, 0f, 0f, 0f], program.Vectors["_qa"], 1e-6f);
        AssertVector([2f, 0f, 0f, 0f], program.Vectors["_qb"], 1e-6f);
        AssertVector([0f, 0f, 0f, -1f], program.Vectors["_qh"], 1e-6f);

        AssertVector([32f, 32f, 1f / 32f, 1f / 32f], program.Vectors["texsize_noise_lq"], 1e-6f);
        AssertVector([16f, 16f, 1f / 16f, 1f / 16f], program.Vectors["texsize_noise_lq_lite"], 1e-6f);
        AssertVector([4f, 4f, 0.25f, 0.25f], program.Vectors["texsize_noisevol_hq"], 1e-6f);
    }

    [Fact]
    public void WritesTwentyStateMatricesAndFourRandomOnes()
    {
        var state = NewState();
        for (var i = 0; i < 20; i++)
        {
            state.RotBase[i] = Vector3.Zero;
            state.RotSpeed[i] = Vector3.Zero;
            state.Xlate[i] = Vector3.Zero;
        }

        var program = new RecordingProgram();
        ShaderParams.Apply(program, state, Context(time: 0f), new ShaderTextureSizes(640, 480), Vector4.Zero, new Random(7));

        // 无旋转无平移 → 单位矩阵的前三行
        AssertVector([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f], program.Vectors["rot_s1"], 1e-6f);
        Assert.Equal(12, program.Vectors["rot_uf4"].Length);
        Assert.Equal(12, program.Vectors["rot_rand4"].Length);

        var matrixWrites = program.Vectors.Keys.Count(key => key.StartsWith("rot_", StringComparison.Ordinal));
        Assert.Equal(24, matrixWrites);
    }

    [Fact]
    public void MissingConstantsAreSkipped()
    {
        var state = NewState();
        var program = new RecordingProgram();
        program.Missing.Add("_c3");
        program.Missing.Add("rot_s1");
        program.Missing.Add("texsize_noise_hq");

        ShaderParams.Apply(program, state, Context(), new ShaderTextureSizes(640, 480), Vector4.Zero, new Random(1));

        Assert.False(program.Vectors.ContainsKey("_c3"));
        Assert.False(program.Vectors.ContainsKey("rot_s1"));
        Assert.False(program.Vectors.ContainsKey("texsize_noise_hq"));
        Assert.True(program.Vectors.ContainsKey("_c4"));
    }

    [Fact]
    public void SafeBlurMinMaxPushesApartAndClamps()
    {
        var state = NewState();
        state.PerFrame["blur1_min"].Value = 0.4;
        state.PerFrame["blur1_max"].Value = 0.4;   // 间距 0 → 撑开
        state.PerFrame["blur2_min"].Value = 0.0;
        state.PerFrame["blur2_max"].Value = 0.9;
        state.PerFrame["blur3_min"].Value = 0.1;
        state.PerFrame["blur3_max"].Value = 1.0;

        ShaderParams.GetSafeBlurMinMax(state, out var min, out var max);

        Assert.Equal(0.35f, min[0], 5);
        Assert.Equal(0.35f, max[0], 5);

        // 第二段先被夹到第一段范围（0.35..0.35），间距仍为 0 → 再按原版写法各减 0.05
        Assert.Equal(0.30f, min[1], 5);
        Assert.Equal(0.30f, max[1], 5);

        // 第三段同理级联到 0.25
        Assert.Equal(0.25f, min[2], 5);
        Assert.Equal(0.25f, max[2], 5);
    }

    [Fact]
    public void BuildMatrixMatchesRowVectorOrder()
    {
        // RotX(90°) 应把 +Y 转到 +Z（行向量：v * M）
        var matrix = ShaderParams.BuildMatrix(MathF.PI / 2, 0, 0, Vector3.Zero);
        var rotated = Vector3.Transform(new Vector3(0, 1, 0), matrix);

        Assert.Equal(0f, rotated.X, 5);
        Assert.Equal(0f, rotated.Y, 5);
        Assert.Equal(1f, rotated.Z, 5);
    }
}
