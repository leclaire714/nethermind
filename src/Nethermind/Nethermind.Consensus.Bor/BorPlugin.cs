using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Init.Steps;

namespace Nethermind.Consensus.Bor;

public class BorPlugin : IConsensusPlugin, IInitializationPlugin
{
    public string Name => "Bor";

    public string Description => $"{SealEngineType} Consensus Engine";

    public string Author => "Nethermind";

    public string SealEngineType => "Bor";

    public Task Init(INethermindApi api)
    {
        if (api.SealEngineType != SealEngineType)
            return Task.CompletedTask;

        (IApiWithStores getApi, IApiWithBlockchain setApi) = api.ForInit;

        // I don't like this a lot, static method, calling from here to http client
        // when maybe we are using grpc, but right now I'm tired.
        HeimdallHttpClient.RegisterConverters(getApi.EthereumJsonSerializer);

        // We need to validate that the sealer is the correct one
        setApi.SealValidator = new BorSealValidator();

        // There are no block rewards on the Bor layer
        setApi.RewardCalculatorSource = NoBlockRewards.Instance;

        // Recover the block author from the extradata signature before processing
        setApi.BlockPreprocessor.AddLast(new BorAuthorRecoveryStep(getApi.EthereumEcdsa!, getApi.ChainSpec!.Bor));

        return Task.CompletedTask;
    }

    public IBlockProductionTrigger DefaultBlockProductionTrigger => throw new NotImplementedException();

    public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
    {
        throw new NotImplementedException();
    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public bool ShouldRunSteps(INethermindApi api)
    {
        ArgumentNullException.ThrowIfNull(api.ChainSpec);
        return api.ChainSpec.SealEngineType == SealEngineType;
    }
}