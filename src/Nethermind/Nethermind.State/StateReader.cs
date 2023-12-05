// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    public class StateReader : IStateReader
    {
        private readonly IDb _codeDb;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;
        private readonly ITrieStore _trieStore;
        private readonly StateTree _state;

        public StateReader(ITrieStore? trieStore, IDb? codeDb, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<StateReader>() ?? throw new ArgumentNullException(nameof(logManager));
            _logManager = logManager;
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _trieStore = trieStore;
            _state = new StateTree(trieStore.GetTrieStore(null), logManager);
        }

        public Account? GetAccount(Hash256 stateRoot, Address address)
        {
            return GetState(stateRoot, address);
        }

        public byte[] GetStorage(Hash256 stateRoot, Address address, in UInt256 index)
        {
            _state.RootHash = stateRoot;
            Account? account = _state.Get(address);
            if (account == null) return null;

            StorageTree storageTree = new StorageTree(_trieStore.GetTrieStore(address.ToAccountPath), account.StorageRoot, _logManager);
            return storageTree.Get(index);
        }

        public UInt256 GetBalance(Hash256 stateRoot, Address address)
        {
            return GetState(stateRoot, address)?.Balance ?? UInt256.Zero;
        }

        public byte[]? GetCode(Hash256 codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return Array.Empty<byte>();
            }

            return _codeDb[codeHash.Bytes];
        }

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
        {
            _state.Accept(treeVisitor, rootHash, visitingOptions);
        }

        public bool HasStateForRoot(Hash256 stateRoot)
        {
            RootCheckVisitor visitor = new();
            RunTreeVisitor(visitor, stateRoot);
            return visitor.HasRoot;
        }

        public byte[] GetCode(Hash256 stateRoot, Address address)
        {
            Account? account = GetState(stateRoot, address);
            return account is null ? Array.Empty<byte>() : GetCode(account.CodeHash);
        }

        private Account? GetState(Hash256 stateRoot, Address address)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return null;
            }

            Metrics.StateTreeReads++;
            Account? account = _state.Get(address, stateRoot);
            return account;
        }
    }
}
