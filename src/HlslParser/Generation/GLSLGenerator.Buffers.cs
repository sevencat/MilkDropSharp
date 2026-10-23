using System.Diagnostics;

namespace HlslParser;

/// <summary>Port of the constant buffer half of GLSLGenerator.cpp.</summary>
public sealed partial class GLSLGenerator
{
    private void OutputBuffer(int indent, HLSLBuffer buffer)
    {
        // Empty uniform blocks cause compilation errors on NVIDIA, so don't emit them.
        if (buffer.Field == null)
        {
            return;
        }

        if ((_options.Flags & (int)Flags.Flag_EmulateConstantBuffer) != 0)
        {
            uint size = 0;
            LayoutBuffer(buffer, ref size);

            uint uniformSize = (size + 3) / 4;

            _writer.WriteLineTagged(indent, buffer.FileName, buffer.Line, "uniform vec4 %s%s[%d];", _options.ConstantBufferPrefix, buffer.Name, uniformSize);
        }
        else
        {
            _writer.WriteLineTagged(indent, buffer.FileName, buffer.Line, "layout (std140) uniform %s%s {", _options.ConstantBufferPrefix, buffer.Name);
            HLSLDeclaration field = buffer.Field;
            while (field != null)
            {
                _writer.BeginLine(indent + 1, field.FileName, field.Line);
                OutputDeclaration(field.Type, field.Name);
                _writer.Write(";");
                _writer.EndLine();
                field = (HLSLDeclaration)field.NextStatement;
            }
            _writer.WriteLine(indent, "};");
        }
    }

    private static void AlignForWrite(ref uint offset, uint size)
    {
        Debug.Assert(size <= 4);

        if (offset / 4 != (offset + size - 1) / 4)
        {
            offset = (offset + 3) & ~3u;
        }
    }

    private void LayoutBuffer(HLSLBuffer buffer, ref uint offset)
    {
        for (HLSLDeclaration field = buffer.Field; field != null; field = (HLSLDeclaration)field.NextStatement)
        {
            LayoutBuffer(field.Type, ref offset);
        }
    }

    private void LayoutBuffer(in HLSLType type, ref uint offset)
    {
        LayoutBufferAlign(type, ref offset);

        if (type.Array)
        {
            int arraySize = 0;
            _tree.GetExpressionValue(type.ArraySize, out arraySize);

            uint elementSize = 0;
            LayoutBufferElement(type, ref elementSize);

            uint alignedElementSize = (elementSize + 3) & ~3u;

            offset += alignedElementSize * (uint)arraySize;
        }
        else
        {
            LayoutBufferElement(type, ref offset);
        }
    }

    private void LayoutBufferElement(in HLSLType type, ref uint offset)
    {
        if (type.BaseType == HLSLBaseType.Float)
        {
            offset += 1;
        }
        else if (type.BaseType == HLSLBaseType.Float2)
        {
            offset += 2;
        }
        else if (type.BaseType == HLSLBaseType.Float3)
        {
            offset += 3;
        }
        else if (type.BaseType == HLSLBaseType.Float4)
        {
            offset += 4;
        }
        else if (type.BaseType == HLSLBaseType.Float4x4)
        {
            offset += 16;
        }
        else if (type.BaseType == HLSLBaseType.UserDefined)
        {
            HLSLStruct st = _tree.FindGlobalStruct(type.TypeName);

            if (st != null)
            {
                for (HLSLStructField field = st.Field; field != null; field = field.NextField)
                {
                    LayoutBuffer(field.Type, ref offset);
                }
            }
            else
            {
                Error("Unknown type %s", type.TypeName);
            }
        }
        else
        {
            Error("Constant buffer layout is not supported for %s", GetTypeName(type));
        }
    }

    private void LayoutBufferAlign(in HLSLType type, ref uint offset)
    {
        if (type.Array)
        {
            AlignForWrite(ref offset, 4);
        }
        else if (type.BaseType == HLSLBaseType.Float)
        {
            AlignForWrite(ref offset, 1);
        }
        else if (type.BaseType == HLSLBaseType.Float2)
        {
            AlignForWrite(ref offset, 2);
        }
        else if (type.BaseType == HLSLBaseType.Float3)
        {
            AlignForWrite(ref offset, 3);
        }
        else if (type.BaseType == HLSLBaseType.Float4)
        {
            AlignForWrite(ref offset, 4);
        }
        else if (type.BaseType == HLSLBaseType.Float4x4)
        {
            AlignForWrite(ref offset, 4);
        }
        else if (type.BaseType == HLSLBaseType.UserDefined)
        {
            AlignForWrite(ref offset, 4);
        }
        else
        {
            Error("Constant buffer layout is not supported for %s", GetTypeName(type));
        }
    }

    private HLSLBuffer GetBufferAccessExpression(HLSLExpression expression)
    {
        if (expression.NodeType == HLSLNodeType.IdentifierExpression)
        {
            HLSLIdentifierExpression identifierExpression = (HLSLIdentifierExpression)expression;

            if (identifierExpression.Global)
            {
                HLSLDeclaration declaration = _tree.FindGlobalDeclaration(identifierExpression.Name, out HLSLBuffer buffer);

                if (declaration != null && buffer != null)
                {
                    return buffer;
                }
            }
        }
        else if (expression.NodeType == HLSLNodeType.MemberAccess)
        {
            HLSLMemberAccess memberAccess = (HLSLMemberAccess)expression;

            if (memberAccess.Object.ExpressionType.BaseType == HLSLBaseType.UserDefined)
            {
                return GetBufferAccessExpression(memberAccess.Object);
            }
        }
        else if (expression.NodeType == HLSLNodeType.ArrayAccess)
        {
            HLSLArrayAccess arrayAccess = (HLSLArrayAccess)expression;

            if (arrayAccess.Array.ExpressionType.Array)
            {
                return GetBufferAccessExpression(arrayAccess.Array);
            }
        }

        return null;
    }

    private void OutputBufferAccessExpression(HLSLBuffer buffer, HLSLExpression expression, in HLSLType type, uint postOffset)
    {
        if (type.Array)
        {
            Error("Constant buffer access is not supported for arrays (use indexing instead)");
        }
        else if (type.BaseType == HLSLBaseType.Float)
        {
            _writer.Write("%s%s[", _options.ConstantBufferPrefix, buffer.Name);
            uint index = OutputBufferAccessIndex(expression, postOffset);
            _writer.Write("%d].%c", index / 4, (int)"xyzw"[(int)(index % 4)]);
        }
        else if (type.BaseType == HLSLBaseType.Float2)
        {
            _writer.Write("%s%s[", _options.ConstantBufferPrefix, buffer.Name);
            uint index = OutputBufferAccessIndex(expression, postOffset);
            _writer.Write("%d].%s", index / 4, index % 4 == 0 ? "xy" : index % 4 == 1 ? "yz" : "zw");
        }
        else if (type.BaseType == HLSLBaseType.Float3)
        {
            _writer.Write("%s%s[", _options.ConstantBufferPrefix, buffer.Name);
            uint index = OutputBufferAccessIndex(expression, postOffset);
            _writer.Write("%d].%s", index / 4, index % 4 == 0 ? "xyz" : "yzw");
        }
        else if (type.BaseType == HLSLBaseType.Float4)
        {
            _writer.Write("%s%s[", _options.ConstantBufferPrefix, buffer.Name);
            uint index = OutputBufferAccessIndex(expression, postOffset);
            Debug.Assert(index % 4 == 0);
            _writer.Write("%d]", index / 4);
        }
        else if (type.BaseType == HLSLBaseType.Float4x4)
        {
            _writer.Write("mat4(");
            for (int i = 0; i < 4; ++i)
            {
                _writer.Write("%s%s[", _options.ConstantBufferPrefix, buffer.Name);
                uint index = OutputBufferAccessIndex(expression, postOffset + (uint)i * 4);
                Debug.Assert(index % 4 == 0);
                _writer.Write("%d]%c", index / 4, i == 3 ? (int)')' : (int)',');
            }
        }
        else if (type.BaseType == HLSLBaseType.UserDefined)
        {
            HLSLStruct st = _tree.FindGlobalStruct(type.TypeName);

            if (st != null)
            {
                _writer.Write("%s(", st.Name);

                uint offset = postOffset;

                for (HLSLStructField field = st.Field; field != null; field = field.NextField)
                {
                    OutputBufferAccessExpression(buffer, expression, field.Type, offset);

                    if (field.NextField != null)
                    {
                        _writer.Write(",");
                    }

                    LayoutBuffer(field.Type, ref offset);
                }

                _writer.Write(")");
            }
            else
            {
                Error("Unknown type %s", type.TypeName);
            }
        }
        else
        {
            Error("Constant buffer layout is not supported for %s", GetTypeName(type));
        }
    }

    private uint OutputBufferAccessIndex(HLSLExpression expression, uint postOffset)
    {
        if (expression.NodeType == HLSLNodeType.IdentifierExpression)
        {
            HLSLIdentifierExpression identifierExpression = (HLSLIdentifierExpression)expression;
            Debug.Assert(identifierExpression.Global);

            HLSLDeclaration declaration = _tree.FindGlobalDeclaration(identifierExpression.Name, out HLSLBuffer buffer);
            Debug.Assert(declaration != null);
            Debug.Assert(buffer != null);

            uint offset = 0;

            for (HLSLDeclaration field = buffer.Field; field != null; field = (HLSLDeclaration)field.NextStatement)
            {
                if (ReferenceEquals(field, declaration))
                {
                    LayoutBufferAlign(field.Type, ref offset);
                    break;
                }

                LayoutBuffer(field.Type, ref offset);
            }

            return offset + postOffset;
        }
        else if (expression.NodeType == HLSLNodeType.MemberAccess)
        {
            HLSLMemberAccess memberAccess = (HLSLMemberAccess)expression;

            HLSLType type = memberAccess.Object.ExpressionType;
            Debug.Assert(type.BaseType == HLSLBaseType.UserDefined);

            HLSLStruct st = _tree.FindGlobalStruct(type.TypeName);

            if (st != null)
            {
                uint offset = 0;

                for (HLSLStructField field = st.Field; field != null; field = field.NextField)
                {
                    if (String_.Equal(field.Name, memberAccess.Field))
                    {
                        LayoutBufferAlign(field.Type, ref offset);
                        break;
                    }

                    LayoutBuffer(field.Type, ref offset);
                }

                return offset + OutputBufferAccessIndex(memberAccess.Object, postOffset);
            }
            else
            {
                Error("Unknown type %s", type.TypeName);
            }
        }
        else if (expression.NodeType == HLSLNodeType.ArrayAccess)
        {
            HLSLArrayAccess arrayAccess = (HLSLArrayAccess)expression;

            HLSLType type = arrayAccess.Array.ExpressionType;
            Debug.Assert(type.Array);

            uint elementSize = 0;
            LayoutBufferElement(type, ref elementSize);

            uint alignedElementSize = (elementSize + 3) & ~3u;

            int arrayIndex = 0;
            if (_tree.GetExpressionValue(arrayAccess.Index, out arrayIndex))
            {
                uint offset = (uint)arrayIndex * alignedElementSize;

                return offset + OutputBufferAccessIndex(arrayAccess.Array, postOffset);
            }
            else
            {
                _writer.Write("%d*(", alignedElementSize / 4);
                OutputExpression(arrayAccess.Index);
                _writer.Write(")+");

                return OutputBufferAccessIndex(arrayAccess.Array, postOffset);
            }
        }
        else
        {
            Debug.Assert(false, "IsBufferAccessExpression should have returned false");
        }

        return 0;
    }
}
