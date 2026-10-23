using MilkDrop.ProjectM.Playlist;
using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary>播放列表（P8）：索引推进、历史、shuffle、排序、过滤。</summary>
public sealed class PlaylistTests
{
    [Fact]
    public void AddItemRejectsEmptyAndDuplicates()
    {
        var playlist = new Playlist.Playlist();

        Assert.False(playlist.AddItem(string.Empty, Playlist.Playlist.InsertAtEnd, true));
        Assert.True(playlist.AddItem("/a.milk", Playlist.Playlist.InsertAtEnd, false));
        Assert.False(playlist.AddItem("/a.milk", Playlist.Playlist.InsertAtEnd, false));
        Assert.True(playlist.AddItem("/a.milk", Playlist.Playlist.InsertAtEnd, true));
        Assert.Equal(2u, playlist.Size);
    }

    [Fact]
    public void AddItemInsertsAtRequestedIndex()
    {
        var playlist = new Playlist.Playlist();
        playlist.AddItem("/a.milk", 0, false);
        playlist.AddItem("/b.milk", 0, false);

        Assert.Equal("/b.milk", playlist.Items[0].Filename);
        Assert.Equal("/a.milk", playlist.Items[1].Filename);
    }

    [Fact]
    public void AddPathScansOnlyMilkFiles()
    {
        string directory = Directory.CreateTempSubdirectory("milkdrop-playlist-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(directory, "a.milk"), "[preset00]");
            File.WriteAllText(Path.Combine(directory, "b.txt"), "nope");
            File.WriteAllText(Path.Combine(directory, "c.MILK"), "case matters");

            string sub = Path.Combine(directory, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "d.milk"), "[preset00]");

            var playlist = new Playlist.Playlist();
            uint added = playlist.AddPath(directory, Playlist.Playlist.InsertAtEnd, recursive: false, allowDuplicates: false);

            Assert.Equal(1u, added);

            added = playlist.AddPath(directory, Playlist.Playlist.InsertAtEnd, recursive: true, allowDuplicates: false);
            Assert.Equal(1u, added);
            Assert.Equal(2u, playlist.Size);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NextAndPreviousWrapAround()
    {
        var playlist = CreatePlaylist(3);

        Assert.Equal(0u, playlist.PresetIndex());
        Assert.Equal(1u, playlist.NextPresetIndex());
        Assert.Equal(2u, playlist.NextPresetIndex());
        Assert.Equal(0u, playlist.NextPresetIndex());
        Assert.Equal(2u, playlist.PreviousPresetIndex());
        Assert.Equal(1u, playlist.PreviousPresetIndex());
    }

    [Fact]
    public void LastPresetIndexPopsHistory()
    {
        var playlist = CreatePlaylist(3);

        playlist.NextPresetIndex(); // 0 → 1，历史里有 0
        Assert.Equal(1u, playlist.PresetIndex());
        Assert.Equal(0u, playlist.LastPresetIndex());
        Assert.Equal(0u, playlist.PresetIndex());
    }

    [Fact]
    public void SetPresetIndexWrapsOutOfRangeToZero()
    {
        var playlist = CreatePlaylist(3);

        Assert.Equal(2u, playlist.SetPresetIndex(2));
        Assert.Equal(0u, playlist.SetPresetIndex(99));
        Assert.Equal(2u, playlist.SetPresetIndex(2));
        Assert.Equal(2u, playlist.SetPresetIndex(2));
    }

    [Fact]
    public void EmptyPlaylistThrows()
    {
        var playlist = new Playlist.Playlist();

        Assert.Throws<PlaylistEmptyException>(() => playlist.NextPresetIndex());
        Assert.Throws<PlaylistEmptyException>(() => playlist.PresetIndex());
    }

    [Fact]
    public void SortHandlesFullPathAndFilenameOnly()
    {
        var playlist = new Playlist.Playlist();
        playlist.AddItem("/z/b.milk", Playlist.Playlist.InsertAtEnd, false);
        playlist.AddItem("/a/c.milk", Playlist.Playlist.InsertAtEnd, false);
        playlist.AddItem("/m/a.milk", Playlist.Playlist.InsertAtEnd, false);

        playlist.Sort(0, 3, Playlist.Playlist.SortPredicate.FilenameOnly, Playlist.Playlist.SortOrder.Ascending);
        Assert.Equal(["a.milk", "b.milk", "c.milk"],
            playlist.Items.Select(item => Path.GetFileName(item.Filename)).ToArray());

        playlist.Sort(0, 3, Playlist.Playlist.SortPredicate.FullPath, Playlist.Playlist.SortOrder.Descending);
        Assert.Equal("/z/b.milk", playlist.Items[0].Filename);
    }

    [Fact]
    public void ShufflePicksValidIndexes()
    {
        var playlist = CreatePlaylist(5);
        playlist.SetShuffle(true);

        for (var i = 0; i < 32; i++)
        {
            Assert.InRange(playlist.NextPresetIndex(), 0u, 4u);
        }
    }

    [Fact]
    public void RemoveItemAndApplyFilter()
    {
        var playlist = new Playlist.Playlist();
        playlist.AddItem("/presets/a.milk", Playlist.Playlist.InsertAtEnd, false);
        playlist.AddItem("/presets/b.milk", Playlist.Playlist.InsertAtEnd, false);

        Assert.True(playlist.RemoveItem(0));
        Assert.False(playlist.RemoveItem(5));
        Assert.Equal(1u, playlist.Size);

        playlist.Filter.SetList(["-*b*"]);
        Assert.Equal(1u, playlist.ApplyFilter());
        Assert.True(playlist.Empty);
    }

    [Theory]
    // 无分隔符的表达式只匹配文件名部分。
    [InlineData("+*.milk", "/x/y.milk", true)]
    // 注意：Passes 的默认动作是"命中才生效"，没有规则命中一律保留（C++ 既有语义），
    // 所以 "+*.milk" 不会把 .txt 排除掉。
    [InlineData("+*.milk", "/x/y.txt", true)]
    // '-' 前缀是排除：命中即拒绝。
    [InlineData("-*secret*", "/x/secret.milk", false)]
    [InlineData("-*secret*", "/x/public.milk", true)]
    // "?" 不跨分隔符。
    [InlineData("+.?.milk", "/x/a.milk", true)]
    // "**" 跨目录。
    [InlineData("+/presets/**", "/presets/a/b.milk", true)]
    [InlineData("+/presets/**", "/other/a.milk", true)]
    // "+" 规则的命中/不命中。
    [InlineData("+*b*", "/x/abc.milk", true)]
    [InlineData("+*zzz*", "/x/abc.milk", true)]
    public void FilterGlobMatching(string expression, string filename, bool expected)
    {
        var filter = new Filter();
        filter.SetList([expression]);

        Assert.Equal(expected, filter.Passes(filename));
    }

    [Fact]
    public void EmptyFilterPassesEverything()
    {
        var filter = new Filter();

        Assert.True(filter.Passes("/x/y.milk"));
        Assert.Empty(filter.List);
    }

    private static Playlist.Playlist CreatePlaylist(int count)
    {
        var playlist = new Playlist.Playlist();

        for (var i = 0; i < count; i++)
        {
            playlist.AddItem($"/p{i}.milk", Playlist.Playlist.InsertAtEnd, false);
        }

        return playlist;
    }
}
