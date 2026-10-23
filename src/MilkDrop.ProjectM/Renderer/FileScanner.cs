namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 递归扫描若干根目录，把匹配扩展名的文件回调出去（扩展名不区分大小写）。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/FileScanner.hpp:16-41</c> 与 <c>FileScanner.cpp:15-80</c>。</para>
/// <para>
/// C++ 用 <c>recursive_directory_iterator</c> 并对符号链接做了解析；C# 侧用
/// <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/>
/// 递归枚举，跳过系统文件与不可访问目录（原版遇到异常是静默忽略，这里保持一致）。
/// </para>
/// </remarks>
public sealed class FileScanner
{
    private readonly string[] _rootDirs;
    private readonly string[] _extensions;

    /// <summary>扫描回调：完整路径 + 不带扩展名的文件名。</summary>
    public delegate void ScanCallback(string path, string basename);

    /// <param name="rootDirs">要扫描的根目录。</param>
    /// <param name="extensions">扩展名列表（带点，例如 <c>.jpg</c>），构造时统一转小写。</param>
    public FileScanner(IEnumerable<string> rootDirs, IEnumerable<string> extensions)
    {
        _rootDirs = rootDirs.ToArray();
        _extensions = extensions.Select(Utils.ToLower).ToArray();
    }

    /// <summary>扫描所有根目录。</summary>
    /// <remarks>根目录重叠时同一文件会被回调多次（与 C++ 行为一致）。</remarks>
    public void Scan(ScanCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var rootDir in _rootDirs)
        {
            try
            {
                if (!Directory.Exists(rootDir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(rootDir, "*", options))
                {
                    var extension = Utils.ToLower(Path.GetExtension(file));
                    if (extension.Length == 0 || Array.IndexOf(_extensions, extension) < 0)
                    {
                        continue;
                    }

                    callback(file, Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (IOException)
            {
                // 原版同样静默忽略（FileScanner.cpp:71-78）。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
