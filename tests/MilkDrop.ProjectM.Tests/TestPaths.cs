namespace MilkDrop.ProjectM.Tests;

/// <summary>测试用的仓库路径定位（测试运行时的工作目录在 bin/Debug/... 下）。</summary>
internal static class TestPaths
{
    /// <summary>仓库根目录（含 <c>MilkDrop.ProjectM.sln</c> 的那一层）。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>原生 projectM 的预设/纹理资源目录。</summary>
    public static string ProjectMResDir { get; } = Path.Combine(RepoRoot, "res", "projectMSDL-2.0.0-win64");

    /// <summary>预设目录（约 9795 个 <c>.milk</c>）。</summary>
    public static string PresetDir { get; } = Path.Combine(ProjectMResDir, "presets");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MilkDrop.ProjectM.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（MilkDrop.ProjectM.sln）。");
    }
}
