using MilkDrop.Classic.Render;
using StbImageSharp;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// 预设自己声明的纹理 sampler（<c>sampler_fw_clouds</c> 等）→ <c>textures/</c> 目录里的图片。
/// </summary>
/// <remarks>
/// 对应 <c>CacheParams</c> 里的 <c>TEX_DISK</c> 分支（<c>plugin.cpp:3070-3224</c>）：
/// 按 <c>textures\&lt;根名&gt;.jpg|dds|png|tga|bmp|dib</c> 找文件，找不到再试预设目录。
/// 原版用 <c>D3DXCreateTextureFromFileExW</c> 直接建纹理；这里用 STB 解码后交给后端建纹理。
/// <para>
/// 与 sprite 不同，这里**不做色键**：原版传的 color key 是 0（= 不键），
/// 所以图片自带的 alpha 应当保留。
/// </para>
/// <para>
/// 纹理跨预设共享（原版也有 <c>m_textures</c> 缓存 + 淘汰），这里用简单的 LRU 上限控制显存：
/// 加载失败的根名也会记成 null，避免每帧重试。
/// </para>
/// </remarks>
public sealed class PresetTextureCache : IDisposable
{
    /// <summary>原版 <c>texture_exts</c> 的顺序（<c>plugin.cpp:2847</c>）。</summary>
    private static readonly string[] Extensions = ["jpg", "dds", "png", "tga", "bmp", "dib"];

    /// <summary>同时保留的磁盘纹理上限。</summary>
    private const int MaxCached = 8;

    private readonly IRenderBackend _backend;
    private readonly string? _texturesDir;
    private readonly string? _presetsDir;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _tick;

    private sealed class Entry(ITexture? texture, long tick)
    {
        public ITexture? Texture { get; } = texture;

        public long LastUsed { get; set; } = tick;
    }

    public PresetTextureCache(IRenderBackend backend)
        : this(backend, TryGetDirectory(() => ResourceLocator.TexturesDir), TryGetDirectory(() => ResourceLocator.PresetsDir))
    {
    }

    public PresetTextureCache(IRenderBackend backend, string? texturesDir, string? presetsDir)
    {
        _backend = backend;
        _texturesDir = texturesDir;
        _presetsDir = presetsDir;
    }

    /// <summary>当前缓存的纹理数（含加载失败的占位项）。</summary>
    public int Count => _cache.Count;

    /// <summary>取一张磁盘纹理；找不到文件或解码失败返回 null。</summary>
    public ITexture? Get(string rootName)
    {
        if (string.IsNullOrEmpty(rootName))
        {
            return null;
        }

        if (_cache.TryGetValue(rootName, out var cached))
        {
            cached.LastUsed = ++_tick;
            return cached.Texture;
        }

        var texture = Load(rootName);
        EvictIfNeeded();
        _cache[rootName] = new Entry(texture, ++_tick);
        return texture;
    }

    /// <summary>按原版的扩展名顺序在纹理目录 / 预设目录里找文件。</summary>
    public static string? FindFile(string? texturesDir, string? presetsDir, string rootName)
    {
        if (string.IsNullOrEmpty(rootName))
        {
            return null;
        }

        foreach (var extension in Extensions)
        {
            var name = rootName + "." + extension;

            if (!string.IsNullOrEmpty(texturesDir))
            {
                var candidate = Path.Combine(texturesDir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            if (!string.IsNullOrEmpty(presetsDir))
            {
                var candidate = Path.Combine(presetsDir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
        {
            (entry.Texture as IDisposable)?.Dispose();
        }

        _cache.Clear();
    }

    private ITexture? Load(string rootName)
    {
        var path = FindFile(_texturesDir, _presetsDir, rootName);
        if (path is null)
        {
            return null;
        }

        try
        {
            ImageResult image;
            using (var stream = File.OpenRead(path))
            {
                // dds 不在 STB 支持范围内：下面按扩展名顺序继续找别的格式
                image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            }

            if (image.Width <= 0 || image.Height <= 0)
            {
                return null;
            }

            return _backend.CreateTexture(image.Width, image.Height, ToBgra(image));
        }
        catch (Exception)
        {
            // 解码失败（格式不支持 / 文件损坏）：当作没有这张纹理
            return null;
        }
    }

    private static byte[] ToBgra(ImageResult image)
    {
        var source = image.Data;
        var bgra = new byte[image.Width * image.Height * 4];

        for (var i = 0; i < bgra.Length; i += 4)
        {
            // D3D9 的 A8R8G8B8 在内存里是 BGRA
            bgra[i + 0] = source[i + 2];
            bgra[i + 1] = source[i + 1];
            bgra[i + 2] = source[i + 0];
            bgra[i + 3] = source[i + 3];
        }

        return bgra;
    }

    private void EvictIfNeeded()
    {
        while (_cache.Count >= MaxCached)
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;

            foreach (var (key, entry) in _cache)
            {
                if (entry.Texture is not null && entry.LastUsed < oldest)
                {
                    oldest = entry.LastUsed;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
            {
                return;   // 剩下的都是加载失败的占位项，没有显存可回收
            }

            (_cache[oldestKey].Texture as IDisposable)?.Dispose();
            _cache.Remove(oldestKey);
        }
    }

    private static string? TryGetDirectory(Func<string> resolve)
    {
        try
        {
            var path = resolve();
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
