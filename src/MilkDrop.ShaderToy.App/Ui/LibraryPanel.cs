using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MilkDrop.ShaderToy.Import;

namespace MilkDrop.ShaderToy.App.Ui;

/// <summary>
/// 左侧"本地库"面板：<c>shadertoy.db</c> 里的作品列表（只读）。
/// </summary>
/// <remarks>
/// <para>
/// 作品数据（工程 JSON、pass 代码、通道绑定）全在数据库里，磁盘上只有 <c>media/</c> 下的图片。
/// 所以这里没有下载/导入/删除/收藏 —— 抓取与资源下载由 ShaderToySnap 负责，
/// 本面板只做"选一个播"和"重扫/重载"。
/// </para>
/// <para>
/// 列表顺序在扫描时固定（按抓取时间倒序），播放不重排；只有"重新扫描"按钮会重新排序。
/// </para>
/// </remarks>
internal sealed class LibraryPanel : IDisposable
{
    /// <param name="dataRoot">数据根目录（<c>shadertoy.db</c> 与 <c>media/</c> 都在它下面）。</param>
    public LibraryPanel(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        DataRoot = Path.GetFullPath(dataRoot);
        Library = ShaderLibrary.Scan(DataRoot);
    }

    /// <summary>数据根目录（绝对路径）。</summary>
    public string DataRoot { get; }

    /// <summary>数据库路径（可能不存在）。</summary>
    public string DatabasePath => Library.DatabasePath;

    /// <summary>当前作品数。</summary>
    public int EntryCount => Library.Entries.Count;

    /// <summary>"重新加载"按钮：从数据库重新读入正在播放的作品。</summary>
    public event Action? ReloadRequested;

    /// <summary>最近一次扫描结果。</summary>
    public ShaderLibrary Library { get; private set; }

    /// <summary>重新读数据库（顺序按抓取时间重排）。</summary>
    public void Rescan() => Library = ShaderLibrary.Scan(DataRoot);

    /// <summary>
    /// 画面板。
    /// </summary>
    /// <returns>要播放的作品（双击/右键"播放"）；没有时返回 null。</returns>
    /// <param name="size">面板尺寸（已按 DPI 缩放）。</param>
    /// <param name="cond">位置/尺寸的应用条件（改 DPI 或分辨率时由调用方强制重排）。</param>
    public ShaderLibraryEntry? Draw(Vector2 size, ImGuiCond cond = ImGuiCond.FirstUseEver)
    {
        ShaderLibraryEntry? open = null;

        ImGui.SetNextWindowPos(new Vector2(0, 0), cond);
        ImGui.SetNextWindowSize(size, cond);

        if (!ImGui.Begin("本地库"))
        {
            ImGui.End();
            return null;
        }

        DrawHeader();

        float footer = ImGui.GetTextLineHeightWithSpacing() + (ImGui.GetStyle().ItemSpacing.Y * 2f);
        float listHeight = Math.Max(100f, ImGui.GetContentRegionAvail().Y - footer);

        if (ImGui.BeginChild("##library-list", new Vector2(0, listHeight), ImGuiChildFlags.Border))
        {
            foreach (ShaderLibraryEntry entry in Library.Entries)
            {
                ImGui.PushID(entry.Id);

                if (ImGui.Selectable(entry.DisplayName, false, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        open = entry;
                    }
                }

                // 右键菜单必须紧跟 Selectable：后面 SameLine 出来的作者会抢走"最后一项"
                if (ImGui.BeginPopupContextItem("##ctx"))
                {
                    if (ImGui.MenuItem("播放"))
                    {
                        open = entry;
                    }

                    if (ImGui.MenuItem("打开数据目录"))
                    {
                        OpenDirectory(DataRoot);
                    }

                    ImGui.EndPopup();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.DisplayName}\n{entry.Id}    {(string.IsNullOrEmpty(entry.Author) ? "作者未知" : entry.Author)}");
                }

                // 左栏很窄：作者不换行，超出部分直接裁掉
                if (!string.IsNullOrEmpty(entry.Author))
                {
                    ImGui.PushTextWrapPos(0f);
                    ImGui.SameLine();
                    ImGui.TextDisabled("— " + entry.Author);
                    ImGui.PopTextWrapPos();
                }

                ImGui.PopID();
            }

            if (Library.Entries.Count == 0)
            {
                ImGui.TextDisabled("（空）—— 先用 ShaderToySnap 抓几个作品，或用 --data 指到它的 output 目录");
            }
        }

        ImGui.EndChild();

        DrawFooter();

        if (Library.Warnings.Count > 0 && ImGui.CollapsingHeader($"扫描提示 ({Library.Warnings.Count})"))
        {
            foreach (string warning in Library.Warnings)
            {
                ImGui.BulletText(warning);
            }
        }

        ImGui.End();
        return open;
    }

    /// <summary>面板本身没有要释放的资源（数据库每次查询都用完即关）。</summary>
    public void Dispose()
    {
    }

    private void DrawHeader()
    {
        if (ImGui.SmallButton("重新扫描"))
        {
            Rescan();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("重新读 shadertoy.db（按抓取时间排序；播放动作本身不会打乱列表）");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("重新加载"))
        {
            ReloadRequested?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从数据库重新读入正在播放的作品（在库里改过代码后点这里）；读入失败时保留当前画面。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{Library.Entries.Count} 个作品");
    }

    private void DrawFooter()
    {
        ImGui.TextDisabled("数据库: " + ShortenPath(DatabasePath));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(DatabasePath);
        }
    }

    /// <summary>把长路径压成 <c>…\父目录\自身</c>，否则左栏宽度放不下。</summary>
    private static string ShortenPath(string path)
    {
        if (path.Length <= 34)
        {
            return path;
        }

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        string parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);

        return parent.Length == 0 ? "…" + leaf : $"…{Path.DirectorySeparatorChar}{parent}{Path.DirectorySeparatorChar}{leaf}";
    }

    private static void OpenDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 打不开资源管理器不影响播放
        }
    }
}
