// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls.Shamatar;
using Nethermind.Evm.Precompiles.Snarks.Shamatar;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System.Runtime.Intrinsics;
using System.Diagnostics.CodeAnalysis;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm
{
    public partial class VirtualMachine : IVirtualMachine
    {
        public const int MaxCallDepth = 1024;

        private readonly static UInt256 P255Int = (UInt256)BigInteger.Pow(2, 255);
        private static UInt256 P255 => P255Int;
        private readonly static UInt256 BigInt256 = 256;
        public readonly static UInt256 BigInt32 = 32;

        internal byte[] BytesZero = { 0 };

        internal readonly static byte[] BytesZero32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };

        internal byte[] BytesMax32 =
        {
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255
        };

        private readonly byte[] _chainId;

        private readonly IBlockhashProvider _blockhashProvider;
        private readonly ISpecProvider _specProvider;
        private static readonly LruCache<KeccakKey, CodeInfo> _codeCache = new(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");
        private readonly ILogger _logger;
        private IWorldState _worldState;
        private IStateProvider _state;
        private readonly Stack<EvmState> _stateStack = new();
        private IStorageProvider _storage;
        private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
        private Dictionary<Address, CodeInfo>? _precompiles;
        private byte[] _returnDataBuffer = Array.Empty<byte>();
        private ITxTracer _txTracer = NullTxTracer.Instance;

        public VirtualMachine(
            IBlockhashProvider? blockhashProvider,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
            InitializePrecompiledContracts();
        }

        public TransactionSubstate Run(EvmState state, IWorldState worldState, ITxTracer txTracer)
        {
            _txTracer = txTracer;

            _state = worldState.StateProvider;
            _storage = worldState.StorageProvider;
            _worldState = worldState;

            IReleaseSpec spec = _specProvider.GetSpec(state.Env.TxExecutionContext.Header.Number, state.Env.TxExecutionContext.Header.Timestamp);
            EvmState currentState = state;
            byte[] previousCallResult = null;
            ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;
            UInt256 previousCallOutputDestination = UInt256.Zero;
            while (true)
            {
                if (!currentState.IsContinuation)
                {
                    _returnDataBuffer = Array.Empty<byte>();
                }

                try
                {
                    CallResult callResult;
                    if (currentState.IsPrecompile)
                    {
                        if (_txTracer.IsTracingActions)
                        {
                            _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.Env.InputData, currentState.ExecutionType, true);
                        }

                        callResult = ExecutePrecompile(currentState, spec);

                        if (!callResult.PrecompileSuccess.Value)
                        {
                            if (currentState.IsPrecompile && currentState.IsTopLevel)
                            {
                                Metrics.EvmExceptions++;
                                // TODO: when direct / calls are treated same we should not need such differentiation
                                throw new PrecompileExecutionFailureException();
                            }

                            // TODO: testing it as it seems the way to pass zkSNARKs tests
                            currentState.GasAvailable = 0;
                        }
                    }
                    else
                    {
                        if (_txTracer.IsTracingActions && !currentState.IsContinuation)
                        {
                            _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.ExecutionType.IsAnyCreate() ? currentState.Env.CodeInfo.MachineCode : currentState.Env.InputData, currentState.ExecutionType);
                            if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo.MachineCode);
                        }

                        callResult = ExecuteCall(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                        if (!callResult.IsReturn)
                        {
                            _stateStack.Push(currentState);
                            currentState = callResult.StateToExecute;
                            previousCallResult = null; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests (failing block 9411 on Ropsten https://ropsten.etherscan.io/vmtrace?txhash=0x666194d15c14c54fffafab1a04c08064af165870ef9a87f65711dcce7ed27fe1)
                            _returnDataBuffer = Array.Empty<byte>();
                            previousCallOutput = ZeroPaddedSpan.Empty;
                            continue;
                        }

                        if (callResult.IsException)
                        {
                            if (_txTracer.IsTracingActions) _txTracer.ReportActionError(callResult.ExceptionType);
                            _worldState.Restore(currentState.Snapshot);

                            RevertParityTouchBugAccount(spec);

                            if (currentState.IsTopLevel)
                            {
                                return new TransactionSubstate(callResult.ExceptionType, _txTracer != NullTxTracer.Instance);
                            }

                            previousCallResult = StatusCode.FailureBytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = Array.Empty<byte>();
                            previousCallOutput = ZeroPaddedSpan.Empty;

                            currentState.Dispose();
                            currentState = _stateStack.Pop();
                            currentState.IsContinuation = true;
                            continue;
                        }
                    }

                    if (currentState.IsTopLevel)
                    {
                        if (_txTracer.IsTracingActions)
                        {
                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);

                            if (callResult.IsException)
                            {
                                _txTracer.ReportActionError(callResult.ExceptionType);
                            }
                            else if (callResult.ShouldRevert)
                            {
                                if (currentState.ExecutionType.IsAnyCreate())
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.Revert, currentState.GasAvailable - codeDepositGasCost);
                                }
                                else
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.Revert, currentState.GasAvailable);
                                }
                            }
                            else
                            {
                                if (currentState.ExecutionType.IsAnyCreate() && currentState.GasAvailable < codeDepositGasCost)
                                {
                                    if (spec.ChargeForTopLevelCreate)
                                    {
                                        _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                    }
                                    else
                                    {
                                        _txTracer.ReportActionEnd(currentState.GasAvailable, currentState.To, callResult.Output);
                                    }
                                }
                                // Reject code starting with 0xEF if EIP-3541 is enabled.
                                else if (currentState.ExecutionType.IsAnyCreate() && CodeDepositHandler.CodeIsInvalid(spec, callResult.Output))
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.InvalidCode);
                                }
                                else
                                {
                                    if (currentState.ExecutionType.IsAnyCreate())
                                    {
                                        _txTracer.ReportActionEnd(currentState.GasAvailable - codeDepositGasCost, currentState.To, callResult.Output);
                                    }
                                    else
                                    {
                                        _txTracer.ReportActionEnd(currentState.GasAvailable, _returnDataBuffer);
                                    }
                                }
                            }
                        }

                        return new TransactionSubstate(
                            callResult.Output,
                            currentState.Refund,
                            (IReadOnlyCollection<Address>)currentState.DestroyList,
                            (IReadOnlyCollection<LogEntry>)currentState.Logs,
                            callResult.ShouldRevert,
                            _txTracer != NullTxTracer.Instance);
                    }

                    Address callCodeOwner = currentState.Env.ExecutingAccount;
                    using EvmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                    currentState.GasAvailable += previousState.GasAvailable;
                    bool previousStateSucceeded = true;

                    if (!callResult.ShouldRevert)
                    {
                        long gasAvailableForCodeDeposit = previousState.GasAvailable; // TODO: refactor, this is to fix 61363 Ropsten
                        if (previousState.ExecutionType.IsAnyCreate())
                        {
                            previousCallResult = callCodeOwner.Bytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = Array.Empty<byte>();
                            previousCallOutput = ZeroPaddedSpan.Empty;

                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);
                            bool invalidCode = CodeDepositHandler.CodeIsInvalid(spec, callResult.Output);
                            if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
                            {
                                Keccak codeHash = _state.UpdateCode(callResult.Output);
                                _state.UpdateCodeHash(callCodeOwner, codeHash, spec);
                                currentState.GasAvailable -= codeDepositGasCost;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output);
                                }
                            }
                            else if (spec.FailOnOutOfGasCodeDeposit || invalidCode)
                            {
                                currentState.GasAvailable -= gasAvailableForCodeDeposit;
                                worldState.Restore(previousState.Snapshot);
                                if (!previousState.IsCreateOnPreExistingAccount)
                                {
                                    _state.DeleteAccount(callCodeOwner);
                                }

                                previousCallResult = BytesZero;
                                previousStateSucceeded = false;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
                                }
                            }
                            else if (_txTracer.IsTracingActions)
                            {
                                _txTracer.ReportActionEnd(0L, callCodeOwner, callResult.Output);
                            }
                        }
                        else
                        {
                            _returnDataBuffer = callResult.Output;
                            previousCallResult = callResult.PrecompileSuccess.HasValue ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes) : StatusCode.SuccessBytes;
                            previousCallOutput = callResult.Output.AsSpan().SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                            previousCallOutputDestination = (ulong)previousState.OutputDestination;
                            if (previousState.IsPrecompile)
                            {
                                // parity induced if else for vmtrace
                                if (_txTracer.IsTracingInstructions)
                                {
                                    _txTracer.ReportMemoryChange((long)previousCallOutputDestination, previousCallOutput);
                                }
                            }

                            if (_txTracer.IsTracingActions)
                            {
                                _txTracer.ReportActionEnd(previousState.GasAvailable, _returnDataBuffer);
                            }
                        }

                        if (previousStateSucceeded)
                        {
                            previousState.CommitToParent(currentState);
                        }
                    }
                    else
                    {
                        worldState.Restore(previousState.Snapshot);
                        _returnDataBuffer = callResult.Output;
                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutput = callResult.Output.AsSpan().SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = (ulong)previousState.OutputDestination;


                        if (_txTracer.IsTracingActions)
                        {
                            _txTracer.ReportActionError(EvmExceptionType.Revert, previousState.GasAvailable);
                        }
                    }
                }
                catch (Exception ex) when (ex is EvmException or OverflowException)
                {
                    if (_logger.IsTrace) _logger.Trace($"exception ({ex.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                    _worldState.Restore(currentState.Snapshot);

                    RevertParityTouchBugAccount(spec);

                    if (txTracer.IsTracingInstructions)
                    {
                        txTracer.ReportOperationError(ex is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
                        txTracer.ReportOperationRemainingGas(0);
                    }

                    if (_txTracer.IsTracingActions)
                    {
                        EvmException evmException = ex as EvmException;
                        _txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
                    }

                    if (currentState.IsTopLevel)
                    {
                        return new TransactionSubstate(ex is OverflowException ? EvmExceptionType.Other : (ex as EvmException).ExceptionType, _txTracer != NullTxTracer.Instance);
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutputDestination = UInt256.Zero;
                    _returnDataBuffer = Array.Empty<byte>();
                    previousCallOutput = ZeroPaddedSpan.Empty;

                    currentState.Dispose();
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                }
            }
        }

        private void RevertParityTouchBugAccount(IReleaseSpec spec)
        {
            if (_parityTouchBugAccount.ShouldDelete)
            {
                if (_state.AccountExists(_parityTouchBugAccount.Address))
                {
                    _state.AddToBalance(_parityTouchBugAccount.Address, UInt256.Zero, spec);
                }

                _parityTouchBugAccount.ShouldDelete = false;
            }
        }

        public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
        {
            IStateProvider state = worldState.StateProvider;
            if (codeSource.IsPrecompile(vmSpec))
            {
                if (_precompiles is null)
                {
                    throw new InvalidOperationException("EVM precompile have not been initialized properly.");
                }

                return _precompiles[codeSource];
            }

            Keccak codeHash = state.GetCodeHash(codeSource);
            CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
            if (cachedCodeInfo is null)
            {
                byte[] code = state.GetCode(codeHash);

                if (code is null)
                {
                    throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
                }

                cachedCodeInfo = new CodeInfo(code);
                _codeCache.Set(codeHash, cachedCodeInfo);
            }
            else
            {
                // need to touch code so that any collectors that track database access are informed
                state.TouchCode(codeHash);
            }

            return cachedCodeInfo;
        }

        private void InitializePrecompiledContracts()
        {
            _precompiles = new Dictionary<Address, CodeInfo>
            {
                [EcRecoverPrecompile.Instance.Address] = new(EcRecoverPrecompile.Instance),
                [Sha256Precompile.Instance.Address] = new(Sha256Precompile.Instance),
                [Ripemd160Precompile.Instance.Address] = new(Ripemd160Precompile.Instance),
                [IdentityPrecompile.Instance.Address] = new(IdentityPrecompile.Instance),

                [Bn256AddPrecompile.Instance.Address] = new(Bn256AddPrecompile.Instance),
                [Bn256MulPrecompile.Instance.Address] = new(Bn256MulPrecompile.Instance),
                [Bn256PairingPrecompile.Instance.Address] = new(Bn256PairingPrecompile.Instance),
                [ModExpPrecompile.Instance.Address] = new(ModExpPrecompile.Instance),

                [Blake2FPrecompile.Instance.Address] = new(Blake2FPrecompile.Instance),

                [G1AddPrecompile.Instance.Address] = new(G1AddPrecompile.Instance),
                [G1MulPrecompile.Instance.Address] = new(G1MulPrecompile.Instance),
                [G1MultiExpPrecompile.Instance.Address] = new(G1MultiExpPrecompile.Instance),
                [G2AddPrecompile.Instance.Address] = new(G2AddPrecompile.Instance),
                [G2MulPrecompile.Instance.Address] = new(G2MulPrecompile.Instance),
                [G2MultiExpPrecompile.Instance.Address] = new(G2MultiExpPrecompile.Instance),
                [PairingPrecompile.Instance.Address] = new(PairingPrecompile.Instance),
                [MapToG1Precompile.Instance.Address] = new(MapToG1Precompile.Instance),
                [MapToG2Precompile.Instance.Address] = new(MapToG2Precompile.Instance),

                [PointEvaluationPrecompile.Instance.Address] = new(PointEvaluationPrecompile.Instance),
            };
        }

        private static bool UpdateMemoryCost(EvmPooledMemory? memory, ref long gasAvailable, in UInt256 position, in UInt256 length)
        {
            if (memory is null)
            {
                ThrowInvalidOperationException();
            }

            long memoryCost = memory.CalculateMemoryCost(in position, length);
            bool result = true;
            if (memoryCost > 0L
                && !UpdateGas(memoryCost, ref gasAvailable))
            {
                result = false;
            }

            return result;

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException("EVM memory has not been initialized properly.");
            }
        }

        private static bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (gasAvailable < gasCost)
            {
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        private static void UpdateGasUp(long refund, ref long gasAvailable)
        {
            gasAvailable += refund;
        }

        private bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec, bool chargeForWarm = true)
        {
            // Console.WriteLine($"Accessing {address}");

            bool result = true;
            if (spec.UseHotAndColdStorage)
            {
                if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
                {
                    vmState.WarmUp(address);
                }

                if (vmState.IsCold(address) && !address.IsPrecompile(spec))
                {
                    result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                    vmState.WarmUp(address);
                }
                else if (chargeForWarm)
                {
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
            }

            return result;
        }

        private enum StorageAccessType
        {
            SLOAD,
            SSTORE
        }

        private bool ChargeStorageAccessGas(
            ref long gasAvailable,
            EvmState vmState,
            StorageCell storageCell,
            StorageAccessType storageAccessType,
            IReleaseSpec spec)
        {
            // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

            bool result = true;
            if (spec.UseHotAndColdStorage)
            {
                if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
                {
                    vmState.WarmUp(storageCell);
                }

                if (vmState.IsCold(storageCell))
                {
                    result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                    vmState.WarmUp(storageCell);
                }
                else if (storageAccessType == StorageAccessType.SLOAD)
                {
                    // we do not charge for WARM_STORAGE_READ_COST in SSTORE scenario
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
            }

            return result;
        }

        private CallResult ExecutePrecompile(EvmState state, IReleaseSpec spec)
        {
            ReadOnlyMemory<byte> callData = state.Env.InputData;
            UInt256 transferValue = state.Env.TransferValue;
            long gasAvailable = state.GasAvailable;

            IPrecompile precompile = state.Env.CodeInfo.Precompile;
            long baseGasCost = precompile.BaseGasCost(spec);
            long dataGasCost = precompile.DataGasCost(callData, spec);

            bool wasCreated = false;
            if (!_state.AccountExists(state.Env.ExecutingAccount))
            {
                wasCreated = true;
                _state.CreateAccount(state.Env.ExecutingAccount, transferValue);
            }
            else
            {
                _state.AddToBalance(state.Env.ExecutingAccount, transferValue, spec);
            }

            // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-161.md
            // An additional issue was found in Parity,
            // where the Parity client incorrectly failed
            // to revert empty account deletions in a more limited set of contexts
            // involving out-of-gas calls to precompiled contracts;
            // the new Geth behavior matches Parity’s,
            // and empty accounts will cease to be a source of concern in general
            // in about one week once the state clearing process finishes.
            if (state.Env.ExecutingAccount.Equals(_parityTouchBugAccount.Address)
                && !wasCreated
                && transferValue.IsZero
                && spec.ClearEmptyAccountWhenTouched)
            {
                _parityTouchBugAccount.ShouldDelete = true;
            }

            if (!UpdateGas(baseGasCost, ref gasAvailable))
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            if (!UpdateGas(dataGasCost, ref gasAvailable))
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            state.GasAvailable = gasAvailable;

            try
            {
                (ReadOnlyMemory<byte> output, bool success) = precompile.Run(callData, spec);
                CallResult callResult = new(output.ToArray(), success, !success);
                return callResult;
            }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Error($"Precompiled contract ({precompile.GetType()}) execution exception", exception);
                CallResult callResult = new(Array.Empty<byte>(), false, true);
                return callResult;
            }
        }

        [SkipLocalsInit]
        private CallResult ExecuteCall(EvmState vmState, byte[]? previousCallResult, ZeroPaddedSpan previousCallOutput, scoped in UInt256 previousCallOutputDestination, IReleaseSpec spec)
        {
            bool isTrace = _logger.IsTrace;
            bool traceOpcodes = _txTracer.IsTracingInstructions;
            ExecutionEnvironment env = vmState.Env;
            TxExecutionContext txCtx = env.TxExecutionContext;

            if (!vmState.IsContinuation)
            {
                if (!_state.AccountExists(env.ExecutingAccount))
                {
                    _state.CreateAccount(env.ExecutingAccount, env.TransferValue);
                }
                else
                {
                    _state.AddToBalance(env.ExecutingAccount, env.TransferValue, spec);
                }

                if (vmState.ExecutionType.IsAnyCreate() && spec.ClearEmptyAccountWhenTouched)
                {
                    _state.IncrementNonce(env.ExecutingAccount);
                }
            }

            if (vmState.Env.CodeInfo.MachineCode.Length == 0)
            {
                return CallResult.Empty;
            }

            vmState.InitStacks();
            EvmStack stack = new(vmState.DataStack.AsSpan(), vmState.DataStackHead, _txTracer);
            long gasAvailable = vmState.GasAvailable;
            int programCounter = vmState.ProgramCounter;
            Span<byte> code = env.CodeInfo.MachineCode.AsSpan();


            static void UpdateCurrentState(EvmState state, in int pc, in long gas, in int stackHead)
            {
                state.ProgramCounter = pc;
                state.GasAvailable = gas;
                state.DataStackHead = stackHead;
            }

            void StartInstructionTrace(Instruction instruction, EvmStack stackValue)
            {
                _txTracer.StartOperation(env.CallDepth + 1, gasAvailable, instruction, programCounter, txCtx.Header.IsPostMerge);
                if (_txTracer.IsTracingMemory)
                {
                    _txTracer.SetOperationMemory(vmState.Memory?.GetTrace() ?? new List<string>());
                }

                if (_txTracer.IsTracingStack)
                {
                    _txTracer.SetOperationStack(stackValue.GetStackTrace());
                }
            }

            void Jump(in UInt256 jumpDest, bool isSubroutine = false)
            {
                if (jumpDest > int.MaxValue)
                {
                    Metrics.EvmExceptions++;
                    if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidJumpDestination);
                    // https://github.com/NethermindEth/nethermind/issues/140
                    throw new InvalidJumpDestinationException();
                    //                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
                }

                int jumpDestInt = (int)jumpDest;

                if (!env.CodeInfo.ValidateJump(jumpDestInt, isSubroutine))
                {
                    if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidJumpDestination);
                    // https://github.com/NethermindEth/nethermind/issues/140
                    throw new InvalidJumpDestinationException();
                    //                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                }

                programCounter = jumpDestInt;
            }

            if (previousCallResult is not null)
            {
                stack.PushBytes(previousCallResult);
                if (traceOpcodes) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
            }

            if (previousCallOutput.Length > 0)
            {
                UInt256 localPreviousDest = previousCallOutputDestination;
                if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in localPreviousDest, (ulong)previousCallOutput.Length))
                {
                    goto OutOfGas;
                }

                vmState.Memory.Save(in localPreviousDest, previousCallOutput);
                //                if(traceOpcodes) _txTracer.ReportMemoryChange((long)localPreviousDest, previousCallOutput);
            }

            while (programCounter < code.Length)
            {
                Instruction instruction = (Instruction)code[programCounter];
                // Console.WriteLine(instruction);
                if (traceOpcodes)
                {
                    StartInstructionTrace(instruction, stack);
                }

                programCounter++;
                switch (instruction)
                {
                    case Instruction.STOP:
                        {
                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            return CallResult.Empty;
                        }
                    case Instruction.ADD:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionADD(ref stack);
                            break;
                        }
                    case Instruction.MUL:
                        {
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            InstructionMUL(ref stack);
                            break;
                        }
                    case Instruction.SUB:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionSUB(ref stack);
                            break;
                        }
                    case Instruction.DIV:
                        {
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            InstructionDIV(ref stack);
                            break;
                        }
                    case Instruction.SDIV:
                        {
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            InstructionSDIV(ref stack);
                            break;
                        }
                    case Instruction.MOD:
                        {
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            InstructionMOD(ref stack);
                            break;
                        }
                    case Instruction.SMOD:
                        {
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            InstructionSMOD(ref stack);
                            break;
                        }
                    case Instruction.ADDMOD:
                        {
                            if (!UpdateGas(GasCostOf.Mid, ref gasAvailable)) goto OutOfGas;

                            InstructionADDMOD(ref stack);
                            break;
                        }
                    case Instruction.MULMOD:
                        {
                            if (!UpdateGas(GasCostOf.Mid, ref gasAvailable)) goto OutOfGas;

                            InstructionMULMOD(ref stack);
                            break;
                        }
                    case Instruction.EXP:
                        {
                            if (!UpdateGas(GasCostOf.Exp, ref gasAvailable)) goto OutOfGas;

                            if (!InstructionEXP(ref stack, ref gasAvailable, spec)) goto OutOfGas;
                            break;
                        }
                    case Instruction.SIGNEXTEND:
                        {
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            InstructionSIGNEXTEND(ref stack);
                            break;
                        }
                    case Instruction.LT:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionLT(ref stack);
                            break;
                        }
                    case Instruction.GT:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionGT(ref stack);
                            break;
                        }
                    case Instruction.SLT:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionSLT(ref stack);
                            break;
                        }
                    case Instruction.SGT:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionSGT(ref stack);
                            break;
                        }
                    case Instruction.EQ:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionEQ(ref stack);
                            break;
                        }
                    case Instruction.ISZERO:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionISZERO(ref stack);
                            break;
                        }
                    case Instruction.AND:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionAND(ref stack);
                            break;
                        }
                    case Instruction.OR:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionOR(ref stack);
                            break;
                        }
                    case Instruction.XOR:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionXOR(ref stack);
                            break;
                        }
                    case Instruction.NOT:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionNOT(ref stack);
                            break;
                        }
                    case Instruction.BYTE:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            InstructionBYTE(ref stack);
                            break;
                        }
                    case Instruction.SHA3:
                        {
                            stack.PopUInt256(out UInt256 memSrc);
                            stack.PopUInt256(out UInt256 memLength);
                            if (!UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(memLength),
                                ref gasAvailable)) goto OutOfGas;

                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memSrc, memLength)) goto OutOfGas;

                            InstructionSHA3(ref stack, vmState, in memSrc, in memLength);
                            break;
                        }
                    case Instruction.ADDRESS:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(env.ExecutingAccount.Bytes);
                            break;
                        }
                    case Instruction.BALANCE:
                        {
                            long gasCost = spec.GetBalanceCost();
                            if (gasCost != 0 && !UpdateGas(gasCost, ref gasAvailable)) goto OutOfGas;

                            Address address = stack.PopAddress();
                            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                            UInt256 balance = _state.GetBalance(address);
                            stack.PushUInt256(in balance);
                            break;
                        }
                    case Instruction.CALLER:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(env.Caller.Bytes);
                            break;
                        }
                    case Instruction.CALLVALUE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 callValue = env.Value;
                            stack.PushUInt256(in callValue);
                            break;
                        }
                    case Instruction.ORIGIN:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(txCtx.Origin.Bytes);
                            break;
                        }
                    case Instruction.CALLDATALOAD:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 src);
                            stack.PushBytes(env.InputData.SliceWithZeroPadding(src, 32));
                            break;
                        }
                    case Instruction.CALLDATASIZE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 callDataSize = (UInt256)env.InputData.Length;
                            stack.PushUInt256(in callDataSize);
                            break;
                        }
                    case Instruction.CALLDATACOPY:
                        {
                            stack.PopUInt256(out UInt256 dest);
                            stack.PopUInt256(out UInt256 src);
                            stack.PopUInt256(out UInt256 length);
                            if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length),
                                ref gasAvailable)) goto OutOfGas;

                            if (length > UInt256.Zero)
                            {
                                if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in dest, length)) goto OutOfGas;

                                ZeroPaddedMemory callDataSlice = env.InputData.SliceWithZeroPadding(src, (int)length);
                                vmState.Memory.Save(in dest, callDataSlice);
                                if (traceOpcodes)
                                {
                                    _txTracer.ReportMemoryChange((long)dest, callDataSlice);
                                }
                            }

                            break;
                        }
                    case Instruction.CODESIZE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 codeLength = (UInt256)code.Length;
                            stack.PushUInt256(in codeLength);
                            break;
                        }
                    case Instruction.CODECOPY:
                        {
                            stack.PopUInt256(out UInt256 dest);
                            stack.PopUInt256(out UInt256 src);
                            stack.PopUInt256(out UInt256 length);
                            if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length), ref gasAvailable)) goto OutOfGas;

                            if (length > UInt256.Zero)
                            {
                                if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in dest, length)) goto OutOfGas;

                                ZeroPaddedSpan codeSlice = code.SliceWithZeroPadding(src, (int)length);
                                vmState.Memory.Save(in dest, codeSlice);
                                if (traceOpcodes) _txTracer.ReportMemoryChange((long)dest, codeSlice);
                            }

                            break;
                        }
                    case Instruction.GASPRICE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 gasPrice = txCtx.GasPrice;
                            stack.PushUInt256(in gasPrice);
                            break;
                        }
                    case Instruction.EXTCODESIZE:
                        {
                            if (!UpdateGas(spec.GetExtCodeCost(), ref gasAvailable)) goto OutOfGas;

                            Address address = stack.PopAddress();
                            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                            byte[] accountCode = GetCachedCodeInfo(_worldState, address, spec).MachineCode;
                            UInt256 codeSize = (UInt256)accountCode.Length;
                            stack.PushUInt256(in codeSize);
                            break;
                        }
                    case Instruction.EXTCODECOPY:
                        {
                            Address address = stack.PopAddress();
                            stack.PopUInt256(out UInt256 dest);
                            stack.PopUInt256(out UInt256 src);
                            stack.PopUInt256(out UInt256 length);

                            long gasCost = spec.GetExtCodeCost();
                            if (!UpdateGas(gasCost + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length),
                                ref gasAvailable)) goto OutOfGas;
                            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                            if (length > UInt256.Zero)
                            {
                                if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in dest, length)) goto OutOfGas;

                                byte[] externalCode = GetCachedCodeInfo(_worldState, address, spec).MachineCode;
                                ZeroPaddedSpan callDataSlice = externalCode.SliceWithZeroPadding(src, (int)length);
                                vmState.Memory.Save(in dest, callDataSlice);
                                if (traceOpcodes)
                                {
                                    _txTracer.ReportMemoryChange((long)dest, callDataSlice);
                                }
                            }

                            break;
                        }
                    case Instruction.RETURNDATASIZE:
                        {
                            if (!spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 res = (UInt256)_returnDataBuffer.Length;
                            stack.PushUInt256(in res);
                            break;
                        }
                    case Instruction.RETURNDATACOPY:
                        {
                            if (!spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;

                            stack.PopUInt256(out UInt256 dest);
                            stack.PopUInt256(out UInt256 src);
                            stack.PopUInt256(out UInt256 length);
                            if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length), ref gasAvailable)) goto OutOfGas;

                            if (UInt256.AddOverflow(length, src, out UInt256 newLength) || newLength > _returnDataBuffer.Length)
                            {
                                return CallResult.AccessViolationException;
                            }

                            if (length > UInt256.Zero)
                            {
                                if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in dest, length)) goto OutOfGas;

                                ZeroPaddedSpan returnDataSlice = _returnDataBuffer.AsSpan().SliceWithZeroPadding(src, (int)length);
                                vmState.Memory.Save(in dest, returnDataSlice);
                                if (traceOpcodes)
                                {
                                    _txTracer.ReportMemoryChange((long)dest, returnDataSlice);
                                }
                            }

                            break;
                        }
                    case Instruction.BLOCKHASH:
                        {
                            Metrics.BlockhashOpcode++;

                            if (!UpdateGas(GasCostOf.BlockHash, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 a);
                            long number = a > long.MaxValue ? long.MaxValue : (long)a;
                            Keccak blockHash = _blockhashProvider.GetBlockhash(txCtx.Header, number);
                            stack.PushBytes(blockHash?.Bytes ?? BytesZero32);

                            if (isTrace)
                            {
                                if (_txTracer.IsTracingBlockHash && blockHash is not null)
                                {
                                    _txTracer.ReportBlockHash(blockHash);
                                }
                            }

                            break;
                        }
                    case Instruction.COINBASE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(txCtx.Header.GasBeneficiary.Bytes);
                            break;
                        }
                    case Instruction.PREVRANDAO:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            if (txCtx.Header.IsPostMerge)
                            {
                                byte[] random = txCtx.Header.Random.Bytes;
                                stack.PushBytes(random);
                            }
                            else
                            {
                                UInt256 diff = txCtx.Header.Difficulty;
                                stack.PushUInt256(in diff);
                            }
                            break;
                        }
                    case Instruction.TIMESTAMP:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 timestamp = txCtx.Header.Timestamp;
                            stack.PushUInt256(in timestamp);
                            break;
                        }
                    case Instruction.NUMBER:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 blockNumber = (UInt256)txCtx.Header.Number;
                            stack.PushUInt256(in blockNumber);
                            break;
                        }
                    case Instruction.GASLIMIT:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 gasLimit = (UInt256)txCtx.Header.GasLimit;
                            stack.PushUInt256(in gasLimit);
                            break;
                        }
                    case Instruction.CHAINID:
                        {
                            if (!spec.ChainIdOpcodeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(_chainId);
                            break;
                        }
                    case Instruction.SELFBALANCE:
                        {
                            if (!spec.SelfBalanceOpcodeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.SelfBalance, ref gasAvailable)) goto OutOfGas;

                            UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                            stack.PushUInt256(in balance);
                            break;
                        }
                    case Instruction.BASEFEE:
                        {
                            if (!spec.BaseFeeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 baseFee = txCtx.Header.BaseFeePerGas;
                            stack.PushUInt256(in baseFee);
                            break;
                        }
                    case Instruction.DATAHASH:
                        {
                            if (!spec.IsEip4844Enabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.DataHash, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 blobIndex);

                            if (txCtx.BlobVersionedHashes is not null && blobIndex < txCtx.BlobVersionedHashes.Length)
                            {
                                stack.PushBytes(txCtx.BlobVersionedHashes[blobIndex.u0]);
                            }
                            else
                            {
                                stack.PushZero();
                            }
                            break;
                        }
                    case Instruction.POP:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PopLimbo();
                            break;
                        }
                    case Instruction.MLOAD:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 memPosition);

                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memPosition, 32)) goto OutOfGas;

                            Span<byte> memData = vmState.Memory.LoadSpan(in memPosition);
                            if (traceOpcodes) _txTracer.ReportMemoryChange(memPosition, memData);

                            stack.PushBytes(memData);
                            break;
                        }
                    case Instruction.MSTORE:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 memPosition);

                            Span<byte> data = stack.PopBytes();

                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memPosition, 32)) goto OutOfGas;

                            vmState.Memory.SaveWord(in memPosition, data);
                            if (traceOpcodes) _txTracer.ReportMemoryChange((long)memPosition, data.SliceWithZeroPadding(0, 32, PadDirection.Left));

                            break;
                        }
                    case Instruction.MSTORE8:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 memPosition);
                            byte data = stack.PopByte();
                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memPosition, UInt256.One)) goto OutOfGas;
                            vmState.Memory.SaveByte(in memPosition, data);
                            if (traceOpcodes) _txTracer.ReportMemoryChange((long)memPosition, data);

                            break;
                        }
                    case Instruction.SLOAD:
                        {
                            Metrics.SloadOpcode++;
                            if (!UpdateGas(spec.GetSLoadCost(), ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 storageIndex);
                            StorageCell storageCell = new(env.ExecutingAccount, storageIndex);
                            if (!ChargeStorageAccessGas(
                                ref gasAvailable,
                                vmState,
                                storageCell,
                                StorageAccessType.SLOAD,
                                spec)) goto OutOfGas;

                            byte[] value = _storage.Get(storageCell);
                            stack.PushBytes(value);

                            if (_txTracer.IsTracingOpLevelStorage)
                            {
                                _txTracer.LoadOperationStorage(storageCell.Address, storageIndex, value);
                            }

                            break;
                        }
                    case Instruction.SSTORE:
                        {
                            Metrics.SstoreOpcode++;

                            if (vmState.IsStatic) goto StaticCallViolation;
                            // fail fast before the first storage read if gas is not enough even for reset
                            if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) goto OutOfGas;
                            if (spec.UseNetGasMeteringWithAStipendFix)
                            {
                                if (_txTracer.IsTracingRefunds) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
                                if (gasAvailable <= GasCostOf.CallStipend) goto OutOfGas;
                            }

                            stack.PopUInt256(out UInt256 storageIndex);
                            Span<byte> newValue = stack.PopBytes();
                            bool newIsZero = newValue.IsZero();
                            if (!newIsZero)
                            {
                                newValue = newValue.WithoutLeadingZeros().ToArray();
                            }
                            else
                            {
                                newValue = new byte[] { 0 };
                            }

                            StorageCell storageCell = new(env.ExecutingAccount, storageIndex);

                            if (!ChargeStorageAccessGas(
                                ref gasAvailable,
                                vmState,
                                storageCell,
                                StorageAccessType.SSTORE,
                                spec)) goto OutOfGas;

                            Span<byte> currentValue = _storage.Get(storageCell);
                            // Console.WriteLine($"current: {currentValue.ToHexString()} newValue {newValue.ToHexString()}");
                            bool currentIsZero = currentValue.IsZero();

                            bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, newValue);
                            long sClearRefunds = RefundOf.SClear(spec.IsEip3529Enabled);

                            if (!spec.UseNetGasMetering) // note that for this case we already deducted 5000
                            {
                                if (newIsZero)
                                {
                                    if (!newSameAsCurrent)
                                    {
                                        vmState.Refund += sClearRefunds;
                                        if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(sClearRefunds);
                                    }
                                }
                                else if (currentIsZero)
                                {
                                    if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable)) goto OutOfGas;
                                }
                            }
                            else // net metered
                            {
                                if (newSameAsCurrent)
                                {
                                    if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable)) goto OutOfGas;
                                }
                                else // net metered, C != N
                                {
                                    Span<byte> originalValue = _storage.GetOriginal(storageCell);
                                    bool originalIsZero = originalValue.IsZero();

                                    bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                                    if (currentSameAsOriginal)
                                    {
                                        if (currentIsZero)
                                        {
                                            if (!UpdateGas(GasCostOf.SSet, ref gasAvailable)) goto OutOfGas;
                                        }
                                        else // net metered, current == original != new, !currentIsZero
                                        {
                                            if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) goto OutOfGas;

                                            if (newIsZero)
                                            {
                                                vmState.Refund += sClearRefunds;
                                                if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(sClearRefunds);
                                            }
                                        }
                                    }
                                    else // net metered, new != current != original
                                    {
                                        long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                                        if (!UpdateGas(netMeteredStoreCost, ref gasAvailable)) goto OutOfGas;

                                        if (!originalIsZero) // net metered, new != current != original != 0
                                        {
                                            if (currentIsZero)
                                            {
                                                vmState.Refund -= sClearRefunds;
                                                if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(-sClearRefunds);
                                            }

                                            if (newIsZero)
                                            {
                                                vmState.Refund += sClearRefunds;
                                                if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(sClearRefunds);
                                            }
                                        }

                                        bool newSameAsOriginal = Bytes.AreEqual(originalValue, newValue);
                                        if (newSameAsOriginal)
                                        {
                                            long refundFromReversal;
                                            if (originalIsZero)
                                            {
                                                refundFromReversal = spec.GetSetReversalRefund();
                                            }
                                            else
                                            {
                                                refundFromReversal = spec.GetClearReversalRefund();
                                            }

                                            vmState.Refund += refundFromReversal;
                                            if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(refundFromReversal);
                                        }
                                    }
                                }
                            }

                            if (!newSameAsCurrent)
                            {
                                Span<byte> valueToStore = newIsZero ? BytesZero : newValue;
                                _storage.Set(storageCell, valueToStore.ToArray());
                            }

                            if (traceOpcodes)
                            {
                                Span<byte> valueToStore = newIsZero ? BytesZero : newValue;
                                Span<byte> span = new byte[32]; // do not stackalloc here
                                storageCell.Index.ToBigEndian(span);
                                _txTracer.ReportStorageChange(span, valueToStore);
                            }

                            if (_txTracer.IsTracingOpLevelStorage)
                            {
                                _txTracer.SetOperationStorage(storageCell.Address, storageIndex, newValue, currentValue);
                            }

                            break;
                        }
                    case Instruction.TLOAD:
                        {
                            Metrics.TloadOpcode++;
                            if (!spec.TransientStorageEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.TLoad, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 storageIndex);
                            StorageCell storageCell = new(env.ExecutingAccount, storageIndex);

                            byte[] value = _storage.GetTransientState(storageCell);
                            stack.PushBytes(value);

                            if (_txTracer.IsTracingOpLevelStorage)
                            {
                                _txTracer.LoadOperationTransientStorage(storageCell.Address, storageIndex, value);
                            }

                            break;
                        }
                    case Instruction.TSTORE:
                        {
                            Metrics.TstoreOpcode++;
                            if (!spec.TransientStorageEnabled) goto InvalidInstruction;
                            if (vmState.IsStatic) goto StaticCallViolation;
                            if (!UpdateGas(GasCostOf.TStore, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 storageIndex);
                            Span<byte> newValue = stack.PopBytes();
                            bool newIsZero = newValue.IsZero();
                            if (!newIsZero)
                            {
                                newValue = newValue.WithoutLeadingZeros().ToArray();
                            }
                            else
                            {
                                newValue = BytesZero;
                            }

                            StorageCell storageCell = new(env.ExecutingAccount, storageIndex);
                            byte[] currentValue = newValue.ToArray();
                            _storage.SetTransientState(storageCell, currentValue);

                            if (_txTracer.IsTracingOpLevelStorage)
                            {
                                _txTracer.SetOperationTransientStorage(storageCell.Address, storageIndex, newValue, currentValue);
                            }

                            break;
                        }
                    case Instruction.JUMP:
                        {
                            if (!UpdateGas(GasCostOf.Mid, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 jumpDest);
                            Jump(jumpDest);
                            break;
                        }
                    case Instruction.JUMPI:
                        {
                            if (!UpdateGas(GasCostOf.High, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 jumpDest);
                            Span<byte> condition = stack.PopBytes();
                            if (!condition.SequenceEqual(BytesZero32))
                            {
                                Jump(jumpDest);
                            }

                            break;
                        }
                    case Instruction.PC:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt32(programCounter - 1);
                            break;
                        }
                    case Instruction.MSIZE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 size = vmState.Memory.Size;
                            stack.PushUInt256(in size);
                            break;
                        }
                    case Instruction.GAS:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            UInt256 gas = (UInt256)gasAvailable;
                            stack.PushUInt256(in gas);
                            break;
                        }
                    case Instruction.JUMPDEST:
                        {
                            if (!UpdateGas(GasCostOf.JumpDest, ref gasAvailable)) goto OutOfGas;

                            break;
                        }
                    case Instruction.PUSH0:
                        {
                            if (!spec.IncludePush0Instruction) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushZero();

                            break;
                        }
                    case Instruction.PUSH1:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            int programCounterInt = programCounter;
                            if (programCounterInt >= code.Length)
                            {
                                stack.PushZero();
                            }
                            else
                            {
                                stack.PushByte(code[programCounterInt]);
                            }

                            programCounter++;
                            break;
                        }
                    case Instruction.PUSH2:
                    case Instruction.PUSH3:
                    case Instruction.PUSH4:
                    case Instruction.PUSH5:
                    case Instruction.PUSH6:
                    case Instruction.PUSH7:
                    case Instruction.PUSH8:
                    case Instruction.PUSH9:
                    case Instruction.PUSH10:
                    case Instruction.PUSH11:
                    case Instruction.PUSH12:
                    case Instruction.PUSH13:
                    case Instruction.PUSH14:
                    case Instruction.PUSH15:
                    case Instruction.PUSH16:
                    case Instruction.PUSH17:
                    case Instruction.PUSH18:
                    case Instruction.PUSH19:
                    case Instruction.PUSH20:
                    case Instruction.PUSH21:
                    case Instruction.PUSH22:
                    case Instruction.PUSH23:
                    case Instruction.PUSH24:
                    case Instruction.PUSH25:
                    case Instruction.PUSH26:
                    case Instruction.PUSH27:
                    case Instruction.PUSH28:
                    case Instruction.PUSH29:
                    case Instruction.PUSH30:
                    case Instruction.PUSH31:
                    case Instruction.PUSH32:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            int length = instruction - Instruction.PUSH1 + 1;
                            int programCounterInt = programCounter;
                            int usedFromCode = Math.Min(code.Length - programCounterInt, length);

                            stack.PushLeftPaddedBytes(code.Slice(programCounterInt, usedFromCode), length);

                            programCounter += length;
                            break;
                        }
                    case Instruction.DUP1:
                    case Instruction.DUP2:
                    case Instruction.DUP3:
                    case Instruction.DUP4:
                    case Instruction.DUP5:
                    case Instruction.DUP6:
                    case Instruction.DUP7:
                    case Instruction.DUP8:
                    case Instruction.DUP9:
                    case Instruction.DUP10:
                    case Instruction.DUP11:
                    case Instruction.DUP12:
                    case Instruction.DUP13:
                    case Instruction.DUP14:
                    case Instruction.DUP15:
                    case Instruction.DUP16:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.Dup(instruction - Instruction.DUP1 + 1);
                            break;
                        }
                    case Instruction.SWAP1:
                    case Instruction.SWAP2:
                    case Instruction.SWAP3:
                    case Instruction.SWAP4:
                    case Instruction.SWAP5:
                    case Instruction.SWAP6:
                    case Instruction.SWAP7:
                    case Instruction.SWAP8:
                    case Instruction.SWAP9:
                    case Instruction.SWAP10:
                    case Instruction.SWAP11:
                    case Instruction.SWAP12:
                    case Instruction.SWAP13:
                    case Instruction.SWAP14:
                    case Instruction.SWAP15:
                    case Instruction.SWAP16:
                        {
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.Swap(instruction - Instruction.SWAP1 + 2);
                            break;
                        }
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                        {
                            if (vmState.IsStatic) goto StaticCallViolation;

                            stack.PopUInt256(out UInt256 memoryPos);
                            stack.PopUInt256(out UInt256 length);
                            long topicsCount = instruction - Instruction.LOG0;
                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memoryPos, length)) goto OutOfGas;
                            if (!UpdateGas(
                                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                                (long)length * GasCostOf.LogData, ref gasAvailable)) goto OutOfGas;

                            ReadOnlyMemory<byte> data = vmState.Memory.Load(in memoryPos, length);
                            Keccak[] topics = new Keccak[topicsCount];
                            for (int i = 0; i < topicsCount; i++)
                            {
                                topics[i] = new Keccak(stack.PopBytes().ToArray());
                            }

                            LogEntry logEntry = new(
                                env.ExecutingAccount,
                                data.ToArray(),
                                topics);
                            vmState.Logs.Add(logEntry);
                            break;
                        }
                    case Instruction.CREATE:
                    case Instruction.CREATE2:
                        {
                            if (!spec.Create2OpcodeEnabled && instruction == Instruction.CREATE2) goto InvalidInstruction;

                            if (vmState.IsStatic) goto StaticCallViolation;

                            // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                            if (!_state.AccountExists(env.ExecutingAccount))
                            {
                                _state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
                            }

                            stack.PopUInt256(out UInt256 value);
                            stack.PopUInt256(out UInt256 memoryPositionOfInitCode);
                            stack.PopUInt256(out UInt256 initCodeLength);
                            Span<byte> salt = null;
                            if (instruction == Instruction.CREATE2)
                            {
                                salt = stack.PopBytes();
                            }

                            //EIP-3860
                            if (spec.IsEip3860Enabled)
                            {
                                if (initCodeLength > spec.MaxInitCodeSize) goto OutOfGas;
                            }

                            long gasCost = GasCostOf.Create +
                                (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0) +
                                (instruction == Instruction.CREATE2 ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0);

                            if (!UpdateGas(gasCost, ref gasAvailable)) goto OutOfGas;
                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memoryPositionOfInitCode, initCodeLength)) goto OutOfGas;

                            // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
                            if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                            {
                                // TODO: need a test for this
                                _returnDataBuffer = Array.Empty<byte>();
                                stack.PushZero();
                                break;
                            }

                            Span<byte> initCode = vmState.Memory.LoadSpan(in memoryPositionOfInitCode, initCodeLength);

                            UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                            if (value > balance)
                            {
                                _returnDataBuffer = Array.Empty<byte>();
                                stack.PushZero();
                                break;
                            }

                            UInt256 accountNonce = _state.GetNonce(env.ExecutingAccount);
                            UInt256 maxNonce = ulong.MaxValue;
                            if (accountNonce >= maxNonce)
                            {
                                _returnDataBuffer = Array.Empty<byte>();
                                stack.PushZero();
                                break;
                            }

                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            // todo: === below is a new call - refactor / move

                            long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
                            if (!UpdateGas(callGas, ref gasAvailable)) goto OutOfGas;

                            Address contractAddress = instruction == Instruction.CREATE
                                ? ContractAddress.From(env.ExecutingAccount, _state.GetNonce(env.ExecutingAccount))
                                : ContractAddress.From(env.ExecutingAccount, salt, initCode);

                            if (spec.UseHotAndColdStorage)
                            {
                                // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
                                vmState.WarmUp(contractAddress);
                            }

                            _state.IncrementNonce(env.ExecutingAccount);

                            Snapshot snapshot = _worldState.TakeSnapshot();

                            bool accountExists = _state.AccountExists(contractAddress);
                            if (accountExists && (GetCachedCodeInfo(_worldState, contractAddress, spec).MachineCode.Length != 0 || _state.GetNonce(contractAddress) != 0))
                            {
                                /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
                                if (isTrace) _logger.Trace($"Contract collision at {contractAddress}");
                                _returnDataBuffer = Array.Empty<byte>();
                                stack.PushZero();
                                break;
                            }

                            if (accountExists)
                            {
                                _state.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
                            }
                            else if (_state.IsDeadAccount(contractAddress))
                            {
                                _storage.ClearStorage(contractAddress);
                            }

                            _state.SubtractFromBalance(env.ExecutingAccount, value, spec);
                            ExecutionEnvironment callEnv = new();
                            callEnv.TxExecutionContext = env.TxExecutionContext;
                            callEnv.CallDepth = env.CallDepth + 1;
                            callEnv.Caller = env.ExecutingAccount;
                            callEnv.ExecutingAccount = contractAddress;
                            callEnv.CodeSource = null;
                            callEnv.CodeInfo = new CodeInfo(initCode.ToArray());
                            callEnv.InputData = ReadOnlyMemory<byte>.Empty;
                            callEnv.TransferValue = value;
                            callEnv.Value = value;

                            EvmState callState = new(
                                callGas,
                                callEnv,
                                instruction == Instruction.CREATE2 ? ExecutionType.Create2 : ExecutionType.Create,
                                false,
                                snapshot,
                                0L,
                                0L,
                                vmState.IsStatic,
                                vmState,
                                false,
                                accountExists);

                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            return new CallResult(callState);
                        }
                    case Instruction.RETURN:
                        {
                            stack.PopUInt256(out UInt256 memoryPos);
                            stack.PopUInt256(out UInt256 length);

                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memoryPos, length)) goto OutOfGas;

                            ReadOnlyMemory<byte> returnData = vmState.Memory.Load(in memoryPos, length);

                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            return new CallResult(returnData.ToArray(), null);
                        }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                        {
                            Metrics.Calls++;

                            if (instruction == Instruction.DELEGATECALL && !spec.DelegateCallEnabled ||
                                instruction == Instruction.STATICCALL && !spec.StaticCallEnabled) goto InvalidInstruction;

                            stack.PopUInt256(out UInt256 gasLimit);
                            Address codeSource = stack.PopAddress();

                            // Console.WriteLine($"CALLIN {codeSource}");
                            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource, spec)) goto OutOfGas;

                            UInt256 callValue;
                            switch (instruction)
                            {
                                case Instruction.STATICCALL:
                                    callValue = UInt256.Zero;
                                    break;
                                case Instruction.DELEGATECALL:
                                    callValue = env.Value;
                                    break;
                                default:
                                    stack.PopUInt256(out callValue);
                                    break;
                            }

                            UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;
                            stack.PopUInt256(out UInt256 dataOffset);
                            stack.PopUInt256(out UInt256 dataLength);
                            stack.PopUInt256(out UInt256 outputOffset);
                            stack.PopUInt256(out UInt256 outputLength);

                            if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE) goto StaticCallViolation;

                            Address caller = instruction == Instruction.DELEGATECALL ? env.Caller : env.ExecutingAccount;
                            Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? codeSource : env.ExecutingAccount;

                            if (isTrace)
                            {
                                _logger.Trace($"caller {caller}");
                                _logger.Trace($"code source {codeSource}");
                                _logger.Trace($"target {target}");
                                _logger.Trace($"value {callValue}");
                                _logger.Trace($"transfer value {transferValue}");
                            }

                            long gasExtra = 0L;

                            if (!transferValue.IsZero)
                            {
                                gasExtra += GasCostOf.CallValue;
                            }

                            if (!spec.ClearEmptyAccountWhenTouched && !_state.AccountExists(target))
                            {
                                gasExtra += GasCostOf.NewAccount;
                            }
                            else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && _state.IsDeadAccount(target))
                            {
                                gasExtra += GasCostOf.NewAccount;
                            }

                            if (!UpdateGas(spec.GetCallCost(), ref gasAvailable)) goto OutOfGas;

                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in dataOffset, dataLength)) goto OutOfGas;
                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in outputOffset, outputLength)) goto OutOfGas;

                            if (!UpdateGas(gasExtra, ref gasAvailable)) goto OutOfGas;

                            if (spec.Use63Over64Rule)
                            {
                                gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
                            }

                            if (gasLimit >= long.MaxValue) goto OutOfGas;

                            long gasLimitUl = (long)gasLimit;
                            if (!UpdateGas(gasLimitUl, ref gasAvailable)) goto OutOfGas;

                            if (!transferValue.IsZero)
                            {
                                if (_txTracer.IsTracingRefunds) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
                                gasLimitUl += GasCostOf.CallStipend;
                            }

                            if (env.CallDepth >= MaxCallDepth || !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
                            {
                                _returnDataBuffer = Array.Empty<byte>();
                                stack.PushZero();

                                if (traceOpcodes)
                                {
                                    // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                                    ReadOnlyMemory<byte> memoryTrace = vmState.Memory.Inspect(in dataOffset, 32);
                                    _txTracer.ReportMemoryChange(dataOffset, memoryTrace.Span);
                                }

                                if (isTrace) _logger.Trace("FAIL - call depth");
                                if (traceOpcodes) _txTracer.ReportOperationRemainingGas(gasAvailable);
                                if (traceOpcodes) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

                                UpdateGasUp(gasLimitUl, ref gasAvailable);
                                if (traceOpcodes) _txTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
                                break;
                            }

                            ReadOnlyMemory<byte> callData = vmState.Memory.Load(in dataOffset, dataLength);

                            Snapshot snapshot = _worldState.TakeSnapshot();
                            _state.SubtractFromBalance(caller, transferValue, spec);

                            ExecutionEnvironment callEnv = new();
                            callEnv.TxExecutionContext = env.TxExecutionContext;
                            callEnv.CallDepth = env.CallDepth + 1;
                            callEnv.Caller = caller;
                            callEnv.CodeSource = codeSource;
                            callEnv.ExecutingAccount = target;
                            callEnv.TransferValue = transferValue;
                            callEnv.Value = callValue;
                            callEnv.InputData = callData;
                            callEnv.CodeInfo = GetCachedCodeInfo(_worldState, codeSource, spec);

                            if (isTrace) _logger.Trace($"Tx call gas {gasLimitUl}");
                            if (outputLength == 0)
                            {
                                // TODO: when output length is 0 outputOffset can have any value really
                                // and the value does not matter and it can cause trouble when beyond long range
                                outputOffset = 0;
                            }

                            ExecutionType executionType = GetCallExecutionType(instruction, txCtx.Header.IsPostMerge);
                            EvmState callState = new(
                                gasLimitUl,
                                callEnv,
                                executionType,
                                false,
                                snapshot,
                                (long)outputOffset,
                                (long)outputLength,
                                instruction == Instruction.STATICCALL || vmState.IsStatic,
                                vmState,
                                false,
                                false);

                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            return new CallResult(callState);
                        }
                    case Instruction.REVERT:
                        {
                            if (!spec.RevertOpcodeEnabled) goto InvalidInstruction;

                            stack.PopUInt256(out UInt256 memoryPos);
                            stack.PopUInt256(out UInt256 length);

                            if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in memoryPos, length)) goto OutOfGas;

                            ReadOnlyMemory<byte> errorDetails = vmState.Memory.Load(in memoryPos, length);

                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            return new CallResult(errorDetails.ToArray(), null, true);
                        }
                    case Instruction.INVALID:
                        {
                            if (!UpdateGas(GasCostOf.High, ref gasAvailable)) goto OutOfGas;

                            goto InvalidInstruction;
                        }
                    case Instruction.SELFDESTRUCT:
                        {
                            if (vmState.IsStatic) goto StaticCallViolation;
                            if (spec.UseShanghaiDDosProtection && !UpdateGas(GasCostOf.SelfDestructEip150, ref gasAvailable)) goto OutOfGas;

                            Metrics.SelfDestructs++;

                            Address inheritor = stack.PopAddress();
                            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, spec, false)) goto OutOfGas;

                            vmState.DestroyList.Add(env.ExecutingAccount);

                            UInt256 ownerBalance = _state.GetBalance(env.ExecutingAccount);
                            if (_txTracer.IsTracingActions) _txTracer.ReportSelfDestruct(env.ExecutingAccount, ownerBalance, inheritor);
                            if (spec.ClearEmptyAccountWhenTouched && ownerBalance != 0 && _state.IsDeadAccount(inheritor))
                            {
                                if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) goto OutOfGas;
                            }

                            bool inheritorAccountExists = _state.AccountExists(inheritor);
                            if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
                            {
                                if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) goto OutOfGas;
                            }

                            if (!inheritorAccountExists)
                            {
                                _state.CreateAccount(inheritor, ownerBalance);
                            }
                            else if (!inheritor.Equals(env.ExecutingAccount))
                            {
                                _state.AddToBalance(inheritor, ownerBalance, spec);
                            }

                            _state.SubtractFromBalance(env.ExecutingAccount, ownerBalance, spec);

                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            return CallResult.Empty;
                        }
                    case Instruction.SHL:
                        {
                            if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 a);
                            if (a >= 256UL)
                            {
                                stack.PopLimbo();
                                stack.PushZero();
                            }
                            else
                            {
                                stack.PopUInt256(out UInt256 b);
                                UInt256 res = b << (int)a.u0;
                                stack.PushUInt256(in res);
                            }

                            break;
                        }
                    case Instruction.SHR:
                        {
                            if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 a);
                            if (a >= 256)
                            {
                                stack.PopLimbo();
                                stack.PushZero();
                            }
                            else
                            {
                                stack.PopUInt256(out UInt256 b);
                                UInt256 res = b >> (int)a.u0;
                                stack.PushUInt256(in res);
                            }

                            break;
                        }
                    case Instruction.SAR:
                        {
                            if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 a);
                            stack.PopSignedInt256(out Int256.Int256 b);
                            if (a >= BigInt256)
                            {
                                if (b.Sign >= 0)
                                {
                                    stack.PushZero();
                                }
                                else
                                {
                                    Int256.Int256 res = Int256.Int256.MinusOne;
                                    stack.PushSignedInt256(in res);
                                }
                            }
                            else
                            {
                                b.RightShift((int)a, out Int256.Int256 res);
                                stack.PushSignedInt256(in res);
                            }

                            break;
                        }
                    case Instruction.EXTCODEHASH:
                        {
                            if (!spec.ExtCodeHashOpcodeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(spec.GetExtCodeHashCost(), ref gasAvailable)) goto OutOfGas;

                            Address address = stack.PopAddress();
                            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                            if (!_state.AccountExists(address) || _state.IsDeadAccount(address))
                            {
                                stack.PushZero();
                            }
                            else
                            {
                                stack.PushBytes(_state.GetCodeHash(address).Bytes);
                            }

                            break;
                        }
                    case Instruction.BEGINSUB:
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidSubroutineEntry);
                            return CallResult.InvalidSubroutineEntry;
                        }
                    case Instruction.RETURNSUB:
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            if (vmState.ReturnStackHead == 0)
                            {
                                if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidSubroutineReturn);
                                return CallResult.InvalidSubroutineReturn;
                            }

                            programCounter = vmState.ReturnStack[--vmState.ReturnStackHead];
                            break;
                        }
                    case Instruction.JUMPSUB:
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.High, ref gasAvailable)) goto OutOfGas;

                            if (vmState.ReturnStackHead == EvmStack.ReturnStackSize)
                            {
                                if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.StackOverflow);
                                return CallResult.StackOverflowException;
                            }

                            vmState.ReturnStack[vmState.ReturnStackHead++] = programCounter;

                            stack.PopUInt256(out UInt256 jumpDest);
                            Jump(jumpDest, true);
                            programCounter++;

                            break;
                        }
                    default:
                        {
                            goto InvalidInstruction;
                        }
                }

                if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
            }

            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
            return CallResult.Empty;

// Common exit errors, goto labels to reduce in loop code duplication
OutOfGas:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.OutOfGas);
            return CallResult.OutOfGasException;
InvalidInstruction:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.BadInstruction);
            return CallResult.InvalidInstructionException;
StaticCallViolation:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.StaticCallViolation);
            return CallResult.StaticCallViolationException;
        }

        private void EndInstructionTrace(long gasAvailable, ulong memorySize)
        {
            if (_txTracer.IsTracingMemory)
            {
                _txTracer.SetOperationMemorySize(memorySize);
            }

            _txTracer.ReportOperationRemainingGas(gasAvailable);
        }

        private void EndInstructionTraceError(long gasAvailable, EvmExceptionType evmExceptionType)
        {
            _txTracer.ReportOperationError(evmExceptionType);
            _txTracer.ReportOperationRemainingGas(gasAvailable);
        }

        private static ExecutionType GetCallExecutionType(Instruction instruction, bool isPostMerge = false)
        {
            ExecutionType executionType;
            if (instruction == Instruction.CALL)
            {
                executionType = ExecutionType.Call;
            }
            else if (instruction == Instruction.DELEGATECALL)
            {
                executionType = ExecutionType.DelegateCall;
            }
            else if (instruction == Instruction.STATICCALL)
            {
                executionType = ExecutionType.StaticCall;
            }
            else if (instruction == Instruction.CALLCODE)
            {
                executionType = ExecutionType.CallCode;
            }
            else
            {
                throw new NotSupportedException($"Execution type is undefined for {instruction.GetName(isPostMerge)}");
            }

            return executionType;
        }

        internal readonly ref struct CallResult
        {
            public static CallResult InvalidSubroutineEntry => new(EvmExceptionType.InvalidSubroutineEntry);
            public static CallResult InvalidSubroutineReturn => new(EvmExceptionType.InvalidSubroutineReturn);
            public static CallResult OutOfGasException => new(EvmExceptionType.OutOfGas);
            public static CallResult AccessViolationException => new(EvmExceptionType.AccessViolation);
            public static CallResult InvalidJumpDestination => new(EvmExceptionType.InvalidJumpDestination);
            public static CallResult InvalidInstructionException
            {
                get
                {
                    return new(EvmExceptionType.BadInstruction);
                }
            }

            public static CallResult StaticCallViolationException => new(EvmExceptionType.StaticCallViolation);
            public static CallResult StackOverflowException => new(EvmExceptionType.StackOverflow); // TODO: use these to avoid CALL POP attacks
            public static CallResult StackUnderflowException => new(EvmExceptionType.StackUnderflow); // TODO: use these to avoid CALL POP attacks

            public static CallResult InvalidCodeException => new(EvmExceptionType.InvalidCode);
            public static CallResult Empty => new(Array.Empty<byte>(), null);

            public CallResult(EvmState stateToExecute)
            {
                StateToExecute = stateToExecute;
                Output = Array.Empty<byte>();
                PrecompileSuccess = null;
                ShouldRevert = false;
                ExceptionType = EvmExceptionType.None;
            }

            private CallResult(EvmExceptionType exceptionType)
            {
                StateToExecute = null;
                Output = StatusCode.FailureBytes;
                PrecompileSuccess = null;
                ShouldRevert = false;
                ExceptionType = exceptionType;
            }

            public CallResult(byte[] output, bool? precompileSuccess, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
            {
                StateToExecute = null;
                Output = output;
                PrecompileSuccess = precompileSuccess;
                ShouldRevert = shouldRevert;
                ExceptionType = exceptionType;
            }

            public EvmState? StateToExecute { get; }
            public byte[] Output { get; }
            public EvmExceptionType ExceptionType { get; }
            public bool ShouldRevert { get; }
            public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case
            public bool IsReturn => StateToExecute is null;
            public bool IsException => ExceptionType != EvmExceptionType.None;
        }
    }
}
