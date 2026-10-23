namespace MilkDrop.ProjectM.Playlist;

/// <summary>播放列表项：一个预设文件路径（对应 <c>playlist/Item.hpp</c> 与 <c>Item.cpp</c>）。</summary>
public sealed class Item(string filename)
{
    /// <summary>完整路径。</summary>
    public string Filename { get; } = filename;

    /// <summary>与给定的文件名比较。</summary>
    public bool Equals(string other) => string.Equals(Filename, other, StringComparison.Ordinal);

    public override string ToString() => Filename;
}
