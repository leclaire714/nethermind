using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Consensus.Bor;

public class BorValidatorSetContract : CallableContract, IBorValidatorSetContract
{
    private readonly IConstantContract _constant;

    public BorValidatorSetContract(
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
        ITransactionProcessor transactionProcessor,
        IAbiEncoder abiEncoder,
        Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _constant = GetConstant(readOnlyTxProcessorSource);
    }

    public void CommitSpan(BlockHeader header, HeimdallSpan span)
    {
        static Rlp EncodeOne(BorValidator validator) =>
            Rlp.Encode(
                Rlp.Encode(validator.Id),
                Rlp.Encode(validator.VotingPower),
                Rlp.Encode(validator.Address)
            );

        static Rlp Encode(BorValidator[] validators) =>
            Rlp.Encode(validators.Select(EncodeOne).ToArray());

        // encoding validator set to bytes
        byte[] validators = Encode(span.ValidatorSet.Validators).Bytes;

        // encoding selected producers to bytes
        byte[] producers = Encode(span.SelectedProducers).Bytes;

        object[] args = {
            span.Number,
            span.StartBlock,
            span.EndBlock,
            validators,
            producers
        };

        Call(header, "commitSpan", Address.SystemUser, UnlimitedGas, args);
    }

    public BorSpan GetCurrentSpan(BlockHeader header)
    {
        (UInt256 number, UInt256 startBlock, UInt256 endBlock) =
            _constant.Call<UInt256, UInt256, UInt256>(header, "getCurrentSpan", Address.SystemUser);

        return new BorSpan
        {
            Number = (long)number,
            StartBlock = (long)startBlock,
            EndBlock = (long)endBlock,
        };
    }

    public BorSpan GetCurrentValidators(BlockHeader header)
    {
        throw new NotImplementedException();
    }
}