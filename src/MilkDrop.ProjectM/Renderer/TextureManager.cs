using System.Reflection;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using StbImageSharp;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 预设用到的所有纹理：预载的噪声/idle 纹理、磁盘上的用户纹理，以及 4 种采样器组合。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/TextureManager.hpp</c> 与 <c>TextureManager.cpp</c>。</para>
/// <para>
/// 纹理查找键是不带 <c>xx_</c> 前缀的小写名；前缀决定 wrap/filter（见
/// <see cref="ExtractTextureSettings"/>）。找不到文件时返回 1×1 占位纹理。
/// </para>
/// <para>
/// 与 C++ 的差异：图片解码用 StbImageSharp 取代 SOIL2（支持的扩展名一致，
/// 但 <c>.dds</c> 的具体压缩格式覆盖率以 stb_image 为准）。
/// </para>
/// </remarks>
public sealed class TextureManager : IDisposable
{
    private const string IdleTextureResourcePrefix = "MilkDrop.ProjectM.IdleTextures.";

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".dds", ".png", ".tga", ".bmp", ".dib"];

    private readonly List<string> _textureSearchPaths;
    private readonly List<ScannedFile> _scannedTextureFiles = [];
    private readonly Dictionary<string, Texture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<(int WrapMode, int FilterMode), Sampler> _samplers = [];
    private readonly Dictionary<string, UsageStats> _textureStats = new(StringComparer.Ordinal);
    private readonly Texture _placeholderTexture;

    private bool _filesScanned;

    /// <param name="textureSearchPaths">纹理搜索目录（按顺序查找）。</param>
    public TextureManager(IEnumerable<string> textureSearchPaths)
    {
        ArgumentNullException.ThrowIfNull(textureSearchPaths);

        _textureSearchPaths = [.. textureSearchPaths];
        _placeholderTexture = new Texture("placeholder", 1, 1, false);

        Preload();
    }

    /// <summary>当前预设所在目录（C++ 里是个空实现，这里只记录下来备查）。</summary>
    public string CurrentPresetPath { get; private set; } = string.Empty;

    /// <summary>设置当前预设目录（对应 <c>SetCurrentPresetPath</c>，4.1.8 里不影响搜索路径）。</summary>
    public void SetCurrentPresetPath(string path) => CurrentPresetPath = path;

    /// <summary>
    /// 按预设里的名字取纹理（对应 <c>GetTexture</c>）：先查缓存，未命中再扫盘加载。
    /// </summary>
    public TextureSamplerDescriptor GetTexture(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);

        ExtractTextureSettings(fullName, out int wrapMode, out int filterMode, out string unqualifiedName);

        if (!_textures.TryGetValue(unqualifiedName, out Texture? texture))
        {
            return TryLoadingTexture(fullName);
        }

        return new TextureSamplerDescriptor(texture, _samplers[(wrapMode, filterMode)], fullName, unqualifiedName);
    }

    /// <summary>只按前缀返回采样器，不加载纹理（对应 <c>GetSampler</c>）。</summary>
    public Sampler GetSampler(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);

        ExtractTextureSettings(fullName, out int wrapMode, out int filterMode, out _);
        return _samplers[(wrapMode, filterMode)];
    }

    /// <summary>
    /// 随机挑一张纹理（对应 <c>GetRandomTexture</c>）：<c>rand00_</c> 后面的部分作为文件名前缀过滤。
    /// 没有匹配时返回空描述符。
    /// </summary>
    public TextureSamplerDescriptor GetRandomTexture(string randomName)
    {
        ArgumentNullException.ThrowIfNull(randomName);

        ScanTextures();

        string lowerCaseName = Utils.ToLower(randomName);
        if (_scannedTextureFiles.Count == 0)
        {
            return new TextureSamplerDescriptor();
        }

        string prefix = lowerCaseName.Length > 7 && lowerCaseName[6] == '_'
            ? lowerCaseName[7..]
            : string.Empty;

        string selectedFilename;

        if (prefix.Length == 0)
        {
            selectedFilename = _scannedTextureFiles[Random.Shared.Next(_scannedTextureFiles.Count)].LowerCaseBaseName;
        }
        else
        {
            List<ScannedFile> filtered = _scannedTextureFiles
                .Where(file => file.LowerCaseBaseName.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (filtered.Count == 0)
            {
                return new TextureSamplerDescriptor();
            }

            selectedFilename = filtered[Random.Shared.Next(filtered.Count)].LowerCaseBaseName;
        }

        TextureSamplerDescriptor descriptor = GetTexture(selectedFilename);

        // 描述符用回 `rand00[_prefix]` 原名，方便着色器里对号入座。
        return new TextureSamplerDescriptor(descriptor.Texture, descriptor.Sampler, randomName, randomName);
    }

    /// <summary>
    /// 每次载入预设时调用一次：所有用户纹理年龄 +1，清空扫描缓存，淘汰一张最老最大的纹理
    /// （对应 <c>PurgeTextures</c>）。
    /// </summary>
    public void PurgeTextures()
    {
        foreach ((string name, Texture texture) in _textures)
        {
            if (texture.IsUserTexture && _textureStats.TryGetValue(name, out UsageStats? stats))
            {
                stats.Age++;
            }
        }

        _scannedTextureFiles.Clear();
        _filesScanned = false;

        // 只淘汰年龄 ≥2 的，避免把过渡中还在用的预设纹理踢掉。
        uint newest = 99_999_999;
        uint oldest = 0;
        bool foundTextureToEvict = false;

        foreach (UsageStats stats in _textureStats.Values)
        {
            if (stats.SizeBytes > 0 && stats.Age > 1)
            {
                newest = Math.Min(newest, stats.Age);
                oldest = Math.Max(oldest, stats.Age);
                foundTextureToEvict = true;
            }
        }

        if (!foundTextureToEvict)
        {
            return;
        }

        uint biggestBytes = 0;
        string biggestName = string.Empty;

        foreach ((string name, UsageStats stats) in _textureStats)
        {
            if (stats.SizeBytes == 0 || stats.Age <= 1)
            {
                continue;
            }

            float sizeMultiplicator = 1.0f + (float)(stats.Age - newest) / (oldest - newest);
            uint scaledSize = (uint)(stats.SizeBytes * sizeMultiplicator);

            if (scaledSize > biggestBytes)
            {
                biggestBytes = scaledSize;
                biggestName = name;
            }
        }

        if (biggestName.Length == 0)
        {
            return;
        }

        _textures.Remove(biggestName);
        _textureStats.Remove(biggestName);
    }

    /// <summary>
    /// 从名字前缀解析 wrap/filter 与不带前缀的纹理名（对应 <c>ExtractTextureSettings</c>）。
    /// </summary>
    /// <remarks>缺省是 <c>fw</c>：双线性 + repeat；不认识的前缀也按 <c>fw</c> 处理并去掉前 3 个字符。</remarks>
    internal static void ExtractTextureSettings(string qualifiedName, out int wrapMode, out int filterMode, out string name)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);

        if (qualifiedName.Length <= 3 || qualifiedName[2] != '_')
        {
            name = qualifiedName;
            filterMode = (int)GLEnum.Linear;
            wrapMode = (int)GLEnum.Repeat;
            return;
        }

        string lowerQualifiedName = Utils.ToLower(qualifiedName);
        string prefix = lowerQualifiedName[..3];

        name = qualifiedName[3..];

        switch (prefix)
        {
            case "fc_":
            case "cf_":
                filterMode = (int)GLEnum.Linear;
                wrapMode = (int)GLEnum.ClampToEdge;
                break;

            case "fw_":
            case "wf_":
                filterMode = (int)GLEnum.Linear;
                wrapMode = (int)GLEnum.Repeat;
                break;

            case "pc_":
            case "cp_":
                filterMode = (int)GLEnum.Nearest;
                wrapMode = (int)GLEnum.ClampToEdge;
                break;

            case "pw_":
            case "wp_":
                filterMode = (int)GLEnum.Nearest;
                wrapMode = (int)GLEnum.Repeat;
                break;

            default:
                // Milkdrop 对不认识的前缀同样去掉前 3 个字符。
                filterMode = (int)GLEnum.Linear;
                wrapMode = (int)GLEnum.Repeat;
                break;
        }
    }

    /// <summary>释放管理器持有的所有 GL 纹理与采样器（C++ 侧靠 shared_ptr 引用计数自动回收）。</summary>
    public void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            foreach (Texture texture in _textures.Values)
            {
                texture.Dispose();
            }

            foreach (Sampler sampler in _samplers.Values)
            {
                sampler.Dispose();
            }

            _placeholderTexture.Dispose();
        }

        _textures.Clear();
        _samplers.Clear();
        _textureStats.Clear();
        _scannedTextureFiles.Clear();
    }

    private void Preload()
    {
        // 4 种 wrap × filter 组合。
        AddSampler(GLEnum.ClampToEdge, GLEnum.Linear);
        AddSampler(GLEnum.ClampToEdge, GLEnum.Nearest);
        AddSampler(GLEnum.Repeat, GLEnum.Linear);
        AddSampler(GLEnum.Repeat, GLEnum.Nearest);

        // idle 纹理（原 C++ 由 SOIL 从内存里的 32 位 TGA 解码）。
        _textures["idlem"] = LoadEmbeddedTexture("idlem", "idlem.tga");
        _textures["idleheadphones"] = LoadEmbeddedTexture("idleheadphones", "idleheadphones.tga");

        // 噪声纹理。
        _textures["noise_lq_lite"] = MilkdropNoise.LowQualityLite();
        _textures["noise_lq"] = MilkdropNoise.LowQuality();
        _textures["noise_mq"] = MilkdropNoise.MediumQuality();
        _textures["noise_hq"] = MilkdropNoise.HighQuality();
        _textures["noisevol_lq"] = MilkdropNoise.LowQualityVolume();
        _textures["noisevol_hq"] = MilkdropNoise.HighQualityVolume();
    }

    private void AddSampler(GLEnum wrapMode, GLEnum filterMode)
    {
        _samplers[((int)wrapMode, (int)filterMode)] = new Sampler((int)wrapMode, (int)filterMode);
    }

    private TextureSamplerDescriptor TryLoadingTexture(string name)
    {
        ExtractTextureSettings(name, out int wrapMode, out int filterMode, out string unqualifiedName);

        ScanTextures();

        string lowerCaseUnqualifiedName = Utils.ToLower(unqualifiedName);

        foreach (ScannedFile file in _scannedTextureFiles)
        {
            if (file.LowerCaseBaseName != lowerCaseUnqualifiedName)
            {
                continue;
            }

            Texture? texture = LoadTexture(file);
            if (texture is not null)
            {
                return new TextureSamplerDescriptor(texture, _samplers[(wrapMode, filterMode)], name, unqualifiedName);
            }
        }

        return new TextureSamplerDescriptor(_placeholderTexture, _samplers[(wrapMode, filterMode)], name, unqualifiedName);
    }

    private Texture? LoadTexture(ScannedFile file)
    {
        if (_textures.TryGetValue(file.LowerCaseBaseName, out Texture? cached))
        {
            return cached;
        }

        ColorComponents componentCount = ColorComponents.RedGreenBlueAlpha;
        ImageResult image;

        try
        {
            using FileStream stream = File.OpenRead(file.FilePath);
            image = ImageResult.FromStream(stream, componentCount);
        }
        catch (Exception)
        {
            // 打不开、格式不支持、数据损坏都当作"加载失败"，交给调用方用占位纹理。
            return null;
        }

        if (image.Data.Length == 0 || image.Width <= 0 || image.Height <= 0)
        {
            return null;
        }

        Texture texture = CreateTexture(file.LowerCaseBaseName, image.Width, image.Height, image.Data, isUserTexture: true);
        _textures[file.LowerCaseBaseName] = texture;
        _textureStats[file.LowerCaseBaseName] = new UsageStats
        {
            SizeBytes = (uint)(image.Width * image.Height * 4),
        };

        return texture;
    }

    private void ScanTextures()
    {
        if (_filesScanned)
        {
            return;
        }

        var fileScanner = new FileScanner(_textureSearchPaths.ToArray(), Extensions);
        fileScanner.Scan(AddTextureFile);
        _filesScanned = true;
    }

    private void AddTextureFile(string fileName, string baseName)
    {
        _scannedTextureFiles.Add(new ScannedFile
        {
            FilePath = fileName,
            LowerCaseBaseName = Utils.ToLower(baseName),
        });
    }

    private static unsafe Texture CreateTexture(string name, int width, int height, byte[] rgba, bool isUserTexture)
    {
        var gl = GlContext.Gl;

        uint textureId = gl.GenTexture();
        gl.BindTexture(GLEnum.Texture2D, textureId);

        fixed (byte* pointer = rgba)
        {
            gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, (uint)width, (uint)height, 0,
                GLEnum.Rgba, GLEnum.UnsignedByte, pointer);
        }

        gl.BindTexture(GLEnum.Texture2D, 0);

        return new Texture(name, textureId, GLEnum.Texture2D, width, height, isUserTexture);
    }

    private static Texture LoadEmbeddedTexture(string name, string fileName)
    {
        Assembly assembly = typeof(TextureManager).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(IdleTextureResourcePrefix + fileName)
                              ?? throw new InvalidOperationException("找不到内嵌纹理资源: " + fileName);

        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return CreateTexture(name, image.Width, image.Height, image.Data, isUserTexture: false);
    }

    private sealed class ScannedFile
    {
        public string FilePath = string.Empty;

        public string LowerCaseBaseName = string.Empty;
    }

    private sealed class UsageStats
    {
        public uint Age;

        public uint SizeBytes;
    }
}
