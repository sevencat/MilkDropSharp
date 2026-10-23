using MilkDrop.Classic.Render;
using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Sprites;

/// <summary><c>texmgr.h:42-58</c> 的错误码（低 4 位是错误、高 4 位是警告）。</summary>
public enum SpriteLoadError
{
    Success = 0,
    BadIndex = 1,
    Format = 8,
    BadFile = 9,
    OutOfMemory = 10,
}

/// <summary><c>texmgr.h:56-58</c> 的警告位。</summary>
[Flags]
public enum SpriteLoadWarnings
{
    None = 0,
    InitCode = 0x10,
    FrameCode = 0x20,
}

public readonly record struct SpriteLoadResult(SpriteLoadError Error, SpriteLoadWarnings Warnings)
{
    public bool Ok => Error == SpriteLoadError.Success;
}

/// <summary>
/// 把一个图片文件解码成可采样纹理。失败返回 null（由调用方转成 <see cref="SpriteLoadError.BadFile"/>）。
/// </summary>
/// <param name="fileName">绝对路径。</param>
/// <param name="colorKey">要抠掉的颜色（0x00RRGGBB，0 = 黑色）。</param>
public delegate ITexture? SpriteTextureLoader(string fileName, uint colorKey);

/// <summary>
/// <c>texmgr</c>（<c>texmgr.cpp</c>）的移植：16 个 sprite 槽，每槽一张纹理 + 一套 EEL VM。
/// </summary>
/// <remarks>
/// 与原版的差异（有意，均不影响画面）：
/// <list type="number">
///   <item>原版的 <c>RecompileExpressions</c>（<c>texmgr.cpp:241-259</c>）里<b>编译调用被删掉了</b>，
///   所以参考实现里 sprite 的 per-frame 代码其实从没执行过、<c>m_codehandle</c> 恒为 NULL。
///   这里按 MilkDrop 2 的原意恢复编译（否则 sprite 只能静止在中心）。</item>
///   <item>纹理复用：原版把同一个 <c>pSurface</c> 指针拷到多个槽并靠引用计数释放，
///   这里同样共享 <see cref="ITexture"/>，用引用计数决定何时 Dispose。</item>
///   <item>mip 链不生成（后端只有单级纹理），对小图 sprite 无可见差异。</item>
/// </list>
/// </remarks>
public sealed class SpriteManager
{
    /// <summary><c>NUM_TEX</c>（<c>texmgr.h:33</c>）。</summary>
    public const int NumSlots = 16;

    private readonly SpriteSlot[] _slots = new SpriteSlot[NumSlots];
    private readonly Dictionary<ITexture, int> _textureRefs = new(ReferenceEqualityComparer.Instance);
    private readonly SpriteTextureLoader _loader;

    public SpriteManager(IExpressionEngine engine, SpriteTextureLoader loader)
    {
        _loader = loader;
        for (var i = 0; i < NumSlots; i++)
        {
            _slots[i] = new SpriteSlot(engine, i);
        }
    }

    public IReadOnlyList<SpriteSlot> Slots => _slots;

    /// <summary>当前激活（有纹理）的槽数。</summary>
    public int ActiveCount => _slots.Count(slot => slot.IsActive);

    /// <summary>按槽号取槽（越界抛异常）。</summary>
    public SpriteSlot this[int index] => _slots[index];

    /// <summary>
    /// 找空槽；没有空槽就回收"启动帧最早"的那个（<c>plugin.cpp:8641-8665</c>）。
    /// </summary>
    public int FindSlotForNewSprite()
    {
        var oldestIndex = 0;
        var oldestFrame = _slots[0].StartFrame;

        for (var i = 0; i < NumSlots; i++)
        {
            if (!_slots[i].IsActive)
            {
                return i;
            }

            if (_slots[i].StartFrame < oldestFrame)
            {
                oldestIndex = i;
                oldestFrame = _slots[i].StartFrame;
            }
        }

        return oldestIndex;
    }

    /// <summary>
    /// 把图片装进指定槽（<c>texmgr.cpp:70-149</c>）。
    /// </summary>
    /// <param name="fileName">图片绝对路径。</param>
    /// <param name="slot">0..15。</param>
    /// <param name="initCode">已剥注释的 init 代码（可为空）。</param>
    /// <param name="frameCode">已剥注释的 per-frame 代码（可为空）。</param>
    /// <param name="time">spawn 时刻。</param>
    /// <param name="frame">spawn 帧号。</param>
    /// <param name="colorKey">透明色（0x00RRGGBB）。</param>
    public SpriteLoadResult LoadTexture(
        string fileName,
        int slot,
        string initCode,
        string frameCode,
        float time,
        long frame,
        uint colorKey)
    {
        if (slot < 0 || slot >= NumSlots)
        {
            return new SpriteLoadResult(SpriteLoadError.BadIndex, SpriteLoadWarnings.None);
        }

        var target = _slots[slot];

        // 同一个文件已经装过：共用纹理，只重建表达式（texmgr.cpp:75-88）。
        var instance = Array.Find(
            _slots,
            other => other.IsActive
                     && !ReferenceEquals(other, target)
                     && string.Equals(other.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (instance is not null)
        {
            KillTexture(slot);
            target.Texture = instance.Texture;
            target.ImageWidth = instance.ImageWidth;
            target.ImageHeight = instance.ImageHeight;
            target.FileName = instance.FileName;
            AddTextureRef(instance.Texture!);
        }
        else
        {
            KillTexture(slot);

            ITexture? texture;
            try
            {
                texture = _loader(fileName, colorKey);
            }
            catch (OutOfMemoryException)
            {
                return new SpriteLoadResult(SpriteLoadError.OutOfMemory, SpriteLoadWarnings.None);
            }
            catch
            {
                texture = null;
            }

            if (texture is null)
            {
                return new SpriteLoadResult(SpriteLoadError.BadFile, SpriteLoadWarnings.None);
            }

            target.Texture = texture;
            target.ImageWidth = texture.Width;
            target.ImageHeight = texture.Height;
            target.FileName = fileName;
            AddTextureRef(texture);
        }

        target.StartTime = time;
        target.StartFrame = frame;
        target.UserData = 0;
        target.CodeError = null;
        target.FrameCode = null;
        target.FrameCodeText = string.Empty;

        var warnings = SpriteLoadWarnings.None;

        // 1. init 代码：编译 + 执行一次
        if (!target.RunInitCode(initCode, out var initError))
        {
            warnings |= SpriteLoadWarnings.InitCode;
            target.CodeError = $"sprite {slot} init: {initError}";
        }

        // 2. per-frame 代码：只编译
        target.FrameCodeText = frameCode;
        if (!string.IsNullOrWhiteSpace(frameCode))
        {
            target.FrameCode = target.Vm.Compile(frameCode);
            if (target.FrameCode is null)
            {
                warnings |= SpriteLoadWarnings.FrameCode;
                target.CodeError = $"sprite {slot} code: {target.Vm.LastError}";
            }
        }

        return new SpriteLoadResult(SpriteLoadError.Success, warnings);
    }

    /// <summary>释放一个槽（<c>texmgr.cpp:151-172</c>）：纹理引用计数归零才 Dispose。</summary>
    public void KillTexture(int slot)
    {
        if (slot < 0 || slot >= NumSlots)
        {
            return;
        }

        var target = _slots[slot];
        if (target.Texture is not null)
        {
            ReleaseTextureRef(target.Texture);
            target.Texture = null;
        }

        target.FileName = string.Empty;
        target.ImageWidth = 0;
        target.ImageHeight = 0;
        target.FrameCode = null;
        target.FrameCodeText = string.Empty;
        target.InitCode = string.Empty;
        target.CodeError = null;
    }

    /// <summary>按 <c>nUserData</c> kill（对应快捷键模式下"杀掉第 N 个 sprite"）。</summary>
    public int KillByUserData(int userData)
    {
        var killed = 0;
        for (var i = 0; i < NumSlots; i++)
        {
            if (_slots[i].IsActive && _slots[i].UserData == userData)
            {
                KillTexture(i);
                killed++;
            }
        }

        return killed;
    }

    /// <summary>释放全部纹理（设备 Reset 前调用；表达式 VM 保留）。</summary>
    public void ResetTextures()
    {
        for (var i = 0; i < NumSlots; i++)
        {
            KillTexture(i);
        }

        _textureRefs.Clear();
    }

    /// <summary>
    /// 从 <c>milk2_img.ini</c> 读一个 <c>[imgNN]</c> 段并装载（<c>plugin.cpp:8567-8713</c>）。
    /// </summary>
    /// <param name="iniPath">ini 路径（原版 <c>m_szImgIniFile</c>）。</param>
    /// <param name="spriteNumber">sprite 编号（段名 <c>img00</c>..）。</param>
    /// <param name="slot">-1 = 自动挑槽。</param>
    /// <param name="resourceRoot"><c>img=</c> 是相对路径时的前缀（原版 <c>m_szMilkdrop2Path</c>）。</param>
    /// <param name="time">spawn 时刻。</param>
    /// <param name="frame">spawn 帧号。</param>
    /// <param name="error">失败原因（可直接展示给用户）。</param>
    public bool LaunchFromIni(
        string iniPath,
        int spriteNumber,
        int slot,
        string resourceRoot,
        float time,
        long frame,
        out string? error)
    {
        error = null;

        var section = $"img{spriteNumber:00}";
        var ini = IniSection.Read(iniPath, section);
        if (ini is null)
        {
            error = $"找不到 sprite 定义：{Path.GetFileName(iniPath)} [{section}]";
            return false;
        }

        var image = ini.Get("img") ?? string.Empty;
        if (image.Length == 0)
        {
            error = $"sprite #{spriteNumber}：img 未定义";
            return false;
        }

        // 不是 "x:\..." 形式就按原版拼资源根目录（plugin.cpp:8589-8595）
        if (image.Length < 2 || image[1] != ':')
        {
            image = Path.Combine(resourceRoot, image);
        }

        // 先读 colorkey_lo（向后兼容），再让 colorkey 覆盖它（plugin.cpp:8600-8602）
        var colorKey = IniSection.ParseInt(ini.Get("colorkey_lo"), 0);
        colorKey = IniSection.ParseInt(ini.Get("colorkey"), colorKey);

        var initCode = ReadCode(ini, "init_");
        var frameCode = ReadCode(ini, "code_");

        if (slot < 0)
        {
            slot = FindSlotForNewSprite();
        }

        var result = LoadTexture(image, slot, initCode, frameCode, time, frame, (uint)colorKey & 0xFFFFFFu);
        _slots[slot].UserData = spriteNumber;

        if (!result.Ok)
        {
            error = result.Error switch
            {
                SpriteLoadError.BadIndex => $"sprite #{spriteNumber}：槽号非法",
                SpriteLoadError.OutOfMemory => $"sprite #{spriteNumber}：显存不足",
                _ => $"sprite #{spriteNumber}：图片缺失或无法解码（{image}）",
            };

            return false;
        }

        error = _slots[slot].CodeError;
        return true;
    }

    /// <summary>读 <c>{prefix}1..N</c> 连续代码行并剥注释（<c>plugin.cpp:8605-8638</c>）。</summary>
    private static string ReadCode(IniSection ini, string prefix)
    {
        var raw = EelPreprocessor.JoinContiguousLines(prefix, key => ini.Get(key));
        return EelPreprocessor.StripLinefeedsAndComments(raw);
    }

    private void AddTextureRef(ITexture texture)
        => _textureRefs[texture] = _textureRefs.GetValueOrDefault(texture) + 1;

    private void ReleaseTextureRef(ITexture texture)
    {
        if (!_textureRefs.TryGetValue(texture, out var count))
        {
            (texture as IDisposable)?.Dispose();
            return;
        }

        if (count <= 1)
        {
            _textureRefs.Remove(texture);
            (texture as IDisposable)?.Dispose();
        }
        else
        {
            _textureRefs[texture] = count - 1;
        }
    }
}

/// <summary>
/// 极简 INI 段读取器（对应 <c>GetPrivateProfileStringW</c> 的单段用法）。
/// </summary>
/// <remarks>
/// <c>milk2_img.ini</c> 里有 <c>[img00]..[img15]</c> 十六个同名键的段，
/// 所以不能整文件拍平成一个字典（<c>PresetKeyValueFile</c> 是给单段 <c>.milk</c> 用的）。
/// </remarks>
internal sealed class IniSection
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    private IniSection()
    {
    }

    /// <summary>读取指定段；文件或段不存在返回 null。</summary>
    public static IniSection? Read(string path, string section)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var result = new IniSection();
        var inSection = false;
        var found = false;

        // 原版配置文件是 ANSI；用 Latin1 保证一个字节对应一个字符（图片名多为 ASCII）。
        foreach (var raw in File.ReadLines(path, System.Text.Encoding.Latin1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                var name = close > 1 ? line[1..close].Trim() : string.Empty;
                inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                found |= inSection;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            result._values.TryAdd(key, value);
        }

        return found ? result : null;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    /// <summary>按 <c>0x</c> 十六进制或十进制解析（原版 <c>GetPrivateProfileInt</c> 只吃十进制，这里放宽）。</summary>
    public static int ParseInt(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}
