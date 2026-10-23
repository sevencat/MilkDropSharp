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
    public const string WindowTitle = "projectM 移植 P0 — ANGLE / GLES3 环境";

    /// <summary>状态栏（<c>{0}</c> 状态、<c>{1}</c> FPS、<c>{2}</c> 帧号、<c>{3}</c> swap 失败次数）。</summary>
    public const string StatusFormat = "状态: {0}    FPS: {1:0.0}    帧: {2}    swap 失败: {3}";

    /// <summary>重新探测能力按钮。</summary>
    public const string RequeryCapabilities = "重新探测能力";

    /// <summary>错误行前缀。</summary>
    public const string ErrorPrefix = "错误: ";

    /// <summary>「无音频时的占位文案」——保持一处，方便后续 P6 接线时复用。</summary>
    public const string NoPreset = "尚未加载预设";
}
