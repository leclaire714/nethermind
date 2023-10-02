// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.EliasFano;

namespace Nethermind.Verkle.Tree;

public class HistoryOfAccounts
{
    public int BlocksChunks { get; set; } = 2000;
    private readonly IDb _historyOfAccounts;
    private static readonly EliasFanoDecoder _decoder = new ();

    public HistoryOfAccounts(IDb historyOfAccounts)
    {
        _historyOfAccounts = historyOfAccounts;
    }

    public void AppendHistoryBlockNumberForKey(Pedersen key, ulong blockNumber)
    {
        List<ulong> shard = GetLastShardOfBlocks(key);
        Console.WriteLine($"AppendHistoryBlockNumberForKey: {key} {blockNumber} LastShard:{string.Join(",", shard)}");
        shard.Add(blockNumber);
        InsertShard(key, shard);
    }

    private void InsertShard(Pedersen key, List<ulong> shard)
    {
        EliasFanoBuilder efb = new(shard[^1] + 1, shard.Count);
        efb.Extend(shard);
        EliasFano ef = efb.Build();
        RlpStream streamNew = new (_decoder.GetLength(ef, RlpBehaviors.None));
        _decoder.Encode(streamNew, ef);
        if (shard.Count == BlocksChunks)
        {
            HistoryKey historyKey = new HistoryKey(key, shard[^1]);
            _historyOfAccounts[historyKey.Encode()] = streamNew.Data;
            historyKey = new HistoryKey(key, ulong.MaxValue);
            _historyOfAccounts.Remove(historyKey.Encode());
        }
        else
        {
            HistoryKey historyKey = new HistoryKey(key, ulong.MaxValue);
            _historyOfAccounts[historyKey.Encode()] = streamNew.Data;
        }
    }

    private void InsertShards(Pedersen key, List<List<ulong>> shardsList)
    {
        foreach (List<ulong> shard in shardsList) InsertShard(key, shard);
    }

    private List<ulong> GetLastShardOfBlocks(Pedersen key)
    {
        HistoryKey shardKey = new HistoryKey(key, ulong.MaxValue);
        byte[]? ef = _historyOfAccounts[shardKey.Encode()];
        List<ulong> shard = new();
        if (ef is not null)
        {
            EliasFano eliasFano = _decoder.Decode(new RlpStream(ef));
            shard.AddRange(eliasFano.GetEnumerator(0));
        }
        return shard;
    }

    public EliasFano? GetAppropriateShard(Pedersen key, ulong blockNumber)
    {
        HistoryKey historyKey = new HistoryKey(key, blockNumber);
        IEnumerable<KeyValuePair<byte[], byte[]?>> itr = _historyOfAccounts.GetIterator(historyKey.Encode());
        KeyValuePair<byte[], byte[]?> keyVal = itr.FirstOrDefault();
        return keyVal.Key is not null ? _decoder.Decode(new RlpStream(keyVal.Value!)) : null;
    }
}

public readonly struct HistoryKey
{
    public Pedersen Key { get; }
    public ulong MaxBlock { get; }

    public HistoryKey(Pedersen address, ulong maxBlock)
    {
        Key = address;
        MaxBlock = maxBlock;
    }

    public byte[] Encode()
    {
        byte[] data = new byte[40];
        Span<byte> dataSpan = data;
        Key.Bytes.CopyTo(dataSpan);
        BinaryPrimitives.WriteUInt64BigEndian(dataSpan.Slice(32), MaxBlock);
        return data;
    }
}

