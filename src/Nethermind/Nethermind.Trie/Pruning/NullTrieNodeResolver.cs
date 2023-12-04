// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieNodeResolver : ISmallTrieNodeResolver
    {
        private NullTrieNodeResolver() { }

        public static readonly NullTrieNodeResolver Instance = new();

        public TrieNode FindCachedOrUnknown(TreePath path, Hash256 hash) => new(NodeType.Unknown, path, hash);
        public byte[]? LoadRlp(TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => null;
        public ISmallTrieNodeResolver GetStorageTrieNodeResolver(Hash256 storage)
        {
            return this;
        }
    }

    public class NullFullTrieNodeResolver : ITrieNodeResolver
    {
        private NullFullTrieNodeResolver() { }

        public static readonly NullFullTrieNodeResolver Instance = new();

        public TrieNode FindCachedOrUnknown(Hash256? address, TreePath path, Hash256 hash) => new(NodeType.Unknown, path, hash);
        public byte[]? LoadRlp(Hash256? address, TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => null;
        public ISmallTrieNodeResolver GetStorageTrieNodeResolver(Hash256 storage)
        {
            return NullTrieNodeResolver.Instance;
        }
    }
}
