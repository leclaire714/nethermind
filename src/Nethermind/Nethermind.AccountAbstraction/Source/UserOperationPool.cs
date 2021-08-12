//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.Tracing.Access;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using Nethermind.AccountAbstraction.Flashbots;
using Nethermind.Core.Timers;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Api;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationPool : IUserOperationPool
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly IAccessListSource _accessListSource;
        private readonly IAccountAbstractionConfig _accountAbstractionConfig;
        private readonly IDictionary<Address, int> _paymasterOffenseCounter;
        private readonly ISet<Address> _bannedPaymasters;
        private readonly UserOperationSortedPool _userOperationSortedPool;
        private readonly IUserOperationSimulator _userOperationSimulator;
        private readonly ConcurrentDictionary<UserOperation, SimulatedUserOperation> _simulatedUserOperations;
        private readonly ITimer _timer;

        public UserOperationPool(
            IBlockTree blockTree,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            IAccessListSource accessListSource,
            IAccountAbstractionConfig accountAbstractionConfig,
            IDictionary<Address, int> paymasterOffenseCounter,
            ISet<Address> bannedPaymasters,
            UserOperationSortedPool userOperationSortedPool,
            IUserOperationSimulator userOperationSimulator,
            ConcurrentDictionary<UserOperation, SimulatedUserOperation> simulatedUserOperations,
            ITimerFactory timerFactory)
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _timestamper = timestamper;
            _accessListSource = accessListSource;
            _accountAbstractionConfig = accountAbstractionConfig;
            _paymasterOffenseCounter = paymasterOffenseCounter;
            _bannedPaymasters = bannedPaymasters;
            _userOperationSortedPool = userOperationSortedPool;
            _userOperationSimulator = userOperationSimulator;
            _simulatedUserOperations = simulatedUserOperations;

            blockTree.NewHeadBlock += OnNewBlock;
            _userOperationSortedPool.Inserted += UserOperationInserted;
            _userOperationSortedPool.Removed += UserOperationRemoved;
            
            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(5000));
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }
        
        private void UserOperationInserted(object? sender, SortedPool<UserOperation, UserOperation, Address>.SortedPoolEventArgs e)
        {
            UserOperation userOperation = e.Key;
            SimulateAndAddToPool(userOperation, _blockTree.Head!.Header);
        }

        private void UserOperationRemoved(object? sender, SortedPool<UserOperation, UserOperation, Address>.SortedPoolRemovedEventArgs e)
        {
            UserOperation userOperation = e.Key;
            _simulatedUserOperations.TryRemove(userOperation, out _);
        }

        private void OnNewBlock(object? sender, BlockEventArgs e)
        {
            Block block = e.Block;
            
            HashSet<Address> blockAccessedAddresses = _accessListSource.AccessList.Data.Keys.ToHashSet();

            blockAccessedAddresses.Remove(block.Beneficiary);
            blockAccessedAddresses.Remove(new Address(_accountAbstractionConfig.SingletonContractAddress));

            _userOperationSortedPool.GetSnapshot().Select(op => op.AccessList);

            foreach (UserOperation op in _userOperationSortedPool.GetSnapshot())
            {
                if (blockAccessedAddresses.Overlaps(op.AccessList.Data.Keys))
                {
                    if (op.ResimulationCounter > _accountAbstractionConfig.MaxResimulations)
                    {
                        _userOperationSortedPool.TryRemove(op);
                        _simulatedUserOperations.Remove(op, out _);

                        if (op.Paymaster == Address.Zero)
                        {
                            _paymasterOffenseCounter[op.Target]++;
                            if (_paymasterOffenseCounter[op.Target] > _accountAbstractionConfig.MaxResimulations)
                            {
                                _bannedPaymasters.Add(op.Target);
                            }
                        }
                        else
                        {
                            _paymasterOffenseCounter[op.Paymaster]++;
                            if (_paymasterOffenseCounter[op.Paymaster] > _accountAbstractionConfig.MaxResimulations)
                            {
                                _bannedPaymasters.Add(op.Paymaster);
                            }
                        }
                    }
                    op.ResimulationCounter++;
                    _simulatedUserOperations.TryRemove(op, out _);
                    SimulateAndAddToPool(op, block.Header);
                }
            } 
            
            
            // verify each one still has enough balance, nonce is correct etc.
        }

        public IEnumerable<UserOperation> GetUserOperations() => _userOperationSortedPool.GetSnapshot();

        public bool AddUserOperation(UserOperation userOperation)
        {
            if (ValidateUserOperation(userOperation, out SimulatedUserOperation simulatedUserOperation))
            {
                return _userOperationSortedPool.TryInsert(userOperation, userOperation);
            }

            return false;
        }

        private bool ValidateUserOperation(UserOperation userOperation, out SimulatedUserOperation simulatedUserOperation)
        {
            if (userOperation.MaxFeePerGas < _accountAbstractionConfig.MinimumGasPrice 
                || userOperation.CallGas < Transaction.BaseTxGasCost)
            {
                simulatedUserOperation = SimulatedUserOperation.FailedSimulatedUserOperation(userOperation);
                return false;
            }

            // make sure target account exists
            if (
                userOperation.Target == Address.Zero
                || !_stateProvider.AccountExists(userOperation.Target))
            {
                simulatedUserOperation = SimulatedUserOperation.FailedSimulatedUserOperation(userOperation);
                return false;
            }

            // make sure paymaster is a contract (if paymaster is used) and is not on banned list
            if (userOperation.Paymaster != Address.Zero)
            {
                if (!_stateProvider.AccountExists(userOperation.Paymaster) 
                    || !_stateProvider.IsContract(userOperation.Paymaster)
                    || _bannedPaymasters.Contains(userOperation.Paymaster))
                {
                    simulatedUserOperation = SimulatedUserOperation.FailedSimulatedUserOperation(userOperation);
                    return false;
                }
            }

            // make sure op not already in pool
            if (_userOperationSortedPool.GetSnapshot().Contains(userOperation))
            {
                simulatedUserOperation = SimulatedUserOperation.FailedSimulatedUserOperation(userOperation);
                return false;
            }

            // simulate
            simulatedUserOperation = Task.Run(() => _userOperationSimulator.Simulate(userOperation, _blockTree.Head.Header, CancellationToken.None, _timestamper.UnixTime.Seconds)).Result;
            if (simulatedUserOperation.Success == false)
            {
                simulatedUserOperation = SimulatedUserOperation.FailedSimulatedUserOperation(userOperation);
                return false;
            }

            return true;
        }

        private async void SimulateAndAddToPool(UserOperation userOperation, BlockHeader parent)
        {
            SimulatedUserOperation simulatedUserOperation = await _userOperationSimulator.Simulate(
                userOperation, 
                parent, 
                CancellationToken.None, 
                _timestamper.UnixTime.Seconds);

            if (simulatedUserOperation.Success)
            {
                _simulatedUserOperations[userOperation] = simulatedUserOperation;
            }
            else
            {
                _userOperationSortedPool.TryRemove(userOperation);
            }

        }
        
        private void TimerOnElapsed(object sender, EventArgs args)
        {
            INethermindApi _nethermindApi = null!;
            ILogger _logger = null!;
            FlashbotsSender flashbotsSender = new FlashbotsSender(new HttpClient(), _nethermindApi.EngineSigner, _logger);
            
            IList<UserOperation> userOperations = new List<UserOperation>();
            IEnumerable<SimulatedUserOperation> simulatedUserOperations = _simulatedUserOperations.Values.OrderByDescending(op => op.ImpliedGasPrice);
            foreach (SimulatedUserOperation operations in simulatedUserOperations)
            {
                userOperations.Add(operations.UserOperation);
            }
            
            if (_userOperationSortedPool.GetSnapshot().Length > 0)
            {
                // turn ops into txs
                Transaction transaction = new Transaction();
                foreach (UserOperation op in _userOperationSortedPool.GetSnapshot())
                {
                    transaction = _userOperationSimulator.BuildTransactionFromUserOperations(userOperations, _blockTree.Head.Header);
                }
                
                // turn txs into MevBundle
                FlashbotsSender.MevBundle bundle = new FlashbotsSender.MevBundle(_blockTree.Head.Header.Number + 1, new []{transaction});
                
                // send MevBundle using SendBundle()
                flashbotsSender.SendBundle(bundle, _accountAbstractionConfig.FlashbotsEndpoint);
            }
            
            _timer.Enabled = true;
        }
    }
}
