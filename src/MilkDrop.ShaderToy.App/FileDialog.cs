using System.Runtime.InteropServices;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// Windows 的"打开文件"对话框（comdlg32 的 <c>GetOpenFileNameW</c>）。
/// </summary>
/// <remarks>
/// 不引 WinForms（AOT 不友好）、也不移植上游的 nativefiledialog；应用本来就只在 Windows 上跑
/// （ANGLE/D3D11 + user32 的 DPI/屏幕查询），一个 comdlg32 声明就够了。
/// 对话框是模态的：调用期间主循环停住，但没有任何 GL 调用，不会破坏 ImGui 状态。
/// </remarks>
internal static class FileDialog
{
    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    /// <summary>
    /// 弹出"打开文件"对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="filter">
    /// 过滤器，Win32 格式且必须以**两个** <c>\0</c> 结尾，
    /// 例如 <c>"JSON (*.json)\0*.json\0所有文件 (*.*)\0*.*\0\0"</c>。
    /// </param>
    /// <param name="owner">父窗口句柄（0 表示不指定，对话框可能出现在窗口后面）。</param>
    /// <returns>选中的绝对路径；取消/非 Windows 返回 null。</returns>
    public static string? ShowOpenFile(string title, string filter, nint owner = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[32 * 1024];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            nint bufferPointer = handle.AddrOfPinnedObject();

            var dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filter,
                lpstrFile = bufferPointer,
                nMaxFile = buffer.Length,
                lpstrTitle = title,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY,
            };

            if (!GetOpenFileNameW(ref dialog))
            {
                // 用户取消与真实错误都不需要区分：调用方把 null 当"没选"处理
                return null;
            }

            string? selected = Marshal.PtrToStringUni(bufferPointer);
            return string.IsNullOrEmpty(selected) ? null : selected;
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrFilter;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrCustomFilter;

        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrFileTitle;

        public int nMaxFileTitle;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrInitialDir;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrTitle;

        public int Flags;
        public short nFileOffset;
        public short nFileExtension;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrDefExt;

        public nint lCustData;
        public nint lpfnHook;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpTemplateName;

        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);
}
