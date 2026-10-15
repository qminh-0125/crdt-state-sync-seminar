using System;
using System.Collections.Generic;
using System.Linq;

namespace StateSync.Core;

/// <summary>
/// Cài đặt cấu trúc Observed-Remove Set (OR-Set / Add-Wins Set) theo định nghĩa
/// tại Mục 3.3.5 (Specification 15) và Mục 5.1 (OR-Cart) của Marc Shapiro et al. (INRIA RR-7506).
/// Mỗi lần Add(e) sinh ra một thẻ định danh duy nhất (unique tag α).
/// Khi Remove(e), chỉ những tag α nào đã được quan sát tại source replica mới bị đưa vào tập Tombstone.
/// Nếu Add(e) và Remove(e) xảy ra đồng thời (Concurrent), phép Add sẽ thắng (Add-Wins semantics).
/// </summary>
public class ORSet<T> where T : notnull
{
    // Lưu ánh xạ: Phần tử e -> Tập các unique tags đã được thêm
    private readonly Dictionary<T, HashSet<string>> _elements = new();

    // Tập các unique tags đã bị xóa (Tombstone tags)
    private readonly HashSet<string> _removedTags = new();

    private readonly object _lock = new();

    /// <summary>
    /// Pha Prepare + Effect của phép Add(e): gắn một uniqueTag mới cho phần tử e.
    /// </summary>
    public string Add(T element, string? uniqueTag = null)
    {
        lock (_lock)
        {
            string tag = uniqueTag ?? Guid.NewGuid().ToString("N");
            if (_removedTags.Contains(tag))
                return tag;

            if (!_elements.TryGetValue(element, out var tags))
            {
                tags = new HashSet<string>();
                _elements[element] = tags;
            }
            tags.Add(tag);
            return tag;
        }
    }

    /// <summary>
    /// Xóa toàn bộ các unique tags hiện đang quan sát được của phần tử e tại replica này.
    /// </summary>
    public HashSet<string> Remove(T element)
    {
        lock (_lock)
        {
            var observed = new HashSet<string>();
            if (_elements.TryGetValue(element, out var tags))
            {
                foreach (var t in tags)
                {
                    observed.Add(t);
                    _removedTags.Add(t);
                }
                tags.Clear();
                _elements.Remove(element);
            }
            return observed;
        }
    }

    /// <summary>
    /// Áp dụng tập các tag bị xóa từ một replica khác truyền tới (Downstream Remove Effect).
    /// </summary>
    public void ApplyRemoveTags(T element, IEnumerable<string> tagsToRemove)
    {
        lock (_lock)
        {
            foreach (var tag in tagsToRemove)
            {
                _removedTags.Add(tag);
                if (_elements.TryGetValue(element, out var activeTags))
                {
                    activeTags.Remove(tag);
                    if (activeTags.Count == 0)
                    {
                        _elements.Remove(element);
                    }
                }
            }
        }
    }

    public bool Lookup(T element)
    {
        lock (_lock)
        {
            return _elements.TryGetValue(element, out var tags) && tags.Count > 0;
        }
    }

    public HashSet<T> GetActiveElements()
    {
        lock (_lock)
        {
            return _elements.Where(kvp => kvp.Value.Count > 0).Select(kvp => kvp.Key).ToHashSet();
        }
    }

    public void Merge(ORSet<T> other)
    {
        lock (_lock)
        {
            lock (other._lock)
            {
                foreach (var remTag in other._removedTags)
                {
                    _removedTags.Add(remTag);
                }

                foreach (var (elem, otherTags) in other._elements)
                {
                    foreach (var tag in otherTags)
                    {
                        if (!_removedTags.Contains(tag))
                        {
                            if (!_elements.TryGetValue(elem, out var localTags))
                            {
                                localTags = new HashSet<string>();
                                _elements[elem] = localTags;
                            }
                            localTags.Add(tag);
                        }
                    }
                }

                // Loại bỏ các tag đã nằm trong tập _removedTags
                var emptyKeys = new List<T>();
                foreach (var (elem, localTags) in _elements)
                {
                    localTags.RemoveWhere(t => _removedTags.Contains(t));
                    if (localTags.Count == 0) emptyKeys.Add(elem);
                }
                foreach (var k in emptyKeys) _elements.Remove(k);
            }
        }
    }
}
