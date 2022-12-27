// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus
{
    public class BlockProducerEnv
    {
        public IBlockTree BlockTree { get; set; }
        public IBlockchainProcessor ChainProcessor { get; set; }
        public IStateProvider ReadOnlyStateProvider { get; set; }
        public ITxSource TxSource { get; set; }
        public IReadOnlyTxProcessorSource ReadOnlyTxProcessingEnv { get; set; }
    }
}
