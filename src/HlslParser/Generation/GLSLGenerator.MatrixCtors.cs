namespace HlslParser;

/// <summary>Port of <c>GLSLGenerator::OutputMatrixCtors</c>.</summary>
public sealed partial class GLSLGenerator
{
    private void OutputMatrixCtors()
    {
        foreach (MatrixCtor ctor in _matrixCtors)
        {
            _writer.Write("%s %s(",
                GetTypeName(new HLSLType(ctor.MatrixType)),
                _matrixCtorsId[ctor]);
            int argNum = 0;
            foreach (HLSLBaseType argType in ctor.ArgumentTypes)
            {
                if (argNum == 0)
                {
                    _writer.Write("%s %c", GetTypeName(new HLSLType(argType)), (int)('a' + argNum));
                }
                else
                {
                    _writer.Write(", %s %c", GetTypeName(new HLSLType(argType)), (int)('a' + argNum));
                }
                argNum++;
            }
            _writer.Write(") { return %s(", GetTypeName(new HLSLType(ctor.MatrixType)));

            BaseTypeDescription ctorTypeDesc = BaseTypes.Descriptions[(int)ctor.MatrixType];
            var args = new string[ctorTypeDesc.NumComponents * ctorTypeDesc.Height];
            for (int i = 0; i < args.Length; ++i)
            {
                args[i] = "0";
            }

            int argNumIn = 0;
            int argNumOut = 0;
            foreach (HLSLBaseType argType in ctor.ArgumentTypes)
            {
                string arg = ((char)('a' + argNumIn)).ToString();

                if (BaseTypes.IsScalarType(argType))
                {
                    int index = (argNumOut % ctorTypeDesc.Height) * (ctorTypeDesc.NumComponents) +
                                (argNumOut / ctorTypeDesc.Height);
                    args[index] = arg;
                    argNumOut++;
                }
                else if (BaseTypes.IsVectorType(argType))
                {
                    BaseTypeDescription argTypeDesc = BaseTypes.Descriptions[(int)argType];
                    for (int dim = 0; dim < argTypeDesc.NumComponents; dim++)
                    {
                        string argVect = arg + ".";
                        argVect += "xyzw"[dim];
                        int index = (argNumOut % ctorTypeDesc.Height) * (ctorTypeDesc.NumComponents) +
                                    (argNumOut / ctorTypeDesc.Height);
                        args[index] = argVect;
                        argNumOut++;
                    }
                }

                argNumIn++;
            }

            bool first = true;
            foreach (string arg in args)
            {
                if (!first)
                {
                    _writer.Write(",%s", arg);
                }
                else
                {
                    _writer.Write("%s", arg);
                }
                first = false;
            }

            _writer.Write("); }");
            _writer.EndLine();
        }
    }
}
