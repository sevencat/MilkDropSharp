namespace HlslParser;

/// <summary>
/// Port of HLSLTokenizer.h's <c>HLSLToken</c> enum.
///
/// The numeric values matter: the tokenizer maps reserved words with
/// <c>256 + index</c> and the parser compares raw ints. Keep the order in sync
/// with <see cref="ReservedWords"/>.
/// </summary>
public enum HLSLToken
{
    // Built-in types.
    Float = 256,
    Float1,
    Float1x1,
    Float2,
    Float2x1,
    Float3,
    Float3x1,
    Float4,
    Float4x1,
    Float2x4,
    Float2x3,
    Float2x2,
    Float3x4,
    Float3x3,
    Float3x2,
    Float4x4,
    Float4x3,
    Float4x2,

    Half,
    Half1,
    Half1x1,
    Half2,
    Half2x1,
    Half3,
    Half3x1,
    Half4,
    Half4x1,
    Half2x4,
    Half2x3,
    Half2x2,
    Half3x4,
    Half3x3,
    Half3x2,
    Half4x4,
    Half4x3,
    Half4x2,

    Double,
    Double1,
    Double1x1,
    Double2,
    Double2x1,
    Double3,
    Double3x1,
    Double4,
    Double4x1,
    Double2x4,
    Double2x3,
    Double2x2,
    Double3x4,
    Double3x3,
    Double3x2,
    Double4x4,
    Double4x3,
    Double4x2,

    Bool,
    Bool2,
    Bool3,
    Bool4,
    Int,
    Int2,
    Int3,
    Int4,
    Uint,
    Uint2,
    Uint3,
    Uint4,

    Texture,
    Sampler,
    Sampler2D,
    Sampler3D,
    SamplerCube,
    Sampler2DShadow,
    Sampler2DMS,
    Sampler2DArray,

    // Reserved words.
    If,
    Else,
    For,
    While,
    Break,
    True,
    False,
    Void,
    Struct,
    CBuffer,
    TBuffer,
    Register,
    Return,
    Continue,
    Discard,
    Const,
    Static,
    Inline,
    PreprocessorDefine,
    PreprocessorIf,
    PreprocessorElse,
    PreprocessorEndif,

    // Input modifiers.
    Uniform,
    In,
    Out,
    InOut,

    // Effect keywords.
    SamplerState,
    Technique,
    Pass,

    // Multi-character symbols.
    LessEqual,
    GreaterEqual,
    EqualEqual,
    NotEqual,
    PlusPlus,
    MinusMinus,
    PlusEqual,
    MinusEqual,
    TimesEqual,
    DivideEqual,
    AndAnd,
    BarBar,

    // Other token types.
    FloatLiteral,
    IntLiteral,
    Identifier,

    EndOfLine,

    EndOfStream,
}
