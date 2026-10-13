namespace Archify.Net;

// ponytail: V8 parity — JS Map: insertion-ordered; Set on an existing key updates in place, delete + re-add appends.
// Keys use EqualityComparer<TKey>.Default: key by string or by reference object, never by value-tuple/record.
public sealed class JsMap<TKey, TValue> where TKey : notnull
{
    readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> index = [];
    readonly LinkedList<KeyValuePair<TKey, TValue>> order = new();

    public int Count => index.Count;
    public bool Has(TKey key) => index.ContainsKey(key);
    public TValue? Get(TKey key) => index.TryGetValue(key, out var node) ? node.Value.Value : default;
    public void Set(TKey key, TValue value)
    {
        if (index.TryGetValue(key, out var node)) node.Value = new(key, value);
        else index[key] = order.AddLast(new KeyValuePair<TKey, TValue>(key, value));
    }
    public bool Delete(TKey key)
    {
        if (!index.Remove(key, out var node)) return false;
        order.Remove(node);
        return true;
    }
    public IEnumerable<TKey> Keys => order.Select(e => e.Key);
    public IEnumerable<TValue> Values => order.Select(e => e.Value);
    public IEnumerable<KeyValuePair<TKey, TValue>> Entries => order;
}
