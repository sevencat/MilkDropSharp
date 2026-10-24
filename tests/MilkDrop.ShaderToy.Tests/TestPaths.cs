namespace MilkDrop.ShaderToy.Tests;

/// <summary>测试用的仓库路径定位（测试运行时的工作目录在 bin/Debug/... 下）。</summary>
internal static class TestPaths
{
    /// <summary>仓库根目录（含 <c>MilkDrop.ShaderToy.sln</c> 的那一层）。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>上游 desktop-shadertoy 的参考代码（含官方导出样本 <c>default-shader.json</c>）。</summary>
    public static string UpstreamDir { get; } =
        Path.Combine(RepoRoot, "refcode", "desktop-shadertoy-0.4.3");

    /// <summary>上游自带的官方格式样本。</summary>
    public static string DefaultShaderJson { get; } = Path.Combine(UpstreamDir, "default-shader.json");

    /// <summary>仓库里的 Shadertoy 资源目录（参考作品与真实导出）。</summary>
    public static string ShaderToyResDir { get; } = Path.Combine(RepoRoot, "res", "shadertoy");

    /// <summary>真实导出的存放目录（往这里丢 JSON，回归测试会自动带上它）。</summary>
    public static string ShaderToyPresetDir { get; } = Path.Combine(ShaderToyResDir, "stpreset");

    /// <summary>
    /// 真实导出样本 flGyWc（"lunchbreak"）：从网页/编辑器粘出来时字符串被硬换行，
    /// 键名带前导空格、裸 CR/LF、被打断的 <c>\ /</c> 全都在。
    /// </summary>
    public static string FlGyWcJson { get; } = Path.Combine(ShaderToyPresetDir, "flGyWc.json");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MilkDrop.ShaderToy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（MilkDrop.ShaderToy.sln）。");
    }
}
