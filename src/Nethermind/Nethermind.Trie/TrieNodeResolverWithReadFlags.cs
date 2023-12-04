// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieNodeResolverWithReadFlags : ISmallTrieNodeResolver
{
    private readonly ISmallTrieStore _baseResolver;
    private readonly ReadFlags _defaultFlags;

    public TrieNodeResolverWithReadFlags(ISmallTrieStore baseResolver, ReadFlags defaultFlags)
    {
        _baseResolver = baseResolver;
        _defaultFlags = defaultFlags;
    }

    public TrieNode FindCachedOrUnknown(TreePath treePath, Hash256 hash)
    {
        return _baseResolver.FindCachedOrUnknown(treePath, hash);
    }

    public byte[]? LoadRlp(TreePath treePath, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (flags != ReadFlags.None)
        {
            return _baseResolver.LoadRlp(treePath, hash, flags | _defaultFlags);
        }

        return _baseResolver.LoadRlp(treePath, hash, _defaultFlags);
    }

    public ISmallTrieNodeResolver GetStorageTrieNodeResolver(Hash256 address)
    {
        return _baseResolver.GetStorageTrieNodeResolver(address);
    }
}
