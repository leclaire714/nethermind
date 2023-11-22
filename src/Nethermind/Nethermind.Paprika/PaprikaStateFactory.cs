// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Paprika.Chain;
using Paprika.Merkle;
using Paprika.Store;
using IWorldState = Paprika.Chain.IWorldState;
using PaprikaKeccak = Paprika.Crypto.Keccak;
using PaprikaAccount = Paprika.Account;

namespace Nethermind.Paprika;

public class PaprikaStateFactory : IStateFactory
{
    private static readonly ulong _sepolia = (ulong)32.GiB();
    private static readonly TimeSpan _flushFileEvery = TimeSpan.FromSeconds(10);

    private readonly PagedDb _db;
    private readonly Blockchain _blockchain;

    private long _lastFinalized;

    public PaprikaStateFactory(string directory)
    {
        _db = PagedDb.MemoryMappedDb(_sepolia, 64, directory, true);
        _blockchain = new Blockchain(_db, new ComputeMerkleBehavior(true, 2, 2), _flushFileEvery);
        _blockchain.Flushed += (_, blockNumber) =>
            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(blockNumber));
    }

    public IState Get(Keccak stateRoot) => new State(_blockchain.StartNew(Convert(stateRoot)));

    public IReadOnlyState GetReadOnly(Keccak stateRoot) =>
        new ReadOnlyState(_blockchain.StartReadOnly(Convert(stateRoot)));

    public bool HasRoot(Keccak stateRoot) => _blockchain.HasState(Convert(stateRoot));

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public async ValueTask DisposeAsync()
    {
        await _blockchain.DisposeAsync();
        _db.Dispose();
    }

    private static PaprikaKeccak Convert(Keccak keccak) => new(keccak.Bytes);
    private static PaprikaKeccak Convert(in ValueKeccak keccak) => new(keccak.Bytes);
    private static Keccak Convert(PaprikaKeccak keccak) => new(keccak.BytesAsSpan);
    private static PaprikaKeccak Convert(Address address) => Convert(ValueKeccak.Compute(address.Bytes));

    // shamelessly stolen from storage trees
    private const int CacheSize = 1024;
    private static readonly byte[][] _cache = new byte[CacheSize][];

    private static void GetKey(in UInt256 index, in Span<byte> key)
    {
        if (index < CacheSize)
        {
            _cache[(int)index].CopyTo(key);
            return;
        }

        index.ToBigEndian(key);

        // in situ calculation
        KeccakHash.ComputeHashBytesToSpan(key, key);
    }

    static PaprikaStateFactory()
    {
        Span<byte> buffer = stackalloc byte[32];
        for (int i = 0; i < CacheSize; i++)
        {
            UInt256 index = (UInt256)i;
            index.ToBigEndian(buffer);
            _cache[i] = Keccak.Compute(buffer).BytesToArray();
        }
    }

    public void Finalize(Keccak finalizedStateRoot, long finalizedNumber)
    {
        _blockchain.Finalize(Convert(finalizedStateRoot));
        Volatile.Write(ref _lastFinalized, Math.Max(finalizedNumber, Volatile.Read(ref _lastFinalized)));
    }

    class ReadOnlyState : IReadOnlyState
    {
        private readonly IReadOnlyWorldState _wrapped;

        public ReadOnlyState(IReadOnlyWorldState wrapped)
        {
            _wrapped = wrapped;
        }

        public Account? Get(Address address)
        {
            PaprikaAccount account = _wrapped.GetAccount(Convert(address));
            bool hasEmptyStorageAndCode = account.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          account.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (account.Balance.IsZero &&
                account.Nonce.IsZero &&
                hasEmptyStorageAndCode)
                return null;

            if (hasEmptyStorageAndCode)
                return new Account(account.Nonce, account.Balance);

            return new Account(account.Nonce, account.Balance, Convert(account.StorageRootHash),
                Convert(account.CodeHash));
        }

        public byte[] GetStorageAt(in StorageCell cell)
        {
            // bytes are used for two purposes, first for the key encoding and second, for the result handling
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            Span<byte> value = _wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return value.IsEmpty ? new byte[] { 0 } : value.ToArray();
        }

        public Keccak StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }

    class State : IState
    {
        private readonly IWorldState _wrapped;

        public State(IWorldState wrapped)
        {
            _wrapped = wrapped;
        }

        public void Set(Address address, Account? account)
        {
            PaprikaKeccak key = Convert(address);

            if (account == null)
            {
                _wrapped.DestroyAccount(key);
            }
            else
            {
                PaprikaAccount actual = new(account.Balance, account.Nonce, Convert(account.CodeHash),
                    Convert(account.StorageRoot));
                _wrapped.SetAccount(key, actual);
            }
        }

        public Account? Get(Address address)
        {
            PaprikaAccount account = _wrapped.GetAccount(Convert(address));
            bool hasEmptyStorageAndCode = account.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          account.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (account.Balance.IsZero &&
                account.Nonce.IsZero &&
                hasEmptyStorageAndCode)
                return null;

            if (hasEmptyStorageAndCode)
                return new Account(account.Nonce, account.Balance);

            return new Account(account.Nonce, account.Balance, Convert(account.StorageRootHash),
                Convert(account.CodeHash));
        }

        public byte[] GetStorageAt(in StorageCell cell)
        {
            // bytes are used for two purposes, first for the key encoding and second, for the result handling
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            Span<byte> value = _wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return value.IsEmpty ? new byte[] { 0 } : value.ToArray();
        }

        public void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(cell.Index, key);
            _wrapped.SetStorage(Convert(cell.Address), new PaprikaKeccak(key),
                value.IsZero() ? ReadOnlySpan<byte>.Empty : value);
        }

        public void Commit(long blockNumber) => _wrapped.Commit((uint)blockNumber);

        public void Reset() => _wrapped.Reset();

        public Keccak StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }
}
