// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Caching;

public sealed class ReadOnlyOverlayCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
{
    private readonly ICache<TKey, TValue> _baseCache;
    private readonly Dictionary<TKey, TValue> _overlayCache;

    public ReadOnlyOverlayCache(ICache<TKey, TValue> baseCache)
    {
        _baseCache = baseCache ?? throw new ArgumentNullException(nameof(baseCache));
        _overlayCache = new Dictionary<TKey, TValue>();
    }

    public void Clear()
    {
        _overlayCache.Clear();
    }

    public TValue Get(TKey key)
    {
        bool has = _overlayCache.TryGetValue(key, out TValue? value);
        return has ? value! : _baseCache.Get(key)!;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_overlayCache.TryGetValue(key, out TValue? tmpValue))
        {
            value = tmpValue!;
            return true;
        }

        if (_baseCache.TryGet(key, out TValue? tmpValue2))
        {
            value = tmpValue2!;
            return true;
        }

        value = default!;
        return false;
    }


    public bool Set(TKey key, TValue value)
    {
        // Prevent writing to the base cache
        _overlayCache[key] = value;
        return true;
    }

    public bool Delete(TKey key)
    {
        // Prevent deletion in the base cache
        return _overlayCache.Remove(key);
    }

    public bool Contains(TKey key)
    {
        return _overlayCache.ContainsKey(key) || _baseCache.Contains(key);
    }

    public KeyValuePair<TKey, TValue>[] ToArray()
    {
        List<KeyValuePair<TKey, TValue>> overlayArray = new(_overlayCache);
        foreach (KeyValuePair<TKey, TValue> item in _baseCache.ToArray())
        {
            if (!_overlayCache.ContainsKey(item.Key))
            {
                overlayArray.Add(item);
            }
        }
        return overlayArray.ToArray();
    }

}
