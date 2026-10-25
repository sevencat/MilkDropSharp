using MilkDrop.ShaderToy.Import;

using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 测试用的 <c>shadertoy.db</c> 夹具：只建我们用到的表和列，字段名与 ShaderToySnap 的实体一致。
/// </summary>
internal static class TestDatabase
{
    /// <summary>只有 <c>shaders</c> 表：验证"列表只读名字那几列"不用碰其它表。</summary>
    private const string ShadersOnlySchema = """
        create table shaders (
            Id integer primary key autoincrement,
            ShaderId text, Name text, UserName text, Description text, Tags text, RawJson text, Ver text, CapturedAt text);
        """;

    private const string Schema = """
        create table shaders (
            Id integer primary key autoincrement,
            ShaderId text, Name text, UserName text, Description text, Tags text, RawJson text, Ver text, CapturedAt text);
        create table renderpasses (
            Id integer primary key autoincrement,
            ShaderId text, PassIndex integer, Name text, Description text, Type text, Code text);
        create table shader_inputs (
            Id integer primary key autoincrement,
            ShaderId text, PassIndex integer, Channel integer, InputId text, Type text,
            FilePath text, LocalFilePath text, Filter text, Wrap text, VFlip text);
        create table shader_outputs (
            Id integer primary key autoincrement,
            ShaderId text, PassIndex integer, Channel integer, OutputId text);
        """;

    public static void Create(string path) => Execute(path, Schema);

    /// <summary>只建 <c>shaders</c> 表（故意没有 renderpasses/inputs/outputs）。</summary>
    public static void CreateShadersOnly(string path) => Execute(path, ShadersOnlySchema);

    public static void AddShader(
        string path,
        string id,
        string name,
        string author,
        string capturedAt = "2026-01-02 03:04:05",
        string tags = "",
        string rawJson = "",
        string description = "",
        string ver = "0.1")
    {
        Execute(
            path,
            $"""
             insert into shaders (ShaderId, Name, UserName, Description, Tags, RawJson, Ver, CapturedAt)
             values ({Text(id)}, {Text(name)}, {Text(author)}, {Text(description)}, {Text(tags)},
                     {Text(rawJson)}, {Text(ver)}, {Text(capturedAt)});
             """);
    }

    public static void AddPass(string path, string shaderId, int index, string type, string code, string name = "")
    {
        Execute(
            path,
            $"""
             insert into renderpasses (ShaderId, PassIndex, Name, Description, Type, Code)
             values ({Text(shaderId)}, {index}, {Text(name)}, '', {Text(type)}, {Text(code)});
             """);
    }

    public static void AddInput(
        string path,
        string shaderId,
        int passIndex,
        int channel,
        string type,
        string filePath = "",
        string localFile = "",
        string inputId = "",
        string filter = "linear",
        string wrap = "clamp",
        string vflip = "false")
    {
        Execute(
            path,
            $"""
             insert into shader_inputs (ShaderId, PassIndex, Channel, InputId, Type, FilePath, LocalFilePath, Filter, Wrap, VFlip)
             values ({Text(shaderId)}, {passIndex}, {channel}, {Text(inputId)}, {Text(type)}, {Text(filePath)},
                     {Text(localFile)}, {Text(filter)}, {Text(wrap)}, {Text(vflip)});
             """);
    }

    public static void AddOutput(string path, string shaderId, int passIndex, int channel, string outputId)
    {
        Execute(
            path,
            $"""
             insert into shader_outputs (ShaderId, PassIndex, Channel, OutputId)
             values ({Text(shaderId)}, {passIndex}, {channel}, {Text(outputId)});
             """);
    }

    public static void Execute(string path, string sql)
    {
        Assert.True(ShaderDatabase.TryExecute(path, sql, out string? error), error);
    }

    private static string Text(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
