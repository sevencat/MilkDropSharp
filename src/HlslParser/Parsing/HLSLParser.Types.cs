namespace HlslParser;

public partial class HLSLParser
{
    internal bool AcceptTypeModifier(ref int flags)
    {
        if (Accept((int)HLSLToken.Const))
        {
            flags |= (int)HLSLTypeFlags.Const;
            return true;
        }
        if (Accept((int)HLSLToken.Static))
        {
            flags |= (int)HLSLTypeFlags.Static;
            return true;
        }
        if (Accept((int)HLSLToken.Uniform))
        {
            flags |= (int)HLSLTypeFlags.Uniform;
            return true;
        }
        if (Accept((int)HLSLToken.Inline))
        {
            // Ignored: in HLSL all functions are inline.
            return true;
        }

        // Not a usage keyword.
        return false;
    }

    internal bool AcceptInterpolationModifier(ref int flags)
    {
        if (Accept("linear"))
        {
            flags |= (int)HLSLTypeFlags.Linear;
            return true;
        }
        if (Accept("centroid"))
        {
            flags |= (int)HLSLTypeFlags.Centroid;
            return true;
        }
        if (Accept("nointerpolation"))
        {
            flags |= (int)HLSLTypeFlags.NoInterpolation;
            return true;
        }
        if (Accept("noperspective"))
        {
            flags |= (int)HLSLTypeFlags.NoPerspective;
            return true;
        }
        if (Accept("sample"))
        {
            flags |= (int)HLSLTypeFlags.Sample;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a type (with modifiers). <paramref name="type"/> is only partially
    /// reset, matching the C++ behaviour (flags and baseType).
    /// </summary>
    internal bool AcceptType(bool allowVoid, ref HLSLType type)
    {
        type.Flags = 0;
        while (AcceptTypeModifier(ref type.Flags) || AcceptInterpolationModifier(ref type.Flags))
        {
        }

        int token = _tokenizer.GetToken();

        // Check built-in types.
        type.BaseType = HLSLBaseType.Void;
        switch ((HLSLToken)token)
        {
            case HLSLToken.Float:
            case HLSLToken.Float1:
            case HLSLToken.Float1x1:
            case HLSLToken.Half:
            case HLSLToken.Half1:
            case HLSLToken.Half1x1:
            case HLSLToken.Double:
            case HLSLToken.Double1:
            case HLSLToken.Double1x1:
                type.BaseType = HLSLBaseType.Float;
                break;

            case HLSLToken.Float2:
            case HLSLToken.Float2x1:
            case HLSLToken.Half2:
            case HLSLToken.Half2x1:
            case HLSLToken.Double2:
            case HLSLToken.Double2x1:
                type.BaseType = HLSLBaseType.Float2;
                break;

            case HLSLToken.Float3:
            case HLSLToken.Float3x1:
            case HLSLToken.Half3:
            case HLSLToken.Half3x1:
            case HLSLToken.Double3:
            case HLSLToken.Double3x1:
                type.BaseType = HLSLBaseType.Float3;
                break;

            case HLSLToken.Float4:
            case HLSLToken.Float4x1:
            case HLSLToken.Half4:
            case HLSLToken.Half4x1:
            case HLSLToken.Double4:
            case HLSLToken.Double4x1:
                type.BaseType = HLSLBaseType.Float4;
                break;

            case HLSLToken.Float2x4:
            case HLSLToken.Half2x4:
            case HLSLToken.Double2x4:
                type.BaseType = HLSLBaseType.Float2x4;
                break;

            case HLSLToken.Float2x3:
            case HLSLToken.Half2x3:
            case HLSLToken.Double2x3:
                type.BaseType = HLSLBaseType.Float2x3;
                break;

            case HLSLToken.Float2x2:
            case HLSLToken.Half2x2:
            case HLSLToken.Double2x2:
                type.BaseType = HLSLBaseType.Float2x2;
                break;

            case HLSLToken.Float3x4:
            case HLSLToken.Half3x4:
            case HLSLToken.Double3x4:
                type.BaseType = HLSLBaseType.Float3x4;
                break;

            case HLSLToken.Float3x3:
            case HLSLToken.Half3x3:
            case HLSLToken.Double3x3:
                type.BaseType = HLSLBaseType.Float3x3;
                break;

            case HLSLToken.Float3x2:
            case HLSLToken.Half3x2:
            case HLSLToken.Double3x2:
                type.BaseType = HLSLBaseType.Float3x2;
                break;

            case HLSLToken.Float4x4:
            case HLSLToken.Half4x4:
            case HLSLToken.Double4x4:
                type.BaseType = HLSLBaseType.Float4x4;
                break;

            case HLSLToken.Float4x3:
            case HLSLToken.Half4x3:
            case HLSLToken.Double4x3:
                type.BaseType = HLSLBaseType.Float4x3;
                break;

            case HLSLToken.Float4x2:
            case HLSLToken.Half4x2:
            case HLSLToken.Double4x2:
                type.BaseType = HLSLBaseType.Float4x2;
                break;

            case HLSLToken.Bool:
                type.BaseType = HLSLBaseType.Bool;
                break;
            case HLSLToken.Bool2:
                type.BaseType = HLSLBaseType.Bool2;
                break;
            case HLSLToken.Bool3:
                type.BaseType = HLSLBaseType.Bool3;
                break;
            case HLSLToken.Bool4:
                type.BaseType = HLSLBaseType.Bool4;
                break;

            case HLSLToken.Int:
                type.BaseType = HLSLBaseType.Int;
                break;
            case HLSLToken.Int2:
                type.BaseType = HLSLBaseType.Int2;
                break;
            case HLSLToken.Int3:
                type.BaseType = HLSLBaseType.Int3;
                break;
            case HLSLToken.Int4:
                type.BaseType = HLSLBaseType.Int4;
                break;

            case HLSLToken.Uint:
                type.BaseType = HLSLBaseType.Uint;
                break;
            case HLSLToken.Uint2:
                type.BaseType = HLSLBaseType.Uint2;
                break;
            case HLSLToken.Uint3:
                type.BaseType = HLSLBaseType.Uint3;
                break;
            case HLSLToken.Uint4:
                type.BaseType = HLSLBaseType.Uint4;
                break;

            case HLSLToken.Texture:
                type.BaseType = HLSLBaseType.Texture;
                break;

            case HLSLToken.Sampler:
                // @@ IC: For now we assume that generic samplers are always sampler2D.
                type.BaseType = HLSLBaseType.Sampler2D;
                break;
            case HLSLToken.Sampler2D:
                type.BaseType = HLSLBaseType.Sampler2D;
                break;
            case HLSLToken.Sampler3D:
                type.BaseType = HLSLBaseType.Sampler3D;
                break;
            case HLSLToken.SamplerCube:
                type.BaseType = HLSLBaseType.SamplerCube;
                break;
            case HLSLToken.Sampler2DShadow:
                type.BaseType = HLSLBaseType.Sampler2DShadow;
                break;
            case HLSLToken.Sampler2DMS:
                type.BaseType = HLSLBaseType.Sampler2DMS;
                break;
            case HLSLToken.Sampler2DArray:
                type.BaseType = HLSLBaseType.Sampler2DArray;
                break;
        }

        if (type.BaseType != HLSLBaseType.Void)
        {
            _tokenizer.Next();

            if (BaseTypes.IsSamplerType(type.BaseType))
            {
                // Parse optional sampler type.
                if (Accept('<'))
                {
                    int token2 = _tokenizer.GetToken();
                    if (token2 == (int)HLSLToken.Float || token2 == (int)HLSLToken.Float1)
                    {
                        type.SamplerType = HLSLBaseType.Float;
                    }
                    else
                    {
                        _tokenizer.Error("Expected half or float.");
                        return false;
                    }
                    _tokenizer.Next();

                    if (!Expect('>'))
                    {
                        _tokenizer.Error("Syntax error: '>' expected for sampler type");
                        return false;
                    }
                }
            }

            return true;
        }

        if (allowVoid && Accept((int)HLSLToken.Void))
        {
            type.BaseType = HLSLBaseType.Void;
            return true;
        }

        if (token == (int)HLSLToken.Identifier)
        {
            string identifier = _tree.AddString(_tokenizer.GetIdentifier());
            if (FindUserDefinedType(identifier) != null)
            {
                _tokenizer.Next();
                type.BaseType = HLSLBaseType.UserDefined;
                type.TypeName = identifier;
                return true;
            }
        }

        return false;
    }

    internal bool ExpectType(bool allowVoid, ref HLSLType type)
    {
        if (!AcceptType(allowVoid, ref type))
        {
            _tokenizer.Error("Expected type");
            return false;
        }
        return true;
    }

    internal bool AcceptDeclaration(bool allowUnsizedArray, ref HLSLType type, out string name)
    {
        name = null;

        if (!AcceptType(false, ref type))
        {
            return false;
        }

        if (!ExpectIdentifier(out name))
        {
            // TODO: false means we didn't accept a declaration and we had an error!
            return false;
        }

        // Handle array syntax.
        if (Accept('['))
        {
            type.Array = true;
            // Optionally allow no size to be specified for the array.
            if (Accept(']') && allowUnsizedArray)
            {
                return true;
            }
            if (!ParseExpression(out type.ArraySize) || !Expect(']'))
            {
                return false;
            }
        }

        return true;
    }

    internal bool ExpectDeclaration(bool allowUnsizedArray, ref HLSLType type, out string name)
    {
        if (!AcceptDeclaration(allowUnsizedArray, ref type, out name))
        {
            _tokenizer.Error("Expected declaration");
            return false;
        }
        return true;
    }
}
