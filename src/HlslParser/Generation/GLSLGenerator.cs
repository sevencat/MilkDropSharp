using System.Diagnostics;

namespace HlslParser;

/// <summary>
/// Port of <c>GLSLGenerator</c> (vendor/hlslparser/src/GLSLGenerator.cpp).
///
/// Split across partial files to mirror the C++ sections:
/// <list type="bullet">
/// <item>GLSLGenerator.cs — enums, state, <c>Generate</c> prologue (special function output), helpers</item>
/// <item>GLSLGenerator.Expressions.cs — <c>OutputExpression</c>/<c>OutputIdentifier</c></item>
/// <item>GLSLGenerator.Statements.cs — statements, declarations, attributes, entry caller</item>
/// <item>GLSLGenerator.Buffers.cs — cbuffer layout and emulation</item>
/// <item>GLSLGenerator.MatrixCtors.cs — <c>OutputMatrixCtors</c></item>
/// </list>
///
/// PORT: the C++ helper name strings are <c>char[64]</c> buffers filled by
/// <c>ChooseUniqueName</c>; here they are plain strings, which removes the 63
/// byte limit (no behavioural change for realistic identifier lengths).
/// </summary>
public sealed partial class GLSLGenerator
{
    public enum Target
    {
        Target_VertexShader,
        Target_FragmentShader,
    }

    public enum Version
    {
        Version_110, // OpenGL 2.0
        Version_120, // OpenGL 2.1
        Version_140, // OpenGL 3.1
        Version_150, // OpenGL 3.2
        Version_330, // OpenGL 3.3
        Version_100_ES, // OpenGL ES 2.0
        Version_300_ES, // OpenGL ES 3.0
    }

    public enum Flags
    {
        Flag_FlipPositionOutput = 1 << 0,
        Flag_EmulateConstantBuffer = 1 << 1,
        Flag_PackMatrixRowMajor = 1 << 2,
        Flag_LowerMatrixMultiplication = 1 << 3,
        Flag_AlternateNanPropagation = 1 << 4,
    }

    public struct Options
    {
        public int Flags;
        public string ConstantBufferPrefix;

        public Options()
        {
            Flags = 0;
            ConstantBufferPrefix = "";
        }

        public Options(int flags)
        {
            Flags = flags;
            ConstantBufferPrefix = "";
        }
    }

    private enum AttributeModifier
    {
        AttributeModifier_In,
        AttributeModifier_Out,
    }

    private const int s_numReservedWords = 9;

    /// <summary>These are reserved words in GLSL that aren't reserved in HLSL.</summary>
    private static readonly string[] s_reservedWord =
    {
        "output",
        "input",
        "mod",
        "mix",
        "fract",
        "dFdx",
        "dFdy",
        "filter",
        "main",
    };

    private static readonly HLSLType kFloatType = new(HLSLBaseType.Float);
    private static readonly HLSLType kUintType = new(HLSLBaseType.Uint);
    private static readonly HLSLType kIntType = new(HLSLBaseType.Int);
    private static readonly HLSLType kBoolType = new(HLSLBaseType.Bool);

    private static string GetTypeName(in HLSLType type)
    {
        switch (type.BaseType)
        {
            case HLSLBaseType.Void: return "void";
            case HLSLBaseType.Float: return "float";
            case HLSLBaseType.Float2: return "vec2";
            case HLSLBaseType.Float3: return "vec3";
            case HLSLBaseType.Float4: return "vec4";
            case HLSLBaseType.Float2x4: return "mat4x2";
            case HLSLBaseType.Float2x3: return "mat3x2";
            case HLSLBaseType.Float2x2: return "mat2";
            case HLSLBaseType.Float3x4: return "mat4x3";
            case HLSLBaseType.Float3x3: return "mat3";
            case HLSLBaseType.Float3x2: return "mat2x3";
            case HLSLBaseType.Float4x4: return "mat4";
            case HLSLBaseType.Float4x3: return "mat3x4";
            case HLSLBaseType.Float4x2: return "mat2x4";
            case HLSLBaseType.Bool: return "bool";
            case HLSLBaseType.Bool2: return "bvec2";
            case HLSLBaseType.Bool3: return "bvec3";
            case HLSLBaseType.Bool4: return "bvec4";
            case HLSLBaseType.Int: return "int";
            case HLSLBaseType.Int2: return "ivec2";
            case HLSLBaseType.Int3: return "ivec3";
            case HLSLBaseType.Int4: return "ivec4";
            case HLSLBaseType.Uint: return "uint";
            case HLSLBaseType.Uint2: return "uvec2";
            case HLSLBaseType.Uint3: return "uvec3";
            case HLSLBaseType.Uint4: return "uvec4";
            case HLSLBaseType.Texture: return "texture";
            case HLSLBaseType.Sampler: return "sampler";
            case HLSLBaseType.Sampler2D: return "sampler2D";
            case HLSLBaseType.Sampler3D: return "sampler3D";
            case HLSLBaseType.SamplerCube: return "samplerCube";
            case HLSLBaseType.Sampler2DMS: return "sampler2DMS";
            case HLSLBaseType.Sampler2DArray: return "sampler2DArray";
            case HLSLBaseType.UserDefined: return type.TypeName;
            default:
                Debug.Assert(false);
                return "?";
        }
    }

    private static bool GetCanImplicitCast(in HLSLType srcType, in HLSLType dstType)
    {
        return srcType.BaseType == dstType.BaseType;
    }

    private static int GetFunctionArguments(HLSLFunctionCall functionCall, HLSLExpression[] expression, int maxArguments)
    {
        HLSLExpression argument = functionCall.Argument;
        int numArguments = 0;
        while (argument != null)
        {
            if (numArguments < maxArguments)
            {
                expression[numArguments] = argument;
            }
            argument = argument.NextExpression;
            ++numArguments;
        }
        return numArguments;
    }

    /// <summary>Port of the free function <c>commonScalarType</c>.</summary>
    private static HLSLType? CommonScalarType(in HLSLType lhs, in HLSLType rhs)
    {
        if (!HLSLType.IsScalarType(lhs) || !HLSLType.IsScalarType(rhs))
        {
            return null;
        }

        if (lhs.BaseType == HLSLBaseType.Float || rhs.BaseType == HLSLBaseType.Float)
        {
            return kFloatType;
        }

        if (lhs.BaseType == HLSLBaseType.Uint || rhs.BaseType == HLSLBaseType.Uint)
        {
            return kUintType;
        }

        if (lhs.BaseType == HLSLBaseType.Int || rhs.BaseType == HLSLBaseType.Int)
        {
            return kIntType;
        }

        if (lhs.BaseType == HLSLBaseType.Bool || rhs.BaseType == HLSLBaseType.Bool)
        {
            return kBoolType;
        }

        return null;
    }

    private readonly CodeWriter _writer = new(false);
    private readonly IDiagnosticSink _diagnostics;

    private HLSLTree _tree;
    private string _entryName;
    private Target _target;
    private Version _version;
    private bool _versionLegacy;
    private Options _options;

    private bool _outputPosition;
    private int _outputTargets;

    private string _outAttribPrefix;
    private string _inAttribPrefix;

    private string _matrixRowFunction;
    private string _matrixCtorFunction;
    private string _matrixMulFunction;
    private string _clipFunction;
    private string _tex2DlodFunction;
    private string _tex2DbiasFunction;
    private string _tex2DgradFunction;
    private string _tex3DlodFunction;
    private string _tex3DbiasFunction;
    private string _texCUBEbiasFunction;
    private string _texCUBElodFunction;
    private string _scalarSwizzle2Function;
    private string _scalarSwizzle3Function;
    private string _scalarSwizzle4Function;
    private string _sinCosFunction;
    private string _bvecTernary;
    private string _modfFunction;
    private string _acosFunction;
    private string _asinFunction;
    private string _altMultFunction;

    private bool _error;

    private readonly string[] _reservedWord = new string[s_numReservedWords];

    private readonly List<MatrixCtor> _matrixCtors = new();
    private readonly Dictionary<MatrixCtor, string> _matrixCtorsId = new();
    private readonly List<HLSLDeclaration> _globalVarsAssignments = new();

    public GLSLGenerator()
        : this(null)
    {
    }

    public GLSLGenerator(IDiagnosticSink diagnostics)
    {
        _diagnostics = diagnostics ?? NullDiagnosticSink.Instance;

        _tree = null;
        _entryName = null;
        _target = Target.Target_VertexShader;
        _version = Version.Version_330;

        _versionLegacy = false;
        _inAttribPrefix = null;
        _outAttribPrefix = null;
        _error = false;
        _outputPosition = false;
        _outputTargets = 0;
    }

    public bool Generate(HLSLTree tree, Target target, Version version, string entryName)
    {
        return Generate(tree, target, version, entryName, new Options());
    }

    public bool Generate(HLSLTree tree, Target target, Version version, string entryName, Options options)
    {
        _tree = tree;
        _entryName = entryName;
        _target = target;
        _version = version;
        _versionLegacy = version == Version.Version_110 || version == Version.Version_120 || version == Version.Version_100_ES;
        _options = options;

        _globalVarsAssignments.Clear();

        _matrixRowFunction = ChooseUniqueName("matrix_row");
        _matrixCtorFunction = ChooseUniqueName("matrix_ctor");
        _matrixMulFunction = ChooseUniqueName("matrix_mul");
        _clipFunction = ChooseUniqueName("clip");
        _tex2DlodFunction = ChooseUniqueName("tex2Dlod");
        _tex2DbiasFunction = ChooseUniqueName("tex2Dbias");
        _tex2DgradFunction = ChooseUniqueName("tex2Dgrad");
        _tex3DlodFunction = ChooseUniqueName("tex3Dlod");
        _tex3DbiasFunction = ChooseUniqueName("tex3Dbias");
        _texCUBEbiasFunction = ChooseUniqueName("texCUBEbias");
        _texCUBElodFunction = ChooseUniqueName("texCUBElod");
        _modfFunction = ChooseUniqueName("modf");
        _acosFunction = ChooseUniqueName("acos");
        _asinFunction = ChooseUniqueName("asin");
        _altMultFunction = ChooseUniqueName("mult");

        for (int i = 0; i < s_numReservedWords; ++i)
        {
            _reservedWord[i] = ChooseUniqueName(s_reservedWord[i]);
        }

        _scalarSwizzle2Function = ChooseUniqueName("m_scalar_swizzle2");
        _scalarSwizzle3Function = ChooseUniqueName("m_scalar_swizzle3");
        _scalarSwizzle4Function = ChooseUniqueName("m_scalar_swizzle4");

        _sinCosFunction = ChooseUniqueName("sincos");

        _bvecTernary = ChooseUniqueName("bvecTernary");

        if (target == Target.Target_VertexShader)
        {
            _inAttribPrefix = "";
            _outAttribPrefix = "frag_";
        }
        else
        {
            _inAttribPrefix = "frag_";
            _outAttribPrefix = "rast_";
        }

        _tree.ReplaceUniformsAssignments();

        HLSLRoot root = _tree.GetRoot();
        HLSLStatement statement = root.Statement;

        // Find the entry point function.
        HLSLFunction entryFunction = _tree.FindFunction(_entryName);
        if (entryFunction == null)
        {
            Error("Entry point '%s' doesn't exist", _entryName);
            return false;
        }

        if (_version == Version.Version_110)
        {
            _writer.WriteLine(0, "#version 110");
        }
        else if (_version == Version.Version_120)
        {
            _writer.WriteLine(0, "#version 120");
        }
        else if (_version == Version.Version_140)
        {
            _writer.WriteLine(0, "#version 140");

            // Pragmas for NVIDIA.
            _writer.WriteLine(0, "#pragma optionNV(fastmath on)");
            _writer.WriteLine(0, "#pragma optionNV(ifcvt none)");
            _writer.WriteLine(0, "#pragma optionNV(inline all)");
            _writer.WriteLine(0, "#pragma optionNV(strict on)");
            _writer.WriteLine(0, "#pragma optionNV(unroll all)");
        }
        else if (_version == Version.Version_150)
        {
            _writer.WriteLine(0, "#version 150");
        }
        else if (_version == Version.Version_330)
        {
            _writer.WriteLine(0, "#version 330");
        }
        else if (_version == Version.Version_100_ES)
        {
            _writer.WriteLine(0, "#version 100");
            _writer.WriteLine(0, "precision highp float;");
        }
        else if (_version == Version.Version_300_ES)
        {
            _writer.WriteLine(0, "#version 300 es");
            _writer.WriteLine(0, "precision highp float;");
            _writer.WriteLine(0, "precision highp sampler3D;");
        }
        else
        {
            Error("Unrecognized target version");
            return false;
        }

        // Output the special function used to access rows in a matrix.
        // Non-square matrices don't exist before GLSL 1.20 and OpenGL ES 3.0, so
        // their overloads are omitted for 1.10 and ES 1.00 to keep the output valid.
        bool nonSquareMatrices = _version != Version.Version_110 && _version != Version.Version_100_ES;

        _writer.WriteLine(0, "vec2 %s(mat2 m, int i) { return vec2( m[0][i], m[1][i] ); }", _matrixRowFunction);
        _writer.WriteLine(0, "vec2 %s(mat2 m, float i_float) { int i=int(i_float); return vec2( m[0][i], m[1][i] ); }", _matrixRowFunction);
        if (nonSquareMatrices)
        {
            _writer.WriteLine(0, "vec2 %s(mat2x3 m, int i) { return vec2( m[0][i], m[1][i]); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec2 %s(mat2x3 m, float i_float) { int i=int(i_float); return vec2( m[0][i], m[1][i]); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec2 %s(mat2x4 m, int i) { return vec2( m[0][i], m[1][i]); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec2 %s(mat2x4 m, float i_float) { int i=int(i_float); return vec2( m[0][i], m[1][i]); }", _matrixRowFunction);
        }

        _writer.WriteLine(0, "vec3 %s(mat3 m, int i) { return vec3( m[0][i], m[1][i], m[2][i] ); }", _matrixRowFunction);
        _writer.WriteLine(0, "vec3 %s(mat3 m, float i_float) { int i=int(i_float); return vec3( m[0][i], m[1][i], m[2][i] ); }", _matrixRowFunction);
        if (nonSquareMatrices)
        {
            _writer.WriteLine(0, "vec3 %s(mat3x2 m, int i) { return vec3( m[0][i], m[1][i], m[2][i] ); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec3 %s(mat3x2 m, float i_float) { int i=int(i_float); return vec3( m[0][i], m[1][i], m[2][i] ); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec3 %s(mat3x4 m, int i) { return vec3( m[0][i], m[1][i], m[2][i] ); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec3 %s(mat3x4 m, float i_float) { int i=int(i_float); return vec3( m[0][i], m[1][i], m[2][i] ); }", _matrixRowFunction);
        }

        _writer.WriteLine(0, "vec4 %s(mat4 m, int i) { return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }", _matrixRowFunction);
        _writer.WriteLine(0, "vec4 %s(mat4 m, float i_float) { int i=int(i_float); return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }", _matrixRowFunction);
        if (nonSquareMatrices)
        {
            _writer.WriteLine(0, "vec4 %s(mat4x3 m, int i) { return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec4 %s(mat4x3 m, float i_float) { int i=int(i_float); return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec4 %s(mat4x2 m, int i) { return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }", _matrixRowFunction);
            _writer.WriteLine(0, "vec4 %s(mat4x2 m, float i_float) { int i=int(i_float); return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }", _matrixRowFunction);
        }

        // Output the special function used to do matrix cast for OpenGL 2.0
        if (_versionLegacy)
        {
            _writer.WriteLine(0, "mat3 %s(mat4 m) { return mat3(m[0][0], m[0][1], m[0][2], m[1][0], m[1][1], m[1][2], m[2][0], m[2][1], m[2][2]); }", _matrixCtorFunction);
        }

        // Output the special functions used for matrix multiplication lowering
        // They make sure glsl-optimizer can fold expressions better
        if (_tree.NeedsFunction("mul") && (_options.Flags & (int)Flags.Flag_LowerMatrixMultiplication) != 0)
        {
            _writer.WriteLine(0, "vec2 %s(mat2 m, vec2 v) { return m[0] * v.x + m[1] * v.y; }", _matrixMulFunction);
            _writer.WriteLine(0, "vec2 %s(vec2 v, mat2 m) { return vec2(dot(m[0], v), dot(m[1], v)); }", _matrixMulFunction);
            _writer.WriteLine(0, "vec3 %s(mat3 m, vec3 v) { return m[0] * v.x + m[1] * v.y + m[2] * v.z; }", _matrixMulFunction);
            _writer.WriteLine(0, "vec3 %s(vec3 v, mat3 m) { return vec3(dot(m[0], v), dot(m[1], v), dot(m[2], v)); }", _matrixMulFunction);
            _writer.WriteLine(0, "vec4 %s(mat4 m, vec4 v) { return m[0] * v.x + m[1] * v.y + m[2] * v.z + m[3] * v.w; }", _matrixMulFunction);
            _writer.WriteLine(0, "vec4 %s(vec4 v, mat4 m) { return vec4(dot(m[0], v), dot(m[1], v), dot(m[2], v), dot(m[3], v)); }", _matrixMulFunction);
        }

        // Output the special function used to emulate HLSL clip.
        if (_tree.NeedsFunction("clip"))
        {
            string discard = _target == Target.Target_FragmentShader ? "discard" : "";
            _writer.WriteLine(0, "void %s(float x) { if (x < 0.0) %s;  }", _clipFunction, discard);
            _writer.WriteLine(0, "void %s(vec2  x) { if (any(lessThan(x, vec2(0.0, 0.0)))) %s;  }", _clipFunction, discard);
            _writer.WriteLine(0, "void %s(vec3  x) { if (any(lessThan(x, vec3(0.0, 0.0, 0.0)))) %s;  }", _clipFunction, discard);
            _writer.WriteLine(0, "void %s(vec4  x) { if (any(lessThan(x, vec4(0.0, 0.0, 0.0, 0.0)))) %s;  }", _clipFunction, discard);
        }

        // Output the special function used to emulate tex2Dlod.
        if (_tree.NeedsFunction("tex2Dlod"))
        {
            string function = "textureLod";

            if (_version == Version.Version_100_ES)
            {
                _writer.WriteLine(0, "#extension GL_EXT_shader_texture_lod : require");
                function = "texture2DLodEXT";
            }
            else if (_versionLegacy)
            {
                _writer.WriteLine(0, "#extension GL_ARB_shader_texture_lod : require");
                function = "texture2DLod";
            }

            _writer.WriteLine(0, "vec4 %s(sampler2D samp, vec4 texCoord) { return %s(samp, texCoord.xy, texCoord.w);  }", _tex2DlodFunction, function);
        }

        // Output the special function used to emulate tex2Dgrad.
        if (_tree.NeedsFunction("tex2Dgrad"))
        {
            string function = "textureGrad";

            // See tex2Dlod above: ES 1.00 needs the EXT extension, not the ARB one.
            if (_version == Version.Version_100_ES)
            {
                _writer.WriteLine(0, "#extension GL_EXT_shader_texture_lod : require");
                function = "texture2DGradEXT";
            }
            else if (_versionLegacy)
            {
                _writer.WriteLine(0, "#extension GL_ARB_shader_texture_lod : require");
                function = "texture2DGradARB";
            }

            _writer.WriteLine(0, "vec4 %s(sampler2D samp, vec2 texCoord, vec2 dx, vec2 dy) { return %s(samp, texCoord, dx, dy);  }", _tex2DgradFunction, function);
        }

        // Output the special function used to emulate tex2Dbias.
        if (_tree.NeedsFunction("tex2Dbias"))
        {
            if (target == Target.Target_FragmentShader)
            {
                _writer.WriteLine(0, "vec4 %s(sampler2D samp, vec4 texCoord) { return %s(samp, texCoord.xy, texCoord.w);  }", _tex2DbiasFunction, _versionLegacy ? "texture2D" : "texture");
            }
            else
            {
                // Bias value is not supported in vertex shader.
                _writer.WriteLine(0, "vec4 %s(sampler2D samp, vec4 texCoord) { return %s(samp, texCoord.xy);  }", _tex2DbiasFunction, _versionLegacy ? "texture2D" : "texture");
            }
        }

        // Output the special function used to emulate tex2DMSfetch.
        if (_tree.NeedsFunction("tex2DMSfetch"))
        {
            _writer.WriteLine(0, "vec4 tex2DMSfetch(sampler2DMS samp, ivec2 texCoord, int sample) {");
            _writer.WriteLine(1, "return texelFetch(samp, texCoord, sample);");
            _writer.WriteLine(0, "}");
        }

        // Output the special function used to emulate tex3Dlod.
        if (_tree.NeedsFunction("tex3Dlod"))
        {
            _writer.WriteLine(0, "vec4 %s(sampler3D samp, vec4 texCoord) { return %s(samp, texCoord.xyz, texCoord.w);  }", _tex3DlodFunction, _versionLegacy ? "texture3D" : "texture");
        }

        // Output the special function used to emulate tex3Dbias.
        if (_tree.NeedsFunction("tex3Dbias"))
        {
            if (target == Target.Target_FragmentShader)
            {
                _writer.WriteLine(0, "vec4 %s(sampler3D samp, vec4 texCoord) { return %s(samp, texCoord.xyz, texCoord.w);  }", _tex3DbiasFunction, _versionLegacy ? "texture3D" : "texture");
            }
            else
            {
                // Bias value is not supported in vertex shader.
                _writer.WriteLine(0, "vec4 %s(sampler3D samp, vec4 texCoord) { return %s(samp, texCoord.xyz);  }", _tex3DbiasFunction, _versionLegacy ? "texture3D" : "texture");
            }
        }

        // Output the special function used to emulate texCUBEbias.
        if (_tree.NeedsFunction("texCUBEbias"))
        {
            if (target == Target.Target_FragmentShader)
            {
                _writer.WriteLine(0, "vec4 %s(samplerCube samp, vec4 texCoord) { return %s(samp, texCoord.xyz, texCoord.w);  }", _texCUBEbiasFunction, _versionLegacy ? "textureCube" : "texture");
            }
            else
            {
                // Bias value is not supported in vertex shader.
                _writer.WriteLine(0, "vec4 %s(samplerCube samp, vec4 texCoord) { return %s(samp, texCoord.xyz);  }", _texCUBEbiasFunction, _versionLegacy ? "textureCube" : "texture");
            }
        }

        // Output the special function used to emulate texCUBElod
        if (_tree.NeedsFunction("texCUBElod"))
        {
            string function = "textureLod";

            if (_version == Version.Version_110 || _version == Version.Version_120)
            {
                _writer.WriteLine(0, "#extension GL_ARB_shader_texture_lod : require");
                function = "textureCubeLod";
            }
            else if (_version == Version.Version_100_ES)
            {
                _writer.WriteLine(0, "#extension GL_EXT_shader_texture_lod : require");
                function = "textureCubeLodEXT";
            }

            _writer.WriteLine(0, "vec4 %s(samplerCube samp, vec4 texCoord) { return %s(samp, texCoord.xyz, texCoord.w);  }", _texCUBElodFunction, function);
        }

        if (_tree.NeedsFunction("modf"))
        {
            if (_version == Version.Version_110 || _version == Version.Version_120 || _version == Version.Version_100_ES)
            {
                _writer.WriteLine(0, "float %s(float x, out float ip) { ip = int(x); return x - ip; }", _modfFunction);
            }
            else
            {
                _writer.WriteLine(0, "float %s(float x, out float ip) { return modf(x, ip); }", _modfFunction);
            }
        }

        if (_tree.NeedsFunction("acos"))
        {
            /* The shader model 3 implementation of acos and asin extends beyond the [-1,1]
             * range. We need to mimic the behavior of the function outside of this defined
             * domain. To do this, we bail out to a separate function that piecewise-defines
             * a polynomial that qualitatively matches what we see under DX9. The DX9
             * implementation itself is unknown, so this is only a rough match.
             *
             * We also implement this as a separate function because we need to evaluate the
             * argument multiple times. If the argument expression involves side-effects
             * (such as post-increment or -decrement) the result would not be what we expect
             * if we evaluated this equation as a macro in OutputExpression.
             */
            _writer.WriteLine(0, "float %s(float x) { if (abs(x) > 1.0) { return 0.39269908169872415*(4.0-sign(x)*(abs(x) - 3.0)*(abs(x) - 3.0)); } else { return acos(x); } }", _acosFunction);
            _writer.WriteLine(0, "vec2 %s(vec2 x) { vec2 ret; ret.x = %s(x.x); ret.y = %s(x.y); return ret; }", _acosFunction, _acosFunction, _acosFunction);
            _writer.WriteLine(0, "vec3 %s(vec3 x) { vec3 ret; ret.x = %s(x.x); ret.y = %s(x.y); ret.z = %s(x.z); return ret; }", _acosFunction, _acosFunction, _acosFunction, _acosFunction);
            _writer.WriteLine(0, "vec4 %s(vec4 x) { vec4 ret; ret.x = %s(x.x); ret.y = %s(x.y); ret.z = %s(x.z); ret.w = %s(x.w); return ret; }", _acosFunction, _acosFunction, _acosFunction, _acosFunction, _acosFunction);
        }

        if (_tree.NeedsFunction("asin"))
        {
            _writer.WriteLine(0, "float %s(float x) { if (abs(x) > 1.0) { return 0.39269908169872415*sign(x)*(abs(x) - 3.0)*(abs(x) - 3.0); } else { return asin(x); } }", _asinFunction);
            _writer.WriteLine(0, "vec2 %s(vec2 x) { vec2 ret; ret.x = %s(x.x); ret.y = %s(x.y); return ret; }", _asinFunction, _asinFunction, _asinFunction);
            _writer.WriteLine(0, "vec3 %s(vec3 x) { vec3 ret; ret.x = %s(x.x); ret.y = %s(x.y); ret.z = %s(x.z); return ret; }", _asinFunction, _asinFunction, _asinFunction, _asinFunction);
            _writer.WriteLine(0, "vec4 %s(vec4 x) { vec4 ret; ret.x = %s(x.x); ret.y = %s(x.y); ret.z = %s(x.z); ret.w = %s(x.w); return ret; }", _asinFunction, _asinFunction, _asinFunction, _asinFunction, _asinFunction);
        }

        if ((_options.Flags & (int)Flags.Flag_AlternateNanPropagation) != 0)
        {
            /* Implement alternate functions that propagate NaNs like shader model 3 and DX9. */
            _writer.WriteLine(0, "float %s(int i_x, int i_y) { float x=float(i_x); float y=float(i_y); if (x == 0.0 || y == 0.0) { return 0.0; } else { return (x * y); } }", _altMultFunction);
            _writer.WriteLine(0, "float %s(int i_x, float y) { float x=float(i_x); if (x == 0.0 || y == 0.0) { return 0.0; } else { return (x * y); } }", _altMultFunction);
            _writer.WriteLine(0, "float %s(float x, int i_y) { float y=float(i_y); if (x == 0.0 || y == 0.0) { return 0.0; } else { return (x * y); } }", _altMultFunction);
            _writer.WriteLine(0, "float %s(float x, float y) { if (x == 0.0 || y == 0.0) { return 0.0; } else { return (x * y); } }", _altMultFunction);
            _writer.WriteLine(0, "vec2 %s(vec2 x, vec2 y) { return vec2(%s(x.x, y.x), %s(x.y, y.y)); }", _altMultFunction, _altMultFunction, _altMultFunction);
            _writer.WriteLine(0, "vec3 %s(vec3 x, vec3 y) { return vec3(%s(x.x, y.x), %s(x.y, y.y), %s(x.z, y.z)); }", _altMultFunction, _altMultFunction, _altMultFunction, _altMultFunction);
            _writer.WriteLine(0, "vec4 %s(vec4 x, vec4 y) { return vec4(%s(x.x, y.x), %s(x.y, y.y), %s(x.z, y.z), %s(x.w, y.w)); }", _altMultFunction, _altMultFunction, _altMultFunction, _altMultFunction, _altMultFunction);

            // For matrix multiplication just perform the multiplication
            _writer.WriteLine(0, "mat2 %s(mat2 x, mat2 y) { return x * y; }", _altMultFunction);
            _writer.WriteLine(0, "mat3 %s(mat3 x, mat3 y) { return x * y; }", _altMultFunction);
            _writer.WriteLine(0, "mat4 %s(mat4 x, mat4 y) { return x * y; }", _altMultFunction);
        }

        _writer.WriteLine(0, "vec2  %s(float x) { return  vec2(x, x); }", _scalarSwizzle2Function);
        _writer.WriteLine(0, "ivec2 %s(int   x) { return ivec2(x, x); }", _scalarSwizzle2Function);

        _writer.WriteLine(0, "vec3  %s(float x) { return  vec3(x, x, x); }", _scalarSwizzle3Function);
        _writer.WriteLine(0, "ivec3 %s(int   x) { return ivec3(x, x, x); }", _scalarSwizzle3Function);

        _writer.WriteLine(0, "vec4  %s(float x) { return  vec4(x, x, x, x); }", _scalarSwizzle4Function);
        _writer.WriteLine(0, "ivec4 %s(int   x) { return ivec4(x, x, x, x); }", _scalarSwizzle4Function);

        if (!_versionLegacy)
        {
            _writer.WriteLine(0, "uvec2 %s(uint  x) { return uvec2(x, x); }", _scalarSwizzle2Function);
            _writer.WriteLine(0, "uvec3 %s(uint  x) { return uvec3(x, x, x); }", _scalarSwizzle3Function);
            _writer.WriteLine(0, "uvec4 %s(uint  x) { return uvec4(x, x, x, x); }", _scalarSwizzle4Function);
        }

        if (_tree.NeedsFunction("sincos"))
        {
            string[] floatTypes = { "float", "vec2", "vec3", "vec4" };
            for (int i = 0; i < 4; ++i)
            {
                _writer.WriteLine(0, "void %s(%s x, out %s s, out %s c) { s = sin(x); c = cos(x); }", _sinCosFunction,
                    floatTypes[i], floatTypes[i], floatTypes[i]);
            }
        }

        // special function to emulate ?: with bool{2,3,4} condition type
        _writer.WriteLine(0, "vec2 %s(bvec2 cond, vec2 trueExpr, vec2 falseExpr) { vec2 ret; ret.x = cond.x ? trueExpr.x : falseExpr.x; ret.y = cond.y ? trueExpr.y : falseExpr.y; return ret; }", _bvecTernary);
        _writer.WriteLine(0, "vec3 %s(bvec3 cond, vec3 trueExpr, vec3 falseExpr) { vec3 ret; ret.x = cond.x ? trueExpr.x : falseExpr.x; ret.y = cond.y ? trueExpr.y : falseExpr.y; ret.z = cond.z ? trueExpr.z : falseExpr.z; return ret; }", _bvecTernary);
        _writer.WriteLine(0, "vec4 %s(bvec4 cond, vec4 trueExpr, vec4 falseExpr) { vec4 ret; ret.x = cond.x ? trueExpr.x : falseExpr.x; ret.y = cond.y ? trueExpr.y : falseExpr.y; ret.z = cond.z ? trueExpr.z : falseExpr.z; ret.w = cond.w ? trueExpr.w : falseExpr.w; return ret; }", _bvecTernary);

        _tree.EnumerateMatrixCtorsNeeded(_matrixCtors);
        foreach (MatrixCtor ctor in _matrixCtors)
        {
            string id = GetTypeName(new HLSLType(ctor.MatrixType));
            id += "_from";
            foreach (HLSLBaseType argType in ctor.ArgumentTypes)
            {
                id += "_";
                id += GetTypeName(new HLSLType(argType));
            }
            _matrixCtorsId[ctor] = id;
        }

        OutputMatrixCtors();

        // Output the extension used for dFdx/dFdy in GLES2
        if (_version == Version.Version_100_ES && (_tree.NeedsFunction("ddx") || _tree.NeedsFunction("ddy")))
        {
            _writer.WriteLine(0, "#extension GL_OES_standard_derivatives : require");
        }

        OutputAttributes(entryFunction);

        if (_target == Target.Target_FragmentShader)
        {
            if (_outputTargets == 0)
            {
                Error("Fragment shader must output a color");
            }

            if (!_versionLegacy)
            {
                _writer.WriteLine(0, "out vec4 rast_FragData[%d];", _outputTargets);
            }
        }

        OutputStatements(0, statement);
        OutputEntryCaller(entryFunction);

        _tree = null;

        // The GLSL compilers don't check for this, so generate our own error message.
        if (target == Target.Target_VertexShader && !_outputPosition)
        {
            Error("Vertex shader must output a position");
        }

        return !_error;
    }

    public string GetResult()
    {
        return _writer.GetResult();
    }

    private void Error(string format, params object[] args)
    {
        // It's not always convenient to stop executing when an error occurs,
        // so just track once we've hit an error and stop reporting them until
        // we successfully bail out of execution.
        if (_error)
        {
            return;
        }
        _error = true;

        _diagnostics.Error(new Diagnostic(null, 0, CPrintf.Format(format, args)));
    }

    /// <summary>
    /// GLSL contains some reserved words that don't exist in HLSL. This function will
    /// sanitize those names.
    /// </summary>
    private string GetSafeIdentifierName(string name)
    {
        for (int i = 0; i < s_numReservedWords; ++i)
        {
            if (String_.Equal(s_reservedWord[i], name))
            {
                return _reservedWord[i];
            }
        }
        return name;
    }

    /// <summary>
    /// Generates a name of the format "base+n" where n is an integer such that the name
    /// isn't used in the syntax tree.
    /// </summary>
    private string ChooseUniqueName(string baseName)
    {
        for (int i = 0; i < 1024; ++i)
        {
            string name = CPrintf.Format("%s%d", baseName, i);
            if (!_tree.GetContainsString(name))
            {
                return name;
            }
        }
        return null;
    }

    private string GetBuiltInSemantic(string semantic, AttributeModifier modifier)
    {
        return GetBuiltInSemantic(semantic, modifier, out _);
    }

    private string GetBuiltInSemantic(string semantic, AttributeModifier modifier, out int outputIndex)
    {
        outputIndex = -1;

        if (_target == Target.Target_VertexShader && modifier == AttributeModifier.AttributeModifier_Out && String_.Equal(semantic, "POSITION"))
        {
            return "gl_Position";
        }

        if (_target == Target.Target_VertexShader && modifier == AttributeModifier.AttributeModifier_Out && String_.Equal(semantic, "SV_Position"))
        {
            return "gl_Position";
        }

        if (_target == Target.Target_VertexShader && modifier == AttributeModifier.AttributeModifier_Out && String_.Equal(semantic, "PSIZE"))
        {
            return "gl_PointSize";
        }

        if (_target == Target.Target_VertexShader && modifier == AttributeModifier.AttributeModifier_In && String_.Equal(semantic, "SV_InstanceID"))
        {
            return "gl_InstanceID";
        }

        if (_target == Target.Target_FragmentShader && modifier == AttributeModifier.AttributeModifier_Out && String_.Equal(semantic, "SV_Depth"))
        {
            return "gl_FragDepth";
        }

        if (_target == Target.Target_FragmentShader && modifier == AttributeModifier.AttributeModifier_In && String_.Equal(semantic, "SV_Position"))
        {
            return "gl_FragCoord";
        }

        if (_target == Target.Target_FragmentShader && modifier == AttributeModifier.AttributeModifier_Out)
        {
            int index = -1;

            if (semantic != null && semantic.StartsWith("COLOR", StringComparison.Ordinal))
            {
                index = ParseLeadingInt(semantic.Substring(5));
            }
            else if (semantic != null && semantic.StartsWith("SV_Target", StringComparison.Ordinal))
            {
                index = ParseLeadingInt(semantic.Substring(9));
            }

            if (index >= 0)
            {
                if (_outputTargets <= index)
                {
                    _outputTargets = index + 1;
                }

                outputIndex = index;

                return _versionLegacy ? "gl_FragData" : "rast_FragData";
            }
        }

        return null;
    }

    /// <summary>Port of <c>atoi</c>: leading whitespace, optional sign, then digits.</summary>
    private static int ParseLeadingInt(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int index = 0;
        while (index < text.Length && Ascii.IsSpace(text[index]))
        {
            ++index;
        }

        bool negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            ++index;
        }

        int value = 0;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            value = value * 10 + (text[index] - '0');
            ++index;
        }

        return negative ? -value : value;
    }

    private void CompleteConstructorArguments(HLSLExpression expression, HLSLBaseType dstType)
    {
        BaseTypeDescription dstTypeDesc = BaseTypes.Descriptions[(int)dstType];
        int nbComponentsNeeded = dstTypeDesc.NumComponents * dstTypeDesc.Height;

        BaseTypeDescription srcTypeDesc = BaseTypes.Descriptions[(int)expression.ExpressionType.BaseType];
        int nbComponentsProvided = srcTypeDesc.NumComponents * srcTypeDesc.Height;
        if (BaseTypes.IsMatrixType(expression.ExpressionType.BaseType) ||
            BaseTypes.IsVectorType(expression.ExpressionType.BaseType))
        {
            for (int i = nbComponentsProvided; i < nbComponentsNeeded; i++)
            {
                _writer.Write(", 0");
            }
        }
    }
}
