// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ISmallTrieStore : ISmallTrieNodeResolver, IDisposable
    {
        void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None);

        void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

        bool IsPersisted(TreePath path, in ValueHash256 keccak);

        IKeyValueStore AsKeyValueStore();
    }

    public interface ITrieStore : ITrieNodeResolver, IDisposable
    {
        void CommitNode(long blockNumber, Hash256? address, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None);

        void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

        bool IsPersisted(Hash256? address, TreePath path, in ValueHash256 keccak);

        IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        IKeyValueStore AsKeyValueStore(Hash256? address);
        ISmallTrieStore GetTrieStore(Hash256? address);
    }
}
