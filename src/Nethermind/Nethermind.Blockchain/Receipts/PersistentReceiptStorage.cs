// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
#pragma warning disable 618

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private readonly IColumnsDb<ReceiptsColumns> _database;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private long? _lowestInsertedReceiptBlock;
        private readonly IDbWithSpan _blocksDb;
        private readonly IDb _transactionDb;
        private static readonly Keccak MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        private long _migratedBlockNumber;
        private readonly ReceiptArrayStorageDecoder _storageDecoder = ReceiptArrayStorageDecoder.Instance;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptConfig _receiptConfig;

        private const int CacheSize = 64;
        private readonly LruCache<KeccakKey, TxReceipt[]> _receiptsCache = new(CacheSize, CacheSize, "receipts");
        private readonly ILogger _logger;

        public PersistentReceiptStorage(
            IColumnsDb<ReceiptsColumns> receiptsDb,
            ISpecProvider specProvider,
            IReceiptsRecovery receiptsRecovery,
            IBlockTree blockTree,
            IReceiptConfig receiptConfig,
            ILogManager logManager,
            ReceiptArrayStorageDecoder? storageDecoder = null
        )
        {
            long Get(Keccak key, long defaultValue) => _database.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;

            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _blocksDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _storageDecoder = storageDecoder ?? ReceiptArrayStorageDecoder.Instance;
            _receiptConfig = receiptConfig ?? throw new ArgumentNullException(nameof(receiptConfig));

            byte[] lowestBytes = _database.Get(Keccak.Zero);
            _lowestInsertedReceiptBlock = lowestBytes is null ? (long?)null : new RlpStream(lowestBytes).DecodeLong();
            _migratedBlockNumber = Get(MigrationBlockNumberKey, long.MaxValue);

            _logger = logManager.GetClassLogger();
            _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;
        }

        private void BlockTreeOnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            if (e.PreviousBlock != null)
            {
                RemoveBlockTx(e.PreviousBlock);
            }

            // Dont block main loop
            Task.Run(() =>
            {
                Block newMain = e.Block;

                // Delete old tx index
                if (_receiptConfig.TxLookupLimit > 0 && newMain.Number > _receiptConfig.TxLookupLimit.Value)
                {
                    Block newOldTx = _blockTree.FindBlock(newMain.Number - _receiptConfig.TxLookupLimit.Value);
                    if (newOldTx != null)
                    {
                        ClearTxIndexForBlock(newOldTx);
                    }
                }
            });
        }

        private void ClearTxIndexForBlock(Block block)
        {
            _logger.Warn($"Clear old tx for block {block.Number} {block.Hash} {_receiptConfig.TxLookupLimit}");
            using IBatch batch = _transactionDb.StartBatch();
            foreach (Transaction tx in block.Transactions)
            {
                _logger.Warn($"Clear old tx {tx.Hash.Bytes.ToHexString()}");
                batch[tx.Hash.Bytes] = null;
            }
        }

        private void RemoveBlockTx(Block block)
        {
            _logger.Warn($"Removing tx for block {block.Hash}");
            using IBatch batch = _transactionDb.StartBatch();
            foreach (Transaction tx in block.Transactions)
            {
                _logger.Warn($"Removing tx {tx.Hash.Bytes.ToHexString()}");
                batch[tx.Hash.Bytes] = null;
            }
        }

        public Keccak FindBlockHash(Keccak txHash)
        {
            var blockHashData = _transactionDb.Get(txHash);
            if (blockHashData is null) return FindReceiptObsolete(txHash)?.BlockHash;

            if (blockHashData.Length == Keccak.Size) return new Keccak(blockHashData);

            long blockNum = new RlpStream(blockHashData).DecodeLong();
            return _blockTree.FindBlockHash(blockNum);
        }

        // Find receipt stored with old - obsolete format.
        private TxReceipt FindReceiptObsolete(Keccak hash)
        {
            var receiptData = _database.GetSpan(hash);
            try
            {
                return DeserializeReceiptObsolete(hash, receiptData);
            }
            finally
            {
                _database.DangerousReleaseMemory(receiptData);
            }
        }

        private TxReceipt DeserializeReceiptObsolete(Keccak hash, Span<byte> receiptData)
        {
            if (!receiptData.IsNullOrEmpty())
            {
                return _storageDecoder.DeserializeReceiptObsolete(hash, receiptData);
            }

            return null;
        }

        public TxReceipt[] Get(Block block)
        {
            if (block.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                return Array.Empty<TxReceipt>();
            }

            Keccak blockHash = block.Hash;
            if (_receiptsCache.TryGet(blockHash, out TxReceipt[]? receipts))
            {
                return receipts ?? Array.Empty<TxReceipt>();
            }

            Span<byte> receiptsData = _blocksDb.GetSpan(blockHash);
            try
            {
                if (receiptsData.IsNullOrEmpty())
                {
                    return Array.Empty<TxReceipt>();
                }
                else
                {
                    receipts = _storageDecoder.Decode(in receiptsData);

                    _receiptsRecovery.TryRecover(block, receipts);

                    _receiptsCache.Set(blockHash, receipts);
                    return receipts;
                }
            }
            finally
            {
                _blocksDb.DangerousReleaseMemory(receiptsData);
            }
        }

        public TxReceipt[] Get(Keccak blockHash)
        {
            Block? block = _blockTree.FindBlock(blockHash);
            if (block == null) return Array.Empty<TxReceipt>();
            return Get(block);
        }

        public bool CanGetReceiptsByHash(long blockNumber) => blockNumber >= MigratedBlockNumber;

        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator)
        {
            if (_receiptsCache.TryGet(blockHash, out var receipts))
            {
                iterator = new ReceiptsIterator(receipts);
                return true;
            }

            var result = CanGetReceiptsByHash(blockNumber);
            var receiptsData = _blocksDb.GetSpan(blockHash);


            Func<IReceiptsRecovery.IRecoveryContext?> recoveryContextFactory = () => null;

            if (_storageDecoder.IsCompactEncoding(receiptsData))
            {
                recoveryContextFactory = () =>
                {
                    Block block = _blockTree.FindBlock(blockHash);
                    return _receiptsRecovery.CreateRecoveryContext(block!);
                };
            }

            IReceiptRefDecoder refDecoder = _storageDecoder.GetRefDecoder(receiptsData);

            iterator = result ? new ReceiptsIterator(receiptsData, _blocksDb, recoveryContextFactory, refDecoder) : new ReceiptsIterator();
            return result;
        }

        public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical = true)
        {
            txReceipts ??= Array.Empty<TxReceipt>();
            int txReceiptsLength = txReceipts.Length;

            if (block.Transactions.Length != txReceiptsLength)
            {
                throw new InvalidDataException(
                    $"Block {block.ToString(Block.Format.FullHashAndNumber)} has different numbers " +
                    $"of transactions {block.Transactions.Length} and receipts {txReceipts.Length}.");
            }

            _receiptsRecovery.TryRecover(block, txReceipts, false);

            var blockNumber = block.Number;
            var spec = _specProvider.GetSpec(block.Header);
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;

            using (NettyRlpStream stream = _storageDecoder.EncodeToNewNettyStream(txReceipts, behaviors))
            {
                _blocksDb.Set(block.Hash!, stream.AsSpan());
            }

            if (blockNumber < MigratedBlockNumber)
            {
                MigratedBlockNumber = blockNumber;
            }

            _receiptsCache.Set(block.Hash, txReceipts);

            if (ensureCanonical)
            {
                EnsureCanonical(block);
            }
        }

        public long? LowestInsertedReceiptBlockNumber
        {
            get => _lowestInsertedReceiptBlock;
            set
            {
                _lowestInsertedReceiptBlock = value;
                if (value.HasValue)
                {
                    _database.Set(Keccak.Zero, Rlp.Encode(value.Value).Bytes);
                }
            }
        }

        public long MigratedBlockNumber
        {
            get => _migratedBlockNumber;
            set
            {
                _migratedBlockNumber = value;
                _database.Set(MigrationBlockNumberKey, MigratedBlockNumber.ToBigEndianByteArrayWithoutLeadingZeros());
            }
        }

        internal void ClearCache()
        {
            _receiptsCache.Clear();
        }

        public bool HasBlock(Keccak hash)
        {
            return _receiptsCache.Contains(hash) || _blocksDb.KeyExists(hash);
        }

        public void EnsureCanonical(Block block)
        {
            using IBatch batch = _transactionDb.StartBatch();

            long headNumber = _blockTree.FindBestSuggestedHeader()?.Number ?? 0;

            if (_receiptConfig.TxLookupLimit == -1) return;
            if (_receiptConfig.TxLookupLimit != 0 && block.Number <= headNumber - _receiptConfig.TxLookupLimit) return;
            if (_receiptConfig.CompactTxIndex)
            {
                _logger.Warn($"Ensure canon for block with compact {block}");
                List<String> beforeStr = new List<string>();
                foreach (var tx in block.Transactions)
                {
                    beforeStr.Add($"{tx.Hash.Bytes.ToHexString()}, {tx.SenderAddress}, {tx.ChainId}, {tx.Signature}, {tx.Type}");
                }

                TxReceipt[] receipts = Get(block);
                List<String> afterStr = new List<string>();
                foreach (var tx in block.Transactions)
                {
                    afterStr.Add($"{tx.Hash.Bytes.ToHexString()}, {tx.SenderAddress}, {tx.ChainId}, {tx.Signature}, {tx.Type}");
                }

                foreach (var it in beforeStr.Zip(afterStr).Zip(block.Transactions))
                {
                    if (it.First.First != it.First.Second)
                    {
                        _logger.Error($"Different after is detected! {RuntimeHelpers.GetHashCode(it.Second)}");
                        _logger.Error($"Before {it.First.First}");
                        _logger.Error($"After {it.First.Second}");
                    }
                }

                foreach (var it in block.Transactions.Zip(receipts))
                {
                    if (it.First.Hash != it.Second.TxHash)
                    {
                        _logger.Error($"Different in tx hash {it.First.Hash}, {it.Second.TxHash} {RuntimeHelpers.GetHashCode(it.First)}");
                    }
                }

                foreach (var tx in block.Transactions)
                {
                    batch[tx.Hash.Bytes] = Rlp.Encode(block.Number).Bytes;
                }
            }
            else
            {
                _logger.Warn($"Ensure canon for block {block}");
                foreach (Transaction tx in block.Transactions)
                {
                    _logger.Warn($"Ensure canon tx check -> {tx.Hash.Bytes.ToHexString()}");
                }
                TxReceipt[] receipts = Get(block);
                foreach (var it in block.Transactions.Zip(receipts))
                {
                    var tx = it.First;
                    _logger.Warn($"Ensure canon tx -> {it.First.Hash.Bytes.ToHexString()} -> {it.Second.BlockHash.Bytes.ToHexString()}");
                    batch[tx.Hash.Bytes] = block.Hash.Bytes;
                }
            }
        }
    }
}
