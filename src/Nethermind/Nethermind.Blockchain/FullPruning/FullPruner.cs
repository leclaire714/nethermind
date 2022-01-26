﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Main orchestrator of Full Pruning.
    /// </summary>
    public class FullPruner : IDisposable
    {
        private readonly IFullPruningDb _fullPruningDb;
        private readonly IPruningTrigger _pruningTrigger;
        private readonly IPruningConfig _pruningConfig;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ILogManager _logManager;
        private IPruningContext? _currentPruning;
        private CancellationTokenSource? _cancellationTokenSource;
        private int _waitingForBlockProcessed = 0;
        private int _waitingForStateReady = 0;
        private long _blockToWaitFor;
        private long _stateToCopy;
        private readonly ILogger _logger;

        public FullPruner(
            IFullPruningDb fullPruningDb, 
            IPruningTrigger pruningTrigger,
            IPruningConfig pruningConfig,
            IBlockTree blockTree,
            IStateReader stateReader,
            ILogManager logManager)
        {
            _fullPruningDb = fullPruningDb;
            _pruningTrigger = pruningTrigger;
            _pruningConfig = pruningConfig;
            _blockTree = blockTree;
            _stateReader = stateReader;
            _logManager = logManager;
            _pruningTrigger.Prune += OnPrune;
            _logger = _logManager.GetClassLogger();
        }

        /// <summary>
        /// Is activated by pruning trigger, tries to start full pruning.
        /// </summary>
        private void OnPrune(object? sender, PruningTriggerEventArgs e)
        {
            // Lets assume pruning is in progress
            e.Status = PruningStatus.InProgress;
            
            // If we are already pruning, we don't need to do anything
            if (CanRunPruning())
            {
                // we mark that we are waiting for block (for thread safety)
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 1, 0) == 0)
                {
                    // we don't want to start pruning in the middle of block processing, lets wait for new head.
                    _blockTree.NewHeadBlock += OnNewHead;
                    e.Status = PruningStatus.Starting;
                }
            }
        }

        private void OnNewHead(object? sender, BlockEventArgs e)
        {
            if (CanRunPruning())
            {
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 0, 1) == 1)
                {
                    if (e.Block is not null)
                    {
                        if (_fullPruningDb.TryStartPruning(_pruningConfig.Mode.IsMemory(), out IPruningContext pruningContext))
                        {
                            Interlocked.Exchange(ref _currentPruning, pruningContext);
                            if (Interlocked.CompareExchange(ref _waitingForStateReady, 1, 0) == 0)
                            {
                                _blockToWaitFor = e.Block.Number + 2;
                                _stateToCopy = long.MaxValue;
                                if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: waiting for state {e.Block.Number} to be ready.");
                            }
                        }
                    }
                }
            }
            else if (_waitingForStateReady == 1)
            {
                if (_blockTree.BestPersistedState >= _blockToWaitFor && _currentPruning is not null)
                {
                    if (_stateToCopy == long.MaxValue)
                    {
                        _stateToCopy = _blockTree.BestPersistedState.Value;
                    }

                    long blockToPruneAfter = _stateToCopy + Reorganization.MaxDepth;
                    if (_blockTree.Head?.Number > blockToPruneAfter)
                    {
                        BlockHeader? header = _blockTree.FindHeader(_stateToCopy);
                        if (header is not null && Interlocked.CompareExchange(ref _waitingForStateReady, 0, 1) == 1)
                        {
                            if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: pruning garbage before state {_stateToCopy} with root {header.StateRoot}.");
                            Task.Run(() => RunPruning(_currentPruning, header.StateRoot!));
                            _blockTree.NewHeadBlock -= OnNewHead;
                        }
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for block: {blockToPruneAfter} in order to support reorganizations.");
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for state: Current best saved state {_blockTree.BestPersistedState}, waiting for saved state {_blockToWaitFor} in order to not loose any cached state.");
                }
            }
            else
            {
                _blockTree.NewHeadBlock -= OnNewHead;
            }
        }

        private bool CanRunPruning() => _fullPruningDb.CanStartPruning;

        protected virtual void RunPruning(IPruningContext pruning, Keccak statRoot)
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                pruning.MarkStart();
                using CopyTreeVisitor copyTreeVisitor = new(pruning, _cancellationTokenSource, _logManager);
                VisitingOptions visitingOptions = new() { MaxDegreeOfParallelism = _pruningConfig.FullPruningMaxDegreeOfParallelism };
                _stateReader.RunTreeVisitor(copyTreeVisitor, statRoot, visitingOptions);

                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    copyTreeVisitor.Finish();

                    void CommitOnNewBLock(object o, BlockEventArgs e)
                    {
                        _blockTree.NewHeadBlock -= CommitOnNewBLock;
                        // ReSharper disable AccessToDisposedClosure
                        pruning.Commit();
                        pruning.Dispose();
                        // ReSharper restore AccessToDisposedClosure
                    }

                    _blockTree.NewHeadBlock += CommitOnNewBLock;
                }
                else
                {
                    pruning.Dispose();
                }
            }
            catch (Exception)
            {
                pruning.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewHead;
            _pruningTrigger.Prune -= OnPrune;
            _currentPruning?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}
