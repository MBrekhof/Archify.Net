using System.Collections;

namespace Archify.Net;

// ponytail: V8 parity — JS Set: insertion-ordered, delete + re-add appends. Wraps JsMap<T, byte>.
public sealed class JsSet<T> : IEnumerable<T> where T : notnull
{
    readonly JsMap<T, byte> map = new();

    public int Count => map.Count;
    public bool Has(T item) => map.Has(item);
    public void Add(T item) => map.Set(item, 0);
    public bool Delete(T item) => map.Delete(item);
    public IEnumerator<T> GetEnumerator() => map.Keys.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
