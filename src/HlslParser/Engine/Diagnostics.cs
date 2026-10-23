namespace HlslParser;

/// <summary>
/// Port of the C++ <c>Log_Error</c> path. The C++ implementation is a no-op
/// (the actual LOG_ERROR call is commented out), so the default sink is silent
/// to keep behaviour identical. Tests and hosts can attach a collecting sink.
/// </summary>
public readonly record struct Diagnostic(string FileName, int Line, string Message);

public interface IDiagnosticSink
{
    void Error(Diagnostic diagnostic);
}

/// <summary>Matches the C++ behaviour: errors are swallowed.</summary>
public sealed class NullDiagnosticSink : IDiagnosticSink
{
    public static readonly NullDiagnosticSink Instance = new();

    private NullDiagnosticSink()
    {
    }

    public void Error(Diagnostic diagnostic)
    {
    }
}

public sealed class CollectingDiagnosticSink : IDiagnosticSink
{
    public List<Diagnostic> Diagnostics { get; } = new();

    public void Error(Diagnostic diagnostic)
    {
        Diagnostics.Add(diagnostic);
    }
}
