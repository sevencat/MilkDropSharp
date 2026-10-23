namespace HlslParser;

public class HLSLStateAssignment : HLSLNode
{
    public HLSLStateAssignment()
        : base(HLSLNodeType.StateAssignment)
    {
    }

    public string StateName;
    public int D3DRenderState;

    // The C++ code uses a union; keep all three and pick by StateName's type.
    public int IValue;
    public float FValue;
    public string SValue;

    public HLSLStateAssignment NextStateAssignment;
}

public class HLSLPass : HLSLNode
{
    public HLSLPass()
        : base(HLSLNodeType.Pass)
    {
    }

    public string Name;
    public int NumStateAssignments;
    public HLSLStateAssignment StateAssignments;
    public HLSLPass NextPass;
}

public class HLSLTechnique : HLSLStatement
{
    public HLSLTechnique()
        : base(HLSLNodeType.Technique)
    {
    }

    public string Name;
    public int NumPasses;
    public HLSLPass Passes;
}

public class HLSLPipeline : HLSLStatement
{
    public HLSLPipeline()
        : base(HLSLNodeType.Pipeline)
    {
    }

    public string Name;
    public int NumStateAssignments;
    public HLSLStateAssignment StateAssignments;
}

public class HLSLStage : HLSLStatement
{
    public HLSLStage()
        : base(HLSLNodeType.Stage)
    {
    }

    public string Name;
    public HLSLStatement Statement;
    public HLSLDeclaration Inputs;
    public HLSLDeclaration Outputs;
}
