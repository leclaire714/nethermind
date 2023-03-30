using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Bor;

public class BorValidatorSetContract : CallableContract, IBorValidatorSetContract
{
    public BorValidatorSetContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
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
        object[] result = Call(header, "getCurrentSpan", Address.SystemUser);

        return new BorSpan
        {
            Number = (long)(UInt256)result[0],
            StartBlock = (long)(UInt256)result[1],
            EndBlock = (long)(UInt256)result[2],
        };
    }

    public BorSpan GetCurrentValidators(BlockHeader header)
    {
        throw new NotImplementedException();
    }
}