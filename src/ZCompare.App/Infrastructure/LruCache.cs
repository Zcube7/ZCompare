namespace ZCompare.App.Infrastructure;

internal sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _items = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _usage = [];

    public bool TryGetValue(TKey key, out TValue? value)
    {
        if (!_items.TryGetValue(key, out var node))
        {
            value = default;
            return false;
        }

        _usage.Remove(node);
        _usage.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (_items.TryGetValue(key, out var existing))
        {
            existing.Value = (key, value);
            _usage.Remove(existing);
            _usage.AddFirst(existing);
            return;
        }

        var node = new LinkedListNode<(TKey Key, TValue Value)>((key, value));
        _usage.AddFirst(node);
        _items.Add(key, node);

        if (_items.Count <= capacity)
        {
            return;
        }

        var last = _usage.Last!;
        _usage.RemoveLast();
        _items.Remove(last.Value.Key);
    }

    public void Clear()
    {
        _items.Clear();
        _usage.Clear();
    }
}
