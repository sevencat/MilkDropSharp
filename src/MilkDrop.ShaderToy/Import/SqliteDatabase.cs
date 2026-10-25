using System.Runtime.InteropServices;
using System.Text;

namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// 极小的 SQLite 封装（P/Invoke 系统自带的 <c>winsqlite3.dll</c>，Windows 10 起随系统提供）。
/// </summary>
/// <remarks>
/// <para>
/// 只用到这里需要的四件事：打开、执行 SQL、游标查询、关闭。不引 NuGet（Native AOT 下少一层原生依赖，
/// 也不用操心 SQLitePCLRaw 的加载器），代价是只支持 Windows —— 本 App 本来就只跑 Windows（ANGLE/EGL + winmm）。
/// </para>
/// <para>
/// 结果一律按"字符串或 null"取（<see cref="Query"/>），数字/时间由调用方解析：
/// 库里的列就那几类，写一个通用类型映射反而更容易出错。
/// </para>
/// </remarks>
internal sealed class SqliteDatabase : IDisposable
{
    private const string Library = "winsqlite3.dll";

    private const int ResultOk = 0;
    private const int ResultRow = 100;
    private const int ResultDone = 101;

    private const int OpenReadOnly = 0x00000001;
    private const int OpenReadWrite = 0x00000002;
    private const int OpenCreate = 0x00000004;

    /// <summary><c>SQLITE_TRANSIENT</c>：让 SQLite 复制我们传进去的 UTF-8 缓冲。</summary>
    private static readonly IntPtr Transient = new(-1);

    private IntPtr _db;

    private SqliteDatabase(IntPtr db) => _db = db;

    /// <summary>最近一次失败原因（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 打开一个数据库文件。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="create">true：可写、文件不存在就建（测试/工具用）；false：只读，文件必须已存在。</param>
    /// <returns>打不开（文件缺失、没有 winsqlite3.dll、不是 SQLite 文件）时返回 null。</returns>
    public static SqliteDatabase? TryOpen(string path, bool create, out string? error)
    {
        error = null;

        if (!create && !File.Exists(path))
        {
            error = "文件不存在：" + path;
            return null;
        }

        try
        {
            int flags = create ? OpenReadWrite | OpenCreate : OpenReadOnly;
            int rc = sqlite3_open_v2(Utf8Z(path), out IntPtr db, flags, IntPtr.Zero);

            if (rc != ResultOk)
            {
                string message = db == IntPtr.Zero ? $"错误码 {rc}" : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? $"错误码 {rc}";

                if (db != IntPtr.Zero)
                {
                    sqlite3_close_v2(db);
                }

                error = "打开 " + Path.GetFileName(path) + " 失败：" + message;
                return null;
            }

            return new SqliteDatabase(db);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = "找不到系统自带的 winsqlite3.dll（需要 Windows 10 及以上）：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 执行一条查询，返回所有行；每个单元格按文本取（NULL 为 null）。
    /// </summary>
    public List<string?[]> Query(string sql, params string?[] arguments)
    {
        var rows = new List<string?[]>();

        if (_db == IntPtr.Zero)
        {
            return rows;
        }

        IntPtr statement = Prepare(sql);
        if (statement == IntPtr.Zero)
        {
            return rows;
        }

        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                string value = arguments[index] ?? string.Empty;
                sqlite3_bind_text(statement, index + 1, Utf8Z(value), -1, Transient);
            }

            int columns = sqlite3_column_count(statement);

            while (true)
            {
                int rc = sqlite3_step(statement);

                if (rc == ResultRow)
                {
                    var row = new string?[columns];
                    for (var column = 0; column < columns; column++)
                    {
                        IntPtr text = sqlite3_column_text(statement, column);
                        row[column] = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
                    }

                    rows.Add(row);
                    continue;
                }

                if (rc != ResultDone)
                {
                    LastError = ErrorMessage() ?? $"读取失败（错误码 {rc}）";
                }

                break;
            }
        }
        finally
        {
            sqlite3_finalize(statement);
        }

        return rows;
    }

    /// <summary>执行一段 SQL（可多条语句，用于建库/插入）。</summary>
    public bool Execute(string sql)
    {
        if (_db == IntPtr.Zero)
        {
            return false;
        }

        int rc = sqlite3_exec(_db, Utf8Z(sql), IntPtr.Zero, IntPtr.Zero, out IntPtr errorMessage);

        if (rc == ResultOk)
        {
            return true;
        }

        LastError = errorMessage == IntPtr.Zero
            ? ErrorMessage() ?? $"执行失败（错误码 {rc}）"
            : Marshal.PtrToStringUTF8(errorMessage);

        if (errorMessage != IntPtr.Zero)
        {
            sqlite3_free(errorMessage);
        }

        return false;
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero)
        {
            return;
        }

        sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    private IntPtr Prepare(string sql)
    {
        int rc = sqlite3_prepare_v2(_db, Utf8Z(sql), -1, out IntPtr statement, IntPtr.Zero);

        if (rc == ResultOk && statement != IntPtr.Zero)
        {
            return statement;
        }

        LastError = ErrorMessage() ?? $"准备 SQL 失败（错误码 {rc}）";
        return IntPtr.Zero;
    }

    private string? ErrorMessage() =>
        _db == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(sqlite3_errmsg(_db));

    /// <summary>UTF-8 + 结尾 0（SQLite 的 C 接口要的是这个形式）。</summary>
    private static byte[] Utf8Z(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
        return buffer;
    }

    // x64 只有一种调用约定，CallingConvention 不写也安全；这里保持默认（Winapi）。
    [DllImport(Library, EntryPoint = "sqlite3_open_v2")]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Library, EntryPoint = "sqlite3_close_v2")]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Library, EntryPoint = "sqlite3_errmsg")]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport(Library, EntryPoint = "sqlite3_prepare_v2")]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport(Library, EntryPoint = "sqlite3_step")]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, EntryPoint = "sqlite3_finalize")]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Library, EntryPoint = "sqlite3_column_count")]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport(Library, EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_bind_text")]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int byteCount, IntPtr destructor);

    [DllImport(Library, EntryPoint = "sqlite3_exec")]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr errorMessage);

    [DllImport(Library, EntryPoint = "sqlite3_free")]
    private static extern void sqlite3_free(IntPtr pointer);
}
