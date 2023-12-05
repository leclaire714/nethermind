// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
    /// </summary>
    public class TrieStore : ITrieStore
    {
        public record PruneKey(Hash256? Address, TreePath Path, ValueHash256 Keccak)
        {
        }

        private class DirtyNodesCache
        {
            private readonly TrieStore _trieStore;

            public DirtyNodesCache(TrieStore trieStore)
            {
                _trieStore = trieStore;
            }

            public void SaveInCache(Hash256? address, TreePath path, TrieNode node)
            {
                Debug.Assert(node.Keccak is not null, "Cannot store in cache nodes without resolved key.");
                if (_objectsCache.TryAdd(new PruneKey(address, path, node.Keccak), (address, path, node)))
                {
                    Metrics.CachedNodesCount = Interlocked.Increment(ref _count);
                    _trieStore.MemoryUsedByDirtyCache += node.GetMemorySize(false);
                }
            }

            public TrieNode FindCachedOrUnknown(Hash256? address, TreePath path, Hash256 hash)
            {
                TrieNode trieNode;
                if (_objectsCache.TryGetValue(new PruneKey(address, path, hash), out (Hash256, TreePath, TrieNode) item))
                {
                    trieNode = item.Item3;
                    Metrics.LoadedFromCacheNodesCount++;
                }
                else
                {
                    trieNode = new TrieNode(NodeType.Unknown, hash);
                    if (_trieStore._logger.IsTrace) _trieStore._logger.Trace($"Creating new node {trieNode}");
                    SaveInCache(address, path, trieNode);
                }

                return trieNode;
            }

            public TrieNode FromCachedRlpOrUnknown(Hash256? address, TreePath path, Hash256 hash)
            {
                // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                TrieNode trieNode;
                if (_objectsCache.TryGetValue(new PruneKey(address, path, hash), out (Hash256?, TreePath, TrieNode) item))
                {
                    trieNode = item.Item3;
                    if (trieNode!.FullRlp.IsNull)
                    {
                        // // this happens in SyncProgressResolver
                        // throw new InvalidAsynchronousStateException("Read only trie store is trying to read a transient node.");
                        return new TrieNode(NodeType.Unknown, hash);
                    }

                    // we returning a copy to avoid multithreaded access
                    trieNode = new TrieNode(NodeType.Unknown, hash, trieNode.FullRlp);
                    trieNode.ResolveNode(_trieStore.GetTrieStore(address), path);
                    trieNode.Keccak = hash;

                    Metrics.LoadedFromCacheNodesCount++;
                }
                else
                {
                    trieNode = new TrieNode(NodeType.Unknown, hash);
                }

                if (_trieStore._logger.IsTrace) _trieStore._logger.Trace($"Creating new node {trieNode}");
                return trieNode;
            }

            public bool IsNodeCached(PruneKey key) => _objectsCache.ContainsKey(key);

            public ConcurrentDictionary<PruneKey, (Hash256?, TreePath, TrieNode)> AllNodes => _objectsCache;

            private readonly ConcurrentDictionary<PruneKey, (Hash256?, TreePath, TrieNode)> _objectsCache = new();

            private int _count = 0;

            public int Count => _count;

            public void Remove(PruneKey key)
            {
                if (_objectsCache.Remove(key, out _))
                {
                    Metrics.CachedNodesCount = Interlocked.Decrement(ref _count);
                }
            }

            public void Dump()
            {
                if (_trieStore._logger.IsTrace)
                {
                    _trieStore._logger.Trace($"Trie node dirty cache ({Count})");
                    foreach (KeyValuePair<PruneKey, (Hash256?, TreePath, TrieNode)> keyValuePair in _objectsCache)
                    {
                        _trieStore._logger.Trace($"  {keyValuePair.Value}");
                    }
                }
            }

            public void Clear()
            {
                _objectsCache.Clear();
                Interlocked.Exchange(ref _count, 0);
                Metrics.CachedNodesCount = 0;
                _trieStore.MemoryUsedByDirtyCache = 0;
            }
        }

        private int _isFirst;

        private IWriteBatch? _currentBatch = null;

        private readonly DirtyNodesCache _dirtyNodes;

        private RootTrieStoreProxy _rootStore;
        public ISmallTrieStore RootStore => _rootStore;

        private bool _lastPersistedReachedReorgBoundary;
        private Task _pruningTask = Task.CompletedTask;
        private CancellationTokenSource _pruningTaskCancellationTokenSource = new();

        public TrieStore(IKeyValueStoreWithBatching? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Pruning.Persist.EveryBlock, logManager)
        {
        }

        public TrieStore(
            IKeyValueStoreWithBatching? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _dirtyNodes = new DirtyNodesCache(this);
            _rootStore = new RootTrieStoreProxy(this);
        }

        public ISmallTrieStore GetTrieStore(Hash256? address)
        {
            if (address == null) return RootStore;
            return _rootStore.GetStorageTrieStore(address);
        }

        public long LastPersistedBlockNumber
        {
            get => _latestPersistedBlockNumber;
            private set
            {
                if (value != _latestPersistedBlockNumber)
                {
                    Metrics.LastPersistedBlockNumber = value;
                    _latestPersistedBlockNumber = value;
                    _lastPersistedReachedReorgBoundary = false;
                }
            }
        }

        public long MemoryUsedByDirtyCache
        {
            get => _memoryUsedByDirtyCache;
            private set
            {
                Metrics.MemoryUsedByCache = value;
                _memoryUsedByDirtyCache = value;
            }
        }

        public int CommittedNodesCount
        {
            get => _committedNodesCount;
            private set
            {
                Metrics.CommittedNodesCount = value;
                _committedNodesCount = value;
            }
        }

        public int PersistedNodesCount
        {
            get => _persistedNodesCount;
            private set
            {
                Metrics.PersistedNodeCount = value;
                _persistedNodesCount = value;
            }
        }

        public int CachedNodesCount
        {
            get
            {
                Metrics.CachedNodesCount = _dirtyNodes.Count;
                return _dirtyNodes.Count;
            }
        }

        public void CommitNode(long blockNumber, Hash256? address, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitSetExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
            {
                TrieNode node = nodeCommitInfo.Node!;

                if (node!.Keccak is null)
                {
                    throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
                }

                if (CurrentPackage is null)
                {
                    throw new TrieStoreException($"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
                }

                if (node!.LastSeen.HasValue)
                {
                    throw new TrieStoreException($"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
                }

                node = SaveOrReplaceInDirtyNodesCache(address, nodeCommitInfo, node);
                node.LastSeen = Math.Max(blockNumber, node.LastSeen ?? 0);

                if (!_pruningStrategy.PruningEnabled)
                {
                    Persist(address, nodeCommitInfo.Path, node, blockNumber, writeFlags);
                }

                CommittedNodesCount++;
            }
        }

        private HashSet<PruneKey> KeyCached = new HashSet<PruneKey>();

        private TrieNode SaveOrReplaceInDirtyNodesCache(Hash256? address, NodeCommitInfo nodeCommitInfo, TrieNode node)
        {
            if (_pruningStrategy.PruningEnabled)
            {
                if (IsNodeCached(new PruneKey(address, nodeCommitInfo.Path, node.Keccak)))
                {
                    TrieNode cachedNodeCopy = FindCachedOrUnknown(address, nodeCommitInfo.Path, node.Keccak);
                    if (!ReferenceEquals(cachedNodeCopy, node))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
                        cachedNodeCopy.ResolveKey(GetTrieStore(address), nodeCommitInfo.Path, nodeCommitInfo.IsRoot);
                        if (node.Keccak != cachedNodeCopy.Keccak)
                        {
                            throw new InvalidOperationException($"The hash of replacement node {cachedNodeCopy} is not the same as the original {node}.");
                        }

                        if (!nodeCommitInfo.IsRoot)
                        {
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedNodeCopy);
                        }

                        node = cachedNodeCopy;
                        Metrics.ReplacedNodesCount++;
                    }
                }
                else
                {
                    KeyCached.Add(new PruneKey(address, nodeCommitInfo.Path, node.Keccak));
                    _dirtyNodes.SaveInCache(address, nodeCommitInfo.Path, node);
                }
            }

            return node;
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitSetExistsForBlock(blockNumber);

            try
            {
                if (trieType == TrieType.State) // storage tries happen before state commits
                {
                    if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue.Count}");
                    BlockCommitSet set = CurrentPackage;
                    if (set is not null)
                    {
                        set.Root = root;
                        if (_logger.IsTrace) _logger.Trace($"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                        set.Seal();
                    }

                    bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
                    if (shouldPersistSnapshot)
                    {
                        Persist(address, set, writeFlags);
                    }
                    else
                    {
                        PruneCurrentSet();
                    }

                    CurrentPackage = null;
                    if (_pruningStrategy.PruningEnabled && Monitor.IsEntered(_dirtyNodes))
                    {
                        Monitor.Exit(_dirtyNodes);
                    }
                }
            }
            finally
            {
                _currentBatch?.Dispose();
                _currentBatch = null;
            }

            Prune();
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        public static byte[] GetNodeStoragePath(Hash256? address, TreePath path, ValueHash256 keccak)
        {
            byte[] pathBytes = new byte[40];

            if (address != null)
            {
                address.Bytes[..4].CopyTo(pathBytes.AsSpan());
                // Node, this assume Path is zeroed
                path.Path.BytesAsSpan[..4].CopyTo(pathBytes.AsSpan()[4..]);
            } else {
                // I guess we don't actually need 8 byte. 6 byte should be ok enough.
                path.Path.BytesAsSpan[..8].CopyTo(pathBytes.AsSpan()[4..]);
            }
            keccak.Bytes.CopyTo(pathBytes.AsSpan()[8..]);

            return pathBytes;
        }

        public byte[] LoadRlp(Hash256? address, TreePath path, Hash256 keccak, IKeyValueStore? keyValueStore, ReadFlags readFlags = ReadFlags.None)
        {
            keyValueStore ??= _keyValueStore;
            byte[]? rlp = keyValueStore.Get(GetNodeStoragePath(address, path, keccak), readFlags);
            /*
            if (rlp == null)
            {
                Console.Out.WriteLine($"Missing key was cached {KeyCached.Contains(new PruneKey(address, path, keccak))}");
                Console.Out.WriteLine($"Missing key in cache {_dirtyNodes.IsNodeCached(new PruneKey(address, path, keccak))}");
                Console.Out.WriteLine($"Missing key was removed {RemovedKey.GetValueOrDefault(new PruneKey(address, path, keccak), "not removed")}");
                Console.Out.WriteLine($"Missing key was persisted {Persisted.Contains(new PruneKey(address, path, keccak))}");

                byte[] thePath = GetNodeStoragePath(address, path, keccak);
                bool found = false;

                if (keyValueStore is IDb)
                {
                    IDb mem = (IDb)keyValueStore;
                    foreach (var entry in mem.GetAll())
                    {
                        var memKey = entry.Key;
                        if (Bytes.EqualityComparer.Equals(thePath[8..], memKey[8..]))
                        {
                            Console.Out.WriteLine($"Found hash match {thePath.ToHexString()} {memKey.ToHexString()}");
                            found = true;
                        }
                        else
                        if (Bytes.EqualityComparer.Equals(thePath[8..], memKey))
                        {
                            Console.Out.WriteLine($"Found hash match direct {thePath.ToHexString()} {memKey.ToHexString()}");
                            found = true;
                        }
                    }
                }

                if (!found)
                {
                    Console.Out.WriteLine($"No key match {address} {path} {thePath[8..].ToHexString()} {thePath.ToHexString()}");
                }
            }
            */

            if (rlp is null)
            {
                throw new TrieNodeException($"Node {keccak} is missing from the DB", keccak);
            }

            Metrics.LoadedFromDbNodesCount++;

            return rlp;
        }

        public virtual byte[]? LoadRlp(Hash256? address, TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            return LoadRlp(address, path, hash, null, flags);
        }

        public bool IsPersisted(Hash256? address, TreePath path, in ValueHash256 keccak)
        {
            byte[]? rlp = _keyValueStore[TrieStore.GetNodeStoragePath(address, path, keccak)];

            if (rlp is null)
            {
                return false;
            }

            Metrics.LoadedFromDbNodesCount++;

            return true;
        }


        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore)
        {
            return new ReadOnlyTrieStore(this, keyValueStore);
        }

        public bool IsNodeCached(PruneKey key) => _dirtyNodes.IsNodeCached(key);

        public TrieNode FindCachedOrUnknown(Hash256? address, TreePath path, Hash256? hash)
        {
            return FindCachedOrUnknown(address, path, hash, false);
        }

        internal TrieNode FindCachedOrUnknown(Hash256? address, TreePath path, Hash256? hash, bool isReadOnly)
        {
            if (hash is null)
            {
                throw new ArgumentNullException(nameof(hash));
            }

            if (!_pruningStrategy.PruningEnabled)
            {
                return new TrieNode(NodeType.Unknown, hash);
            }

            return isReadOnly ? _dirtyNodes.FromCachedRlpOrUnknown(address, path, hash) : _dirtyNodes.FindCachedOrUnknown(address, path, hash);
        }

        public void Dump() => _dirtyNodes.Dump();

        public void Prune()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache) && _pruningTask.IsCompleted)
            {
                _pruningTask = Task.Run(() =>
                {
                    try
                    {
                        lock (_dirtyNodes)
                        {
                            using (_dirtyNodes.AllNodes.AcquireLock())
                            {
                                if (_logger.IsDebug) _logger.Debug($"Locked {nameof(TrieStore)} for pruning.");

                                while (!_pruningTaskCancellationTokenSource.IsCancellationRequested && _pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
                                {
                                    PruneCache();

                                    if (_pruningTaskCancellationTokenSource.IsCancellationRequested || !CanPruneCacheFurther())
                                    {
                                        break;
                                    }
                                }
                            }
                        }

                        if (_logger.IsDebug) _logger.Debug($"Pruning finished. Unlocked {nameof(TrieStore)}.");
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error("Pruning failed with exception.", e);
                    }
                });
            }
        }

        private bool CanPruneCacheFurther()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
            {
                if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

                using ArrayPoolList<BlockCommitSet> toAddBack = new(_commitSetQueue.Count);
                using ArrayPoolList<BlockCommitSet> candidateSets = new(_commitSetQueue.Count);
                while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
                {
                    if (frontSet!.BlockNumber >= LatestCommittedBlockNumber - Reorganization.MaxDepth)
                    {
                        toAddBack.Add(frontSet);
                    }
                    else if (candidateSets.Count > 0 && candidateSets[0].BlockNumber == frontSet.BlockNumber)
                    {
                        candidateSets.Add(frontSet);
                    }
                    else if (candidateSets.Count == 0 || frontSet.BlockNumber > candidateSets[0].BlockNumber)
                    {
                        candidateSets.Clear();
                        candidateSets.Add(frontSet);
                    }
                }

                // TODO: Find a way to not have to re-add everything
                for (int index = 0; index < toAddBack.Count; index++)
                {
                    _commitSetQueue.Enqueue(toAddBack[index]);
                }

                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Elevated pruning for candidate {blockCommitSet.BlockNumber}");
                    Persist(null, blockCommitSet);
                }

                if (candidateSets.Count > 0)
                {
                    return true;
                }

                _commitSetQueue.TryPeek(out BlockCommitSet? uselessFrontSet);
                if (_logger.IsDebug) _logger.Debug($"Found no candidate for elevated pruning (sets: {_commitSetQueue.Count}, earliest: {uselessFrontSet?.BlockNumber}, newest kept: {LatestCommittedBlockNumber}, reorg depth {Reorganization.MaxDepth})");
            }

            return false;
        }

        /// <summary>
        /// Prunes persisted branches of the current commit set root.
        /// </summary>
        private void PruneCurrentSet()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // We assume that the most recent package very likely resolved many persisted nodes and only replaced
            // some top level branches. Any of these persisted nodes are held in cache now so we just prune them here
            // to avoid the references still being held after we prune the cache.
            // We prune them here but just up to two levels deep which makes it a very lightweight operation.
            // Note that currently the TrieNode ResolveChild un-resolves any persisted child immediately which
            // may make this call unnecessary.
            CurrentPackage?.Root?.PrunePersistedRecursively(2);
            stopwatch.Stop();
            Metrics.DeepPruningTime = stopwatch.ElapsedMilliseconds;
        }

        private Dictionary<PruneKey, string> RemovedKey = new();

        /// <summary>
        /// This method is responsible for reviewing the nodes that are directly in the cache and
        /// removing ones that are either no longer referenced or already persisted.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void PruneCache()
        {
            if (_logger.IsDebug) _logger.Debug($"Pruning nodes {MemoryUsedByDirtyCache / 1.MB()} MB , last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
            Stopwatch stopwatch = Stopwatch.StartNew();

            long newMemory = 0;
            foreach ((PruneKey key, (Hash256? address, TreePath path, TrieNode node)) in _dirtyNodes.AllNodes)
            {
                if (node.IsPersisted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing persisted {node} from memory.");
                    Hash256? keccak = node.Keccak;
                    if (keccak is null)
                    {
                        keccak = node.GenerateKey(this.GetTrieStore(address), path, isRoot: true);
                        PruneKey newKey = new PruneKey(address, path, keccak);
                        if (newKey != key)
                        {
                            throw new InvalidOperationException($"Persisted {node} {key} != {keccak}");
                        }
                    }

                    RemovedKey[key] = "marked persisted";
                    _dirtyNodes.Remove(key);

                    Metrics.PrunedPersistedNodesCount++;
                }
                else if (IsNoLongerNeeded(node))
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing {node} from memory (no longer referenced).");
                    if (node.Keccak is null)
                    {
                        throw new InvalidOperationException($"Removed {node}");
                    }
                    RemovedKey[key] = $"not needed {node.LastSeen} {LastPersistedBlockNumber} {LatestCommittedBlockNumber}";
                    _dirtyNodes.Remove(key);

                    Metrics.PrunedTransientNodesCount++;
                }
                else
                {
                    node.PrunePersistedRecursively(1);
                    newMemory += node.GetMemorySize(false);
                }
            }

            MemoryUsedByDirtyCache = newMemory;
            Metrics.CachedNodesCount = _dirtyNodes.Count;

            stopwatch.Stop();
            Metrics.PruningTime = stopwatch.ElapsedMilliseconds;
            if (_logger.IsDebug) _logger.Debug($"Finished pruning nodes in {stopwatch.ElapsedMilliseconds}ms {MemoryUsedByDirtyCache / 1.MB()} MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache() => _dirtyNodes.Clear();

        public void Dispose()
        {
            if (_logger.IsDebug) _logger.Debug("Disposing trie");
            _pruningTaskCancellationTokenSource.Cancel();
            _pruningTask.Wait();
            PersistOnShutdown();
        }

        #region Private

        protected readonly IKeyValueStoreWithBatching _keyValueStore;

        private readonly ConcurrentDictionary<ValueHash256?, TrieKeyValueStore> _publicStores = new();

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _persistenceStrategy;

        private readonly ILogger _logger;

        private readonly ConcurrentQueue<BlockCommitSet> _commitSetQueue = new();

        private long _memoryUsedByDirtyCache;

        private int _committedNodesCount;

        private int _persistedNodesCount;

        private long _latestPersistedBlockNumber;

        private BlockCommitSet? CurrentPackage { get; set; }

        private bool IsCurrentListSealed => CurrentPackage is null || CurrentPackage.IsSealed;

        private long LatestCommittedBlockNumber { get; set; }

        private void CreateCommitSet(long blockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

            // TODO: this throws on reorgs, does it not? let us recreate it in test
            Debug.Assert(CurrentPackage is null || blockNumber == CurrentPackage.BlockNumber + 1, "Newly begun block is not a successor of the last one");
            Debug.Assert(IsCurrentListSealed, "Not sealed when beginning new block");

            BlockCommitSet commitSet = new(blockNumber);
            _commitSetQueue.Enqueue(commitSet);
            LatestCommittedBlockNumber = Math.Max(blockNumber, LatestCommittedBlockNumber);
            AnnounceReorgBoundaries();
            DequeueOldCommitSets();

            CurrentPackage = commitSet;
            Debug.Assert(ReferenceEquals(CurrentPackage, commitSet), $"Current {nameof(BlockCommitSet)} is not same as the new package just after adding");
        }

        /// <summary>
        /// Persists all transient (not yet persisted) starting from <paramref name="commitSet"/> root.
        /// Already persisted nodes are skipped. After this action we are sure that the full state is available
        /// for the block represented by this commit set.
        /// </summary>
        /// <param name="commitSet">A commit set of a block which root is to be persisted.</param>
        private void Persist(Hash256? address, BlockCommitSet commitSet, WriteFlags writeFlags = WriteFlags.None)
        {
            void PersistNode(TrieNode tn, Hash256? address2, TreePath path) => Persist(address2, path, tn, commitSet.BlockNumber, writeFlags);

            try
            {
                _currentBatch ??= _keyValueStore.StartWriteBatch();
                if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

                Stopwatch stopwatch = Stopwatch.StartNew();
                commitSet.Root?.CallRecursively(PersistNode, address, TreePath.Empty, GetTrieStore(address), true, _logger);
                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

                if (_logger.IsDebug) _logger.Debug($"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {stopwatch.ElapsedMilliseconds}ms (cache memory {MemoryUsedByDirtyCache})");

                LastPersistedBlockNumber = commitSet.BlockNumber;
            }
            finally
            {
                // For safety we prefer to commit half of the batch rather than not commit at all.
                // Generally hanging nodes are not a problem in the DB but anything missing from the DB is.
                _currentBatch?.Dispose();
                _currentBatch = null;
            }

            PruneCurrentSet();
        }

        private HashSet<PruneKey> Persisted = new HashSet<PruneKey>();

        private void Persist(Hash256? address, TreePath path, TrieNode currentNode, long blockNumber, WriteFlags writeFlags = WriteFlags.None)
        {
            _currentBatch ??= _keyValueStore.StartWriteBatch();
            if (currentNode is null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak is not null)
            {
                Debug.Assert(currentNode.LastSeen.HasValue, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");
                Persisted.Add(new PruneKey(address, path, currentNode.Keccak));
                _currentBatch.Set(GetNodeStoragePath(address, path, currentNode.Keccak), currentNode.FullRlp.ToArray(), writeFlags);
                currentNode.IsPersisted = true;
                currentNode.LastSeen = Math.Max(blockNumber, currentNode.LastSeen ?? 0);
                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp.IsNotNull && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        private bool IsNoLongerNeeded(TrieNode node)
        {
            Debug.Assert(node.LastSeen.HasValue, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return node.LastSeen < LastPersistedBlockNumber
                   && node.LastSeen < LatestCommittedBlockNumber - Reorganization.MaxDepth;
        }

        private void DequeueOldCommitSets()
        {
            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                if (blockCommitSet.BlockNumber < LatestCommittedBlockNumber - Reorganization.MaxDepth - 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {LatestCommittedBlockNumber} - {Reorganization.MaxDepth}");
                    _commitSetQueue.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
        }

        private void EnsureCommitSetExistsForBlock(long blockNumber)
        {
            if (CurrentPackage is null)
            {
                if (_pruningStrategy.PruningEnabled && !Monitor.IsEntered(_dirtyNodes))
                {
                    Monitor.Enter(_dirtyNodes);
                }

                CreateCommitSet(blockNumber);
            }
        }

        private void AnnounceReorgBoundaries()
        {
            if (LatestCommittedBlockNumber < 1)
            {
                return;
            }

            bool shouldAnnounceReorgBoundary = !_pruningStrategy.PruningEnabled;
            bool isFirstCommit = Interlocked.Exchange(ref _isFirst, 1) == 0;
            if (isFirstCommit)
            {
                if (_logger.IsDebug) _logger.Debug($"Reached first commit - newest {LatestCommittedBlockNumber}, last persisted {LastPersistedBlockNumber}");
                // this is important when transitioning from fast sync
                // imagine that we transition at block 1200000
                // and then we close the app at 1200010
                // in such case we would try to continue at Head - 1200010
                // because head is loaded if there is no persistence checkpoint
                // so we need to force the persistence checkpoint
                long baseBlock = Math.Max(0, LatestCommittedBlockNumber - 1);
                LastPersistedBlockNumber = baseBlock;
                shouldAnnounceReorgBoundary = true;
            }
            else if (!_lastPersistedReachedReorgBoundary)
            {
                // even after we persist a block we do not really remember it as a safe checkpoint
                // until max reorgs blocks after
                if (LatestCommittedBlockNumber >= LastPersistedBlockNumber + Reorganization.MaxDepth)
                {
                    shouldAnnounceReorgBoundary = true;
                }
            }

            if (shouldAnnounceReorgBoundary)
            {
                ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(LastPersistedBlockNumber));
                _lastPersistedReachedReorgBoundary = true;
            }
        }

        private void PersistOnShutdown()
        {
            // If we are in archive mode, we don't need to change reorg boundaries.
            if (_pruningStrategy.PruningEnabled)
            {
                // here we try to shorten the number of blocks recalculated when restarting (so we force persist)
                // and we need to speed up the standard announcement procedure so we persists a block

                using ArrayPoolList<BlockCommitSet> candidateSets = new(_commitSetQueue.Count);
                while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
                {
                    if (candidateSets.Count == 0 || candidateSets[0].BlockNumber == frontSet!.BlockNumber)
                    {
                        candidateSets.Add(frontSet);
                    }
                    else if (frontSet!.BlockNumber < LatestCommittedBlockNumber - Reorganization.MaxDepth
                             && frontSet!.BlockNumber > candidateSets[0].BlockNumber)
                    {
                        candidateSets.Clear();
                        candidateSets.Add(frontSet);
                    }
                }

                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Persisting on disposal {blockCommitSet} (cache memory at {MemoryUsedByDirtyCache})");
                    Persist(null, blockCommitSet);
                }

                if (candidateSets.Count == 0)
                {
                    if (_logger.IsDebug) _logger.Debug("No commitset to persist at all.");
                }
                else
                {
                    AnnounceReorgBoundaries();
                }
            }
        }

        #endregion

        public void PersistCache(IKeyValueStore store, CancellationToken cancellationToken)
        {
            Task.Run(() =>
            {
                const int million = 1_000_000;
                int persistedNodes = 0;
                Stopwatch stopwatch = Stopwatch.StartNew();

                void PersistNode(TrieNode n, Hash256? address, TreePath path)
                {
                    Hash256? hash = n.Keccak;
                    if (hash is not null)
                    {
                        store.Set(GetNodeStoragePath(address, path, hash), n.FullRlp.ToArray());
                        int persistedNodesCount = Interlocked.Increment(ref persistedNodes);
                        if (_logger.IsInfo && persistedNodesCount % million == 0)
                        {
                            _logger.Info($"Full Pruning Persist Cache in progress: {stopwatch.Elapsed} {persistedNodesCount / million:N} mln nodes persisted.");
                        }
                    }
                }

                if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache started.");
                KeyValuePair<PruneKey, (Hash256?, TreePath, TrieNode)>[] nodesCopy = _dirtyNodes.AllNodes.ToArray();
                Parallel.For(0, nodesCopy.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 }, i =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        nodesCopy[i].Value.Item3.CallRecursively(PersistNode, null, nodesCopy[i].Value.Item2, this.GetTrieStore(nodesCopy[i].Value.Item1), false, _logger, false);
                    }
                });

                if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache finished: {stopwatch.Elapsed} {persistedNodes / (double)million:N} mln nodes persisted.");
            });
        }

        private byte[]? Get(Hash256? address, TreePath path, ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            // TODO: Who would get from here directly
            return _pruningStrategy.PruningEnabled
                   && _dirtyNodes.AllNodes.TryGetValue(new PruneKey(address, path, new ValueHash256(key)), out (Hash256?, TreePath, TrieNode) entry)
                   && entry.Item3 is not null
                   && entry.Item3.NodeType != NodeType.Unknown
                   && entry.Item3.FullRlp.IsNotNull
                ? entry.Item3.FullRlp.ToArray()
                : _keyValueStore.Get(key, flags);
        }

        public IKeyValueStore AsKeyValueStore(Hash256? address)
        {
            return _publicStores.GetOrAdd(address, (key) =>
            {
                return new TrieKeyValueStore(this, key?.ToCommitment());
            });
        }

        private class TrieKeyValueStore : IKeyValueStore
        {
            private readonly TrieStore _trieStore;
            private readonly Hash256? _address;

            public TrieKeyValueStore(TrieStore trieStore, Hash256? address)
            {
                _trieStore = trieStore;
                _address = address;
            }

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _trieStore.Get(_address, TreePath.Empty, key, flags);

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                throw new Exception("Unsupported");
            }
        }

        public class RootTrieStoreProxy : ISmallTrieStore
        {
            private ITrieStore _trieStoreImplementation;
            private ConcurrentDictionary<ValueHash256?, ISmallTrieStore> _storageTrieStore = new();

            public RootTrieStoreProxy(ITrieStore implementation)
            {
                _trieStoreImplementation = implementation;
            }

            public TrieNode FindCachedOrUnknown(TreePath path, Hash256 hash)
            {
                return _trieStoreImplementation.FindCachedOrUnknown(null, path, hash);
            }

            public byte[]? LoadRlp(TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
            {
                return _trieStoreImplementation.LoadRlp(null, path, hash, flags);
            }

            public ISmallTrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
            {
                return GetStorageTrieStore(address);
            }

            public ISmallTrieStore GetStorageTrieStore(Hash256? address)
            {
                return _storageTrieStore.GetOrAdd(address,
                    (_) => new StorageTrieStore(_trieStoreImplementation, address));
            }

            public void Dispose()
            {
                _trieStoreImplementation.Dispose();
            }

            public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
            {
                _trieStoreImplementation.CommitNode(blockNumber, null, nodeCommitInfo, writeFlags);
            }

            public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
            {
                _trieStoreImplementation.FinishBlockCommit(trieType, blockNumber, null, root, writeFlags);
            }

            public bool IsPersisted(TreePath path, in ValueHash256 keccak)
            {
                return _trieStoreImplementation.IsPersisted(null, path, in keccak);
            }

            public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
            {
                add => _trieStoreImplementation.ReorgBoundaryReached += value;
                remove => _trieStoreImplementation.ReorgBoundaryReached -= value;
            }

            public IKeyValueStore AsKeyValueStore()
            {
                return _trieStoreImplementation.AsKeyValueStore(null);
            }
        }

        public class StorageTrieStore : ISmallTrieStore
        {
            private ITrieStore _trieStoreImplementation;
            private readonly Hash256 _address;

            public StorageTrieStore(ITrieStore fullTrieStore, Hash256 address)
            {
                _trieStoreImplementation = fullTrieStore;
                _address = address;
            }

            public TrieNode FindCachedOrUnknown(TreePath path, Hash256 hash)
            {
                return _trieStoreImplementation.FindCachedOrUnknown(_address, path, hash);
            }

            public byte[]? LoadRlp(TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
            {
                return _trieStoreImplementation.LoadRlp(_address, path, hash, flags);
            }

            public ISmallTrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
            {
                throw new Exception("Only two level trie");
            }

            public void Dispose()
            {
                _trieStoreImplementation.Dispose();
            }

            public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
            {
                _trieStoreImplementation.CommitNode(blockNumber, _address, nodeCommitInfo, writeFlags);
            }

            public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
            {
                _trieStoreImplementation.FinishBlockCommit(trieType, blockNumber, _address, root, writeFlags);
            }

            public bool IsPersisted(TreePath path, in ValueHash256 keccak)
            {
                return _trieStoreImplementation.IsPersisted(_address, path, in keccak);
            }

            public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
            {
                add => _trieStoreImplementation.ReorgBoundaryReached += value;
                remove => _trieStoreImplementation.ReorgBoundaryReached -= value;
            }

            public IKeyValueStore AsKeyValueStore()
            {
                return _trieStoreImplementation.AsKeyValueStore(_address);
            }
        }
    }
}
