namespace MilkDrop.ProjectM.App;

/// <summary>
/// 界面里用到的中文字串集中放在这里。
/// </summary>
/// <remarks>
/// <c>--font-check</c> 会用反射遍历本类的所有字符串常量，把每个非 ASCII 字符拿去字体图集里查字形，
/// 因此新增界面文案请写在这里，不要散落在 <c>Program</c> 里（散落的文案不会被字体检查覆盖）。
/// </remarks>
internal static class UiText
{
    /// <summary>主窗口标题。</summary>
    public const string WindowTitle = "MilkDrop — projectM 4.1.8 C# 移植";

    /// <summary>状态栏（<c>{0}</c> 状态、<c>{1}</c> FPS、<c>{2}</c> 帧号、<c>{3}</c> swap 失败次数）。</summary>
    public const string StatusFormat = "状态: {0}    FPS: {1:0.0}    帧: {2}    swap 失败: {3}";

    /// <summary>重新探测能力按钮。</summary>
    public const string RequeryCapabilities = "重新探测能力";

    /// <summary>错误行前缀。</summary>
    public const string ErrorPrefix = "错误: ";

    /// <summary>「无音频时的占位文案」。</summary>
    public const string NoPreset = "尚未加载预设";

    /// <summary>播放中。</summary>
    public const string Playing = "播放中";

    /// <summary>已暂停（锁定当前预设）。</summary>
    public const string Paused = "已暂停";

    /// <summary>播放分组标题。</summary>
    public const string PlaybackSection = "播放";

    /// <summary>上一个预设。</summary>
    public const string PreviousPreset = "上一个";

    /// <summary>下一个预设。</summary>
    public const string NextPreset = "下一个";

    /// <summary>随机预设。</summary>
    public const string RandomPreset = "随机";

    /// <summary>锁定当前预设。</summary>
    public const string LockPreset = "锁定当前预设";

    /// <summary>保存截图。</summary>
    public const string TakeScreenshot = "截图";

    /// <summary>参数分组标题。</summary>
    public const string ParametersSection = "参数";

    /// <summary>节拍灵敏度。</summary>
    public const string BeatSensitivity = "节拍灵敏度";

    /// <summary>每预设时长。</summary>
    public const string PresetDuration = "每预设时长（秒）";

    /// <summary>软切时长。</summary>
    public const string BlendDuration = "软切时长（秒）";

    /// <summary>启用节拍硬切。</summary>
    public const string HardCutEnabled = "启用节拍硬切";

    /// <summary>硬切灵敏度。</summary>
    public const string HardCutSensitivity = "硬切灵敏度";

    /// <summary>网格密度。</summary>
    public const string MeshSize = "网格密度";

    /// <summary>宽高比校正。</summary>
    public const string AspectCorrection = "宽高比校正";

    /// <summary>预设时长的随机抖动。</summary>
    public const string RandomDuration = "时长随机抖动";

    /// <summary>预设列表标题。</summary>
    public const string PresetList = "预设列表";

    /// <summary>搜索框占位。</summary>
    public const string SearchHint = "搜索（按文件名过滤）";

    /// <summary>预设清单统计（参数：命中数 / 总数）。</summary>
    public const string PresetCountFormat = "共 {0} 个（显示 {1} 个）";

    /// <summary>列表被截断时的提示（参数：显示条数）。</summary>
    public const string PresetListTruncatedFormat = "列表过长，只显示前 {0} 条，请用搜索缩小范围";

    /// <summary>当前预设行。</summary>
    public const string CurrentPresetFormat = "当前: {0}";

    /// <summary>预设进度行。</summary>
    public const string ProgressFormat = "进度: {0:0}%";

    /// <summary>音频采集中。</summary>
    public const string AudioRunning = "音频: 采集中";

    /// <summary>音频不可用。</summary>
    public const string AudioUnavailable = "音频: 不可用（静音波形）";

    /// <summary>截图已保存（参数：路径）。</summary>
    public const string ScreenshotSavedFormat = "截图已保存: {0}";

    /// <summary>快捷键标题。</summary>
    public const string HotkeysTitle = "快捷键";

    /// <summary>快捷键说明正文。</summary>
    public const string HotkeysBody =
        "Space 暂停/继续\n" +
        "N 或 → 下一个预设\n" +
        "P 或 ← 上一个预设\n" +
        "R 随机预设\n" +
        "L 锁定/解锁当前预设\n" +
        "S 保存截图\n" +
        "F 显示/隐藏界面\n" +
        "H 显示/隐藏本帮助\n" +
        "Esc 或 Q 退出";

    /// <summary>显示/隐藏界面。</summary>
    public const string ToggleUi = "显示界面（F）";

    /// <summary>批量跑完成后的标题。</summary>
    public const string BenchTitle = "批量验证";
}
