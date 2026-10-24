namespace MilkDrop.ShaderToy.Import;

/// <summary>JSON 结构不合法（不是 Shadertoy 工程）时抛出。</summary>
public sealed class ShaderToyImportException : Exception
{
    public ShaderToyImportException(string message)
        : base(message)
    {
    }

    public ShaderToyImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
