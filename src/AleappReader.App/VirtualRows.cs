using System.Collections;
using System.Collections.Specialized;

namespace AleappReader.App;

/// <summary>
/// A list as long as the whole view that holds only the pages being looked at: the grid's scrollbar spans every row,
/// a row not yet read shows as a placeholder and is swapped in when its page arrives. At most
/// <see cref="MaxPages"/> pages stay in memory; the least recently used one is dropped.
/// </summary>
public sealed class VirtualRows<T> : IList, INotifyCollectionChanged where T : class
{
    public const int PageSize = 100, MaxPages = 3;
    readonly Func<long, int, IReadOnlyList<T>> _fetch; // runs on a worker thread
    readonly Func<T> _placeholder;
    readonly Dictionary<int, T[]> _pages = [];
    readonly LinkedList<int> _lru = new();
    readonly Dictionary<int, T> _waiting = []; // index → placeholder handed out, so Replace names the right old item
    readonly Dictionary<int, Task> _loading = []; // page → its read in flight, so GetAsync can wait for it

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public int Count { get; }

    public VirtualRows(long count, Func<long, int, IReadOnlyList<T>> fetch, Func<T> placeholder)
    {
        Count = (int)Math.Min(count, int.MaxValue);
        _fetch = fetch;
        _placeholder = placeholder;
    }

    public object? this[int index]
    {
        get
        {
            var p = index / PageSize;
            if (_pages.TryGetValue(p, out var page))
            {
                _lru.Remove(p);
                _lru.AddFirst(p);
                if (index % PageSize < page.Length) return page[index % PageSize];
            }
            if (!_waiting.TryGetValue(index, out var ph)) _waiting[index] = ph = _placeholder();
            _ = LoadAsync(p);
            return ph;
        }
        set => throw new NotSupportedException();
    }

    /// <summary>Reads the page holding <paramref name="index"/> now (on a worker thread) so it can be selected.</summary>
    public async Task<T?> GetAsync(int index)
    {
        if (index < 0 || index >= Count) return null;
        await LoadAsync(index / PageSize);
        return _pages.TryGetValue(index / PageSize, out var page) && index % PageSize < page.Length ? page[index % PageSize] : null;
    }

    /// <summary>The rows in memory (for "tag the selection" on rows that are on screen).</summary>
    public IEnumerable<T> Loaded => _pages.Values.SelectMany(p => p);

    Task LoadAsync(int p)
    {
        if (_pages.ContainsKey(p)) return Task.CompletedTask;
        if (!_loading.TryGetValue(p, out var t)) _loading[p] = t = ReadPageAsync(p);
        return t;
    }

    async Task ReadPageAsync(int p)
    {
        try
        {
            var start = (long)p * PageSize;
            var rows = await Task.Run(() => _fetch(start, PageSize));
            _pages[p] = [.. rows];
            _lru.AddFirst(p);
            while (_lru.Count > MaxPages) { _pages.Remove(_lru.Last!.Value); _lru.RemoveLast(); }
            for (int i = 0; i < rows.Count; i++)
                if (_waiting.Remove((int)start + i, out var ph))
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, rows[i], ph, (int)start + i));
        }
        catch (Exception)
        {
            // the view was replaced (new filter or sort) while this page was being read, or the page is unreadable:
            // an empty page keeps its placeholders instead of re-reading on every layout pass
            _pages[p] = [];
        }
        finally { _loading.Remove(p); }
    }

    public int IndexOf(object? value)
    {
        foreach (var (p, page) in _pages)
        {
            var i = Array.IndexOf(page, value);
            if (i >= 0) return p * PageSize + i;
        }
        foreach (var (i, ph) in _waiting) if (ReferenceEquals(ph, value)) return i;
        return -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
}
