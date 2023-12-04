// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection.Metadata;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ISmallTrieNodeResolver
    {
        /// <summary>
        /// Returns a cached and resolved <see cref="TrieNode"/> or a <see cref="TrieNode"/> with Unknown type
        /// but the hash set. The latter case allows to resolve the node later. Resolving the node means loading
        /// its RLP data from the state database.
        /// </summary>
        /// <param name="hash">Keccak hash of the RLP of the node.</param>
        /// <returns></returns>
        TrieNode FindCachedOrUnknown(TreePath path, Hash256 hash);

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? LoadRlp(TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);

        ISmallTrieNodeResolver GetStorageTrieNodeResolver(Hash256? address);
    }

    // TODO: scope out storage root
    public interface ITrieNodeResolver
    {
        // Transitionary item
        TrieNode FindCachedOrUnknown(Hash256? address, TreePath path, Hash256 hash);
        byte[]? LoadRlp(Hash256? address, TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
    }
}
