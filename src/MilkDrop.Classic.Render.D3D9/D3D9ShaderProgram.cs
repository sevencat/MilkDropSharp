using Vortice.Direct3D9;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// D3D9 着色器程序：持有 <c>IDirect3DPixelShader9</c>/<c>IDirect3DVertexShader9</c> + 常量表。
/// </summary>
/// <remarks>
/// 常量按名字写入寄存器（<c>c0..cN</c>），寄存器号来自 shader 源码里的
/// <c>: register(cN)</c> 注解（见 <see cref="ShaderConstantTable"/>），不需要 D3DX 反射。
/// </remarks>
public sealed class D3D9ShaderProgram : IShaderProgram
{
    private readonly IDirect3DDevice9 _device;
    private readonly IDirect3DPixelShader9? _pixelShader;
    private readonly IDirect3DVertexShader9? _vertexShader;
    private readonly ShaderConstantTable _constants;
    private readonly bool _isPixelShader;
    private readonly IReadOnlyCollection<string> _samplerNames;

    private D3D9ShaderProgram(
        string name,
        IDirect3DDevice9 device,
        ShaderConstantTable constants,
        IDirect3DPixelShader9? pixelShader,
        IDirect3DVertexShader9? vertexShader)
    {
        Name = name;
        _device = device;
        _constants = constants;
        _pixelShader = pixelShader;
        _vertexShader = vertexShader;
        _isPixelShader = pixelShader is not null;
        _samplerNames = constants.Samplers.Keys.ToArray();
    }

    public string Name { get; }

    public ShaderConstantTable Constants => _constants;

    public static D3D9ShaderProgram? CreatePixel(
        IDirect3DDevice9 device,
        string name,
        string source,
        out string? error,
        string entryPoint = "PS",
        string profile = ShaderCompiler.DefaultWarpPixelProfile)
    {
        var compiled = ShaderCompiler.Compile(source, entryPoint, profile, name);
        if (!compiled.Success)
        {
            error = compiled.Error;
            return null;
        }

        error = null;
        var shader = device.CreatePixelShader(compiled.Bytecode);
        return new D3D9ShaderProgram(name, device, ShaderConstantTable.Parse(source), shader, null);
    }

    public static D3D9ShaderProgram? CreateVertex(
        IDirect3DDevice9 device,
        string name,
        string source,
        out string? error,
        string entryPoint = "VS",
        string profile = ShaderCompiler.DefaultVertexProfile)
    {
        var compiled = ShaderCompiler.Compile(source, entryPoint, profile, name);
        if (!compiled.Success)
        {
            error = compiled.Error;
            return null;
        }

        error = null;
        var shader = device.CreateVertexShader(compiled.Bytecode);
        return new D3D9ShaderProgram(name, device, ShaderConstantTable.Parse(source), null, shader);
    }

    internal void Bind()
    {
        if (_isPixelShader)
        {
            _device.PixelShader = _pixelShader;
        }
        else
        {
            _device.VertexShader = _vertexShader;
        }
    }

    public bool HasConstant(string name) => _constants.TryGetFloatRegister(name, out _);

    public bool TryGetSamplerStage(string name, out int stage) => _constants.TryGetSampler(name, out stage);

    public IReadOnlyCollection<string> SamplerNames => _samplerNames;

    public void SetFloat(string name, float value) => SetVector(name, value, value, value, value);

    public void SetVector(string name, float x, float y, float z, float w)
    {
        if (!_constants.TryGetFloatRegister(name, out var register))
        {
            return;
        }

        SetRegisters(register, [x, y, z, w]);
    }

    public void SetVectors(string name, ReadOnlySpan<float> packedFloats)
    {
        if (!_constants.TryGetFloatRegister(name, out var register) || packedFloats.Length % 4 != 0)
        {
            return;
        }

        SetRegisters(register, packedFloats.ToArray());
    }

    private void SetRegisters(int startRegister, float[] data)
    {
        var count = (uint)(data.Length / 4);

        if (_isPixelShader)
        {
            _device.SetPixelShaderConstant((uint)startRegister, data, 0, count);
        }
        else
        {
            _device.SetVertexShaderConstant((uint)startRegister, data, 0, count);
        }
    }

    public void Dispose()
    {
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
    }
}
