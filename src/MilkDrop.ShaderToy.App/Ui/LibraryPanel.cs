using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MilkDrop.ShaderToy.Import;

namespace MilkDrop.ShaderToy.App.Ui;

/// <summary>
/// 左侧"本地库"面板：扫描结果 + 过滤 + 下载/导入 + 右键菜单（P8）。
/// </summary>
/// <remarks>
/// <para>
/// 只有列表，没有"选中项详情"块：播放/打开目录/收藏/删除都放右键菜单里，
/// 面板变矮之后下面留给"pass 列表"（左下角）。
/// </para>
/// <para>
/// **列表顺序在扫描时就定下来**，播放不会重排（<see cref="MarkPlayed"/> 只就地更新那一项）。
/// 隐式重扫（下载/导入/删除后）用 <c>preserveOrder: true</c> 保持既有顺序，新增作品排在最前；
/// 只有"重新扫描"按钮才会按最近播放重新排序。
/// </para>
/// <para>
/// 下载在后台线程跑（<see cref="ShaderToyDownloader"/> 是纯 IO + Http），面板只轮询任务状态；
/// 进度文本由后台线程直接覆盖字段（字符串引用赋值是原子的，不碰 ImGui）。
/// </para>
/// </remarks>
internal sealed class LibraryPanel : IDisposable
{
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly nint _ownerWindow;
    private string _downloadTarget = string.Empty;
    private bool _disposed;

    private HttpClient? _http;
    private Task<DownloadResult>? _downloadTask;
    private CancellationTokenSource? _downloadCts;
    private Task<FetchResult>? _fetchTask;
    private CancellationTokenSource? _fetchCts;
    private string? _downloadProgress;
    private string? _downloadMessage;
    private bool _downloadFailed;
    private ShaderLibraryEntry? _pendingOpen;
    private ShaderLibraryEntry? _deleteTarget;
    private bool _openDeletePopup;

    /// <param name="root">本地库根目录。</param>
    /// <param name="ownerWindow">"打开文件"对话框的父窗口句柄（0 表示不指定）。</param>
    public LibraryPanel(string root, nint ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _ownerWindow = ownerWindow;
        Root = Path.GetFullPath(root);
        Library = ShaderLibrary.Scan(Root);
    }

    /// <summary>库根目录（绝对路径）。</summary>
    public string Root { get; }

    /// <summary>
    /// "补齐缺失资源"成功后触发（参数是作品目录）。
    /// 调用方据此判断"补的是不是正在播的那个"，是的话触发一次重新加载，纹理才会真的换上。
    /// </summary>
    public event Action<string>? AssetsFetched;

    /// <summary>
    /// "重新加载"按钮：从磁盘重新读入正在播放的作品。
    /// 面板不认识播放器，由上层转发（Program 在下一帧开头执行，避免换掉正在用的 GL 资源）。
    /// </summary>
    public event Action? ReloadRequested;

    /// <summary>最近一次扫描结果。</summary>
    public ShaderLibrary Library { get; private set; }

    /// <summary>
    /// 重新扫描目录。
    /// </summary>
    /// <param name="preserveOrder">
    /// true：保持列表里既有作品的顺序（新增的排最前），下载/导入/删除后用；
    /// false：按"最近播放"重新排序（只有用户点"重新扫描"时才这样）。
    /// </param>
    public void Rescan(bool preserveOrder = false)
    {
        Dictionary<string, int>? previousOrder = null;

        if (preserveOrder)
        {
            previousOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < Library.Entries.Count; index++)
            {
                previousOrder[Library.Entries[index].ProjectPath] = index;
            }
        }

        Library = ShaderLibrary.Scan(Root);
        _sizes.Clear();

        if (previousOrder is { Count: > 0 })
        {
            // 已知作品按旧位置，新作品（-1）排最前；OrderBy 是稳定排序，新作品之间保持扫描顺序
            Library.Entries.Sort((a, b) =>
                Rank(a.ProjectPath, previousOrder).CompareTo(Rank(b.ProjectPath, previousOrder)));
        }

        static int Rank(string path, Dictionary<string, int> order) =>
            order.TryGetValue(path, out int index) ? index : -1;
    }

    /// <summary>
    /// 播放后**就地**更新那一项的"最近播放"，不重排（列表顺序在扫描时已固定）。
    /// </summary>
    public void MarkPlayed(string projectPath, DateTimeOffset when)
    {
        int index = Library.Entries.FindIndex(e =>
            string.Equals(e.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            Library.Entries[index] = Library.Entries[index] with { LastPlayed = when };
        }
    }

    /// <summary>
    /// 画面板。
    /// </summary>
    /// <returns>要播放的作品（双击/右键"播放"/刚下载完）；没有时返回 null。</returns>
    /// <param name="size">窗口尺寸（已按 DPI 缩放）。</param>
    /// <param name="cond">位置/尺寸的应用条件（改 DPI 或分辨率时由调用方强制重排）。</param>
    public ShaderLibraryEntry? Draw(Vector2 size, ImGuiCond cond = ImGuiCond.FirstUseEver)
    {
        ShaderLibraryEntry? open = null;

        PollTasks();

        ImGui.SetNextWindowPos(new Vector2(0, 0), cond);
        ImGui.SetNextWindowSize(size, cond);

        if (!ImGui.Begin("本地库", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return null;
        }

        DrawHeader();
        DrawDownloadRow();

        float footer = (ImGui.GetTextLineHeightWithSpacing() * 2f) + (ImGui.GetStyle().ItemSpacing.Y * 2f);
        float listHeight = Math.Max(100f, ImGui.GetContentRegionAvail().Y - footer);

        if (ImGui.BeginChild("##library-list", new Vector2(0, listHeight), ImGuiChildFlags.Border))
        {
            foreach (ShaderLibraryEntry entry in Library.Entries)
            {
                ImGui.PushID(entry.ProjectPath);

                if (ImGui.Selectable(entry.DisplayName, false, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        open = entry;
                    }
                }

                // 右键菜单必须紧跟 Selectable：后面 SameLine 出来的作者/标记会抢走"最后一项"，
                // 右键就只能点在文字上才生效
                bool hovered = ImGui.IsItemHovered();

                if (ImGui.BeginPopupContextItem("##ctx"))
                {
                    if (ImGui.MenuItem("播放"))
                    {
                        open = entry;
                    }

                    if (ImGui.MenuItem("打开目录"))
                    {
                        OpenDirectory(entry.Directory);
                    }

                    if (ImGui.MenuItem(entry.Favorite ? "取消收藏" : "收藏"))
                    {
                        ToggleFavorite(entry);
                    }

                    if (ImGui.MenuItem("补齐缺失资源", string.Empty, false, _fetchTask is null))
                    {
                        StartFetch(entry.Directory);
                    }

                    ImGui.Separator();

                    if (ImGui.MenuItem("删除…", string.Empty, false, entry.HasOwnDirectory))
                    {
                        _deleteTarget = entry;
                        _openDeletePopup = true;
                    }

                    ImGui.EndPopup();
                }

                if (hovered)
                {
                    DrawTooltip(entry);
                }

                if (!string.IsNullOrEmpty(entry.Author))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("— " + entry.Author);
                }

                if (entry.Favorite)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "★");
                }

                if (entry.MissingResources > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), $"缺 {entry.MissingResources} 资源");
                }

                ImGui.PopID();
            }

            if (Library.Entries.Count == 0)
            {
                ImGui.TextDisabled("（空）—— 点上面\"导入 JSON…\"或输入 ID 下载");
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

        DrawDeletePopup();
        ImGui.End();

        if (_pendingOpen is { } pending)
        {
            open = pending;
            _pendingOpen = null;
        }

        return open;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = null;

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = null;

        _http?.Dispose();
        _http = null;
    }

    private void DrawHeader()
    {
        if (ImGui.SmallButton("重新扫描"))
        {
            // 用户显式重扫：按"最近播放"重新排序
            Rescan(preserveOrder: false);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("重新读目录并按最近播放排序（播放动作本身不会打乱列表）");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("重新加载"))
        {
            ReloadRequested?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从磁盘重新读入正在播放的作品。\n改完 project.json、或往作品目录的 textures/ 里放了图之后点这里。");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("导入 JSON…"))
        {
            ImportFromDialog();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把本地 json 拷进库：文件名（不含扩展名）就是作品 ID。\n旁边的图片按 JSON 里的相对路径一起带过来。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{Library.Entries.Count} 个作品");
    }

    /// <summary>下载行：输入 ID 或链接 → 后台下载 → 完成后自动播放。</summary>
    private void DrawDownloadRow()
    {
        bool busy = _downloadTask is { IsCompleted: false };

        ImGui.SetNextItemWidth(-96f);
        ImGui.BeginDisabled(busy);
        bool submitted = ImGui.InputTextWithHint(
            "##download",
            "ID 或链接（flGyWc / https://www.shadertoy.com/view/flGyWc）",
            ref _downloadTarget,
            256,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(_downloadTarget));
        if (ImGui.Button("下载") || (submitted && !busy))
        {
            StartDownload();
        }

        ImGui.EndDisabled();

        if (busy)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), "下载中: " + (_downloadProgress ?? "…"));

            ImGui.SameLine();
            if (ImGui.SmallButton("取消"))
            {
                _downloadCts?.Cancel();
            }
        }
        else if (_downloadMessage is { Length: > 0 } message)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                _downloadFailed ? new Vector4(1f, 0.5f, 0.5f, 1f) : new Vector4(0.55f, 0.9f, 0.6f, 1f),
                message);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawFooter()
    {
        // 底部只留操作提示与路径（选中项详情已收进右键菜单）
        ImGui.TextDisabled("双击播放 / 右键：播放·打开目录·收藏·删除");
        ImGui.TextDisabled("库目录: " + ShortenPath(Root));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Root);
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

    /// <summary>删除确认（右键菜单里点"删除…"后弹）。</summary>
    private void DrawDeletePopup()
    {
        if (_openDeletePopup)
        {
            ImGui.OpenPopup("删除作品");
            _openDeletePopup = false;
        }

        if (!ImGui.BeginPopupModal("删除作品", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        if (_deleteTarget is { } target)
        {
            ImGui.TextUnformatted($"确认删除 {Path.GetFileName(target.Directory)}（{LocalLibrary.DescribeSize(SizeOf(target))}）？");
            ImGui.TextDisabled("连同 textures/ 与 meta.json 一起删除，不可恢复。");

            if (ImGui.Button("确认删除"))
            {
                if (LocalLibrary.TryDelete(Root, target, out string? error))
                {
                    _downloadMessage = $"已删除 {Path.GetFileName(target.Directory)}";
                    _downloadFailed = false;
                }
                else
                {
                    _downloadMessage = error;
                    _downloadFailed = true;
                }

                _deleteTarget = null;
                Rescan(preserveOrder: true);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消"))
            {
                _deleteTarget = null;
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawTooltip(ShaderLibraryEntry entry)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(entry.ProjectPath);
        ImGui.TextDisabled(
            $"{LocalLibrary.DescribeSize(SizeOf(entry))}    修改于 {entry.LastWrite:yyyy-MM-dd HH:mm}"
            + (entry.LastPlayed is { } last ? $"    播放于 {last.LocalDateTime:MM-dd HH:mm}" : string.Empty));

        ImGui.TextDisabled(string.IsNullOrEmpty(entry.Author) ? "作者未知" : entry.Author);

        if (entry.MissingResources > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), $"缺 {entry.MissingResources} 个资源");
        }

        ImGui.EndTooltip();
    }

    /// <summary>弹出文件选择框，把选中的 json 导入库里（文件名即 ID）。</summary>
    private void ImportFromDialog()
    {
        string? path = FileDialog.ShowOpenFile(
            "导入 Shadertoy JSON（文件名即作品 ID）",
            "Shadertoy JSON (*.json)\0*.json\0所有文件 (*.*)\0*.*\0\0",
            _ownerWindow);

        if (path is not null)
        {
            ImportFile(path);
        }
    }

    /// <summary>
    /// 导入一个本地 json（命令行 <c>--import</c> 与界面按钮共用）。
    /// </summary>
    public void ImportFile(string path)
    {
        try
        {
            var importer = new LocalJsonImporter(Root);
            ImportResult result = importer.Import(path, overwrite: true);

            if (!result.Success)
            {
                _downloadFailed = true;
                _downloadMessage = result.Describe();
                return;
            }

            Rescan(preserveOrder: true);
            _downloadFailed = false;
            _downloadMessage = result.Describe()
                               + (result.Warnings.Count > 0
                                   ? "    提示: " + string.Join("；", result.Warnings)
                                   : string.Empty);

            _pendingOpen = Library.Entries.FirstOrDefault(e =>
                               string.Equals(e.Directory, result.Directory, StringComparison.OrdinalIgnoreCase))
                           ?? Library.Find(result.Id);

            // 导入时站点路径拿不到（记在 meta 的 missingResources 里）：自动补下载一次，
            // 补完 AssetsFetched 会让正在播的那个重新加载，纹理就换上去了
            if (result.Directory is { Length: > 0 } directory
                && ShaderMeta.TryLoad(directory) is { MissingResources.Count: > 0 })
            {
                StartFetch(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException)
        {
            _downloadFailed = true;
            _downloadMessage = "导入失败：" + ex.Message;
        }
    }

    /// <summary>启动一次后台下载（不阻塞渲染循环）。</summary>
    private void StartDownload()
    {
        if (!ShaderToyDownloader.TryParseTarget(_downloadTarget, out string id, out _, out string? error))
        {
            _downloadMessage = error;
            _downloadFailed = true;
            return;
        }

        _http ??= ShaderToyDownloader.CreateHttpClient();

        var downloader = new ShaderToyDownloader(Root, _http);
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        _downloadMessage = null;
        _downloadFailed = false;
        _downloadProgress = "解析 " + id + " …";

        var options = new DownloadOptions { Overwrite = true };
        _downloadTask = downloader.DownloadAsync(
            id,
            options,
            new DelegateProgress(text => _downloadProgress = text),
            cts.Token);
    }

    /// <summary>补下载一个作品里还指向站点的资源（后台跑）。</summary>
    private void StartFetch(string directory)
    {
        if (_fetchTask is { IsCompleted: false })
        {
            return;
        }

        _http ??= ShaderToyDownloader.CreateHttpClient();

        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        _downloadFailed = false;
        _downloadMessage = null;
        _downloadProgress = "补齐资源 …";

        var fetcher = new MissingResourceFetcher(_http);
        _fetchTask = fetcher.FetchAsync(
            directory,
            new DownloadOptions(),
            new DelegateProgress(text => _downloadProgress = text),
            cts.Token);
    }

    /// <summary>轮询后台任务（下载 / 补齐资源，每帧调一次）。</summary>
    private void PollTasks()
    {
        PollFetch();

        if (_downloadTask is not { IsCompleted: true } task)
        {
            return;
        }

        _downloadTask = null;
        _downloadProgress = null;
        _downloadCts?.Dispose();
        _downloadCts = null;

        DownloadResult? result = task.IsCompletedSuccessfully ? task.Result : null;

        if (result is null)
        {
            _downloadFailed = true;
            _downloadMessage = task.IsCanceled
                ? "下载已取消。"
                : "下载失败：" + (task.Exception?.GetBaseException().Message ?? "未知错误");
            return;
        }

        if (!result.Success)
        {
            _downloadFailed = true;
            _downloadMessage = result.Describe();
            return;
        }

        Rescan(preserveOrder: true);
        _downloadFailed = false;
        _downloadMessage = result.Describe()
                           + (result.Warnings.Count > 0 ? "    提示: " + string.Join("；", result.Warnings) : string.Empty);

        _pendingOpen = Library.Entries.FirstOrDefault(e =>
                           string.Equals(e.Directory, result.Directory, StringComparison.OrdinalIgnoreCase))
                       ?? Library.Find(result.Id);
    }

    /// <summary>"补齐缺失资源"任务的收尾。</summary>
    private void PollFetch()
    {
        if (_fetchTask is not { IsCompleted: true } task)
        {
            return;
        }

        _fetchTask = null;
        _downloadProgress = null;
        _fetchCts?.Dispose();
        _fetchCts = null;

        FetchResult? result = task.IsCompletedSuccessfully ? task.Result : null;

        if (result is null)
        {
            _downloadFailed = true;
            _downloadMessage = task.IsCanceled
                ? "补齐资源已取消。"
                : "补齐资源失败：" + (task.Exception?.GetBaseException().Message ?? "未知错误");
            return;
        }

        Rescan(preserveOrder: true);
        _downloadFailed = !result.Success || result.Failed > 0;
        _downloadMessage = result.Describe()
                           + (result.StillMissing.Count > 0
                               ? "    首个原因: " + result.StillMissing[0].Error
                               : string.Empty);

        if (result.Success && result.Downloaded > 0)
        {
            AssetsFetched?.Invoke(result.Directory);
        }
    }

    private long SizeOf(ShaderLibraryEntry entry)
    {
        // 平铺布局的 Directory 是库根，缓存键必须按作品区分
        if (_sizes.TryGetValue(entry.ProjectPath, out long cached))
        {
            return cached;
        }

        long size = LocalLibrary.ComputeSize(entry);
        _sizes[entry.ProjectPath] = size;
        return size;
    }

    private void ToggleFavorite(ShaderLibraryEntry entry)
    {
        try
        {
            ShaderMeta? meta = ShaderMeta.TryLoad(entry.Directory);
            if (meta is null)
            {
                _downloadMessage = "这个作品没有 meta.json（手工导入），收藏功能不可用。";
                _downloadFailed = true;
                return;
            }

            meta.Favorite = !meta.Favorite;
            meta.Save(entry.Directory);
            Rescan(preserveOrder: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _downloadMessage = "写 meta.json 失败：" + ex.Message;
            _downloadFailed = true;
        }
    }

    private static void OpenDirectory(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 打不开资源管理器不影响播放
        }
    }

    /// <summary>直接转发到后台线程的进度实现（不用 <c>Progress&lt;T&gt;</c>，它依赖同步上下文）。</summary>
    private sealed class DelegateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
