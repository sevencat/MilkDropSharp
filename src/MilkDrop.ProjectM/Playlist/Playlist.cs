namespace MilkDrop.ProjectM.Playlist;

/// <summary>播放列表为空时抛出的异常（对应 <c>PlaylistEmptyException</c>）。</summary>
public sealed class PlaylistEmptyException : Exception
{
    public PlaylistEmptyException()
        : base("Playlist is empty")
    {
    }
}

/// <summary>
/// 播放列表：预设文件清单 + 播放位置/历史/shuffle/过滤/排序。
/// </summary>
/// <remarks>对应 <c>playlist/Playlist.hpp</c> 与 <c>Playlist.cpp</c>。</remarks>
public sealed class Playlist
{
    /// <summary><see cref="AddItem"/> 里表示"加到末尾"的索引。</summary>
    public const uint InsertAtEnd = uint.MaxValue;

    /// <summary>历史记录最多保留的条数。</summary>
    public const int MaxHistoryItems = 1000;

    /// <summary>排序依据。</summary>
    public enum SortPredicate
    {
        /// <summary>按完整路径排序。</summary>
        FullPath,

        /// <summary>只按文件名排序。</summary>
        FilenameOnly,
    }

    /// <summary>排序方向。</summary>
    public enum SortOrder
    {
        /// <summary>升序。</summary>
        Ascending,

        /// <summary>降序。</summary>
        Descending,
    }

    private readonly List<Item> _items = [];
    private readonly Filter _filter = new();
    private readonly LinkedList<uint> _presetHistory = new();
    private readonly Random _random = new();

    private bool _shuffle;
    private uint _currentPosition;

    /// <summary>当前列表中的条目数。</summary>
    public uint Size => (uint)_items.Count;

    /// <summary>列表是否为空。</summary>
    public bool Empty => _items.Count == 0;

    /// <summary>当前列表快照（只读）。</summary>
    public IReadOnlyList<Item> Items => _items;

    /// <summary>当前过滤器。</summary>
    public Filter Filter => _filter;

    /// <summary>shuffle 是否开启。</summary>
    public bool Shuffle => _shuffle;

    /// <summary>清空列表与历史。</summary>
    public void Clear()
    {
        _presetHistory.Clear();
        _items.Clear();
    }

    /// <summary>
    /// 添加一个预设文件。
    /// </summary>
    /// <returns>true = 已添加；false = 空文件名、被过滤或重复。</returns>
    public bool AddItem(string filename, uint index, bool allowDuplicates)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return false;
        }

        if (!_filter.Passes(filename))
        {
            return false;
        }

        if (!allowDuplicates && _items.Any(item => item.Equals(filename)))
        {
            return false;
        }

        _presetHistory.Clear();

        if (index >= _items.Count)
        {
            _items.Add(new Item(filename));
        }
        else
        {
            _items.Insert((int)index, new Item(filename));
        }

        return true;
    }

    /// <summary>递归/非递归扫描目录下的 <c>.milk</c> 并加入列表。</summary>
    /// <returns>实际加入的条目数。</returns>
    public uint AddPath(string path, uint index, bool recursive, bool allowDuplicates)
    {
        uint presetsAdded = 0;

        _presetHistory.Clear();

        try
        {
            var options = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            foreach (string file in Directory.EnumerateFiles(path, "*", options))
            {
                if (!file.EndsWith(".milk", StringComparison.Ordinal))
                {
                    continue;
                }

                uint currentIndex = InsertAtEnd;
                if (index < InsertAtEnd)
                {
                    currentIndex = index + presetsAdded;
                }

                if (AddItem(file, currentIndex, allowDuplicates))
                {
                    presetsAdded++;
                }
            }
        }
        catch (Exception)
        {
            // 目录不存在/无权限：返回已经加进去的数量（与 C++ 一致）。
            return presetsAdded;
        }

        return presetsAdded;
    }

    /// <summary>删除指定位置的条目。</summary>
    public bool RemoveItem(uint index)
    {
        if (index >= _items.Count)
        {
            return false;
        }

        _presetHistory.Clear();
        _items.RemoveAt((int)index);

        return true;
    }

    /// <summary>开启/关闭 shuffle。</summary>
    public void SetShuffle(bool enabled) => _shuffle = enabled;

    /// <summary>对 [startIndex, startIndex+count) 区间排序。</summary>
    public void Sort(uint startIndex, uint count, SortPredicate predicate, SortOrder order)
    {
        if (startIndex >= _items.Count)
        {
            return;
        }

        if (startIndex + count >= _items.Count)
        {
            count = (uint)_items.Count - startIndex;
        }

        _presetHistory.Clear();

        var range = _items.GetRange((int)startIndex, (int)count);
        range.Sort((left, right) =>
        {
            string leftName = predicate == SortPredicate.FullPath
                ? left.Filename
                : Path.GetFileName(left.Filename);

            string rightName = predicate == SortPredicate.FullPath
                ? right.Filename
                : Path.GetFileName(right.Filename);

            return order == SortOrder.Ascending
                ? string.CompareOrdinal(leftName, rightName)
                : string.CompareOrdinal(rightName, leftName);
        });

        _items.RemoveRange((int)startIndex, (int)count);
        _items.InsertRange((int)startIndex, range);
    }

    /// <summary>下一个预设索引（shuffle 时随机）。</summary>
    /// <exception cref="PlaylistEmptyException">列表为空。</exception>
    public uint NextPresetIndex()
    {
        ThrowIfEmpty();

        AddCurrentPresetIndexToHistory();

        if (_shuffle)
        {
            _currentPosition = (uint)_random.Next(_items.Count);
        }
        else
        {
            _currentPosition++;
            if (_currentPosition >= _items.Count)
            {
                _currentPosition = 0;
            }
        }

        return _currentPosition;
    }

    /// <summary>上一个预设索引（shuffle 时随机）。</summary>
    /// <exception cref="PlaylistEmptyException">列表为空。</exception>
    public uint PreviousPresetIndex()
    {
        ThrowIfEmpty();

        AddCurrentPresetIndexToHistory();

        if (_shuffle)
        {
            _currentPosition = (uint)_random.Next(_items.Count);
        }
        else if (_currentPosition == 0)
        {
            _currentPosition = (uint)(_items.Count - 1);
        }
        else
        {
            _currentPosition--;
        }

        return _currentPosition;
    }

    /// <summary>播放历史里最后一个索引（没有历史时退化为"上一个"）。</summary>
    /// <exception cref="PlaylistEmptyException">列表为空。</exception>
    public uint LastPresetIndex()
    {
        ThrowIfEmpty();

        if (_presetHistory.Count > 0)
        {
            _currentPosition = _presetHistory.Last!.Value;
            _presetHistory.RemoveLast();
        }
        else
        {
            _currentPosition = PreviousPresetIndex();
            // 把刚压进去的历史项弹掉，避免来回 ping-pong。
            _presetHistory.RemoveLast();
        }

        return _currentPosition;
    }

    /// <summary>当前索引（不改变位置）。</summary>
    /// <exception cref="PlaylistEmptyException">列表为空。</exception>
    public uint PresetIndex()
    {
        ThrowIfEmpty();
        return _currentPosition;
    }

    /// <summary>设置当前索引（越界则回到 0）。</summary>
    /// <exception cref="PlaylistEmptyException">列表为空。</exception>
    public uint SetPresetIndex(uint presetIndex)
    {
        ThrowIfEmpty();

        AddCurrentPresetIndexToHistory();

        if (presetIndex == _currentPosition)
        {
            return _currentPosition;
        }

        _currentPosition = presetIndex;

        if (_currentPosition >= _items.Count)
        {
            _currentPosition = 0;
        }

        return _currentPosition;
    }

    /// <summary>弹出最新一条历史（预设加载失败时用，避免再次被选中）。</summary>
    public void RemoveLastHistoryEntry()
    {
        if (_presetHistory.Count > 0)
        {
            _presetHistory.RemoveLast();
        }
    }

    /// <summary>把当前过滤器应用到已有条目（只删不加）。</summary>
    /// <returns>被移除的条目数。</returns>
    public uint ApplyFilter()
    {
        uint itemsRemoved = 0;

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_filter.Passes(_items[i].Filename))
            {
                continue;
            }

            _items.RemoveAt(i);
            itemsRemoved++;
        }

        if (itemsRemoved != 0)
        {
            _presetHistory.Clear();
        }

        return itemsRemoved;
    }

    private void AddCurrentPresetIndexToHistory()
    {
        // 不重复记录同一位置。
        if (_presetHistory.Count > 0 && _currentPosition == _presetHistory.Last!.Value)
        {
            return;
        }

        _presetHistory.AddLast(_currentPosition);
        if (_presetHistory.Count > MaxHistoryItems)
        {
            _presetHistory.RemoveFirst();
        }
    }

    private void ThrowIfEmpty()
    {
        if (_items.Count == 0)
        {
            throw new PlaylistEmptyException();
        }
    }
}
