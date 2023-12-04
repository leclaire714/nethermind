// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public struct TreePath
{
    public ValueHash256 Path;
    public int Length;

    public static TreePath Empty = new TreePath();

    public TreePath()
    {
    }

    public static TreePath FromPath(ReadOnlySpan<byte> pathHash)
    {
        if (pathHash.Length != 32) throw new Exception("Path must be 32 byte");
        TreePath path = new TreePath();
        path.Path = new Hash256(pathHash);
        path.Length = 32;
        return path;
    }

    public static TreePath FromNibble(Span<byte> pathNibbles)
    {
        TreePath path = new TreePath();
        Span<byte> pathBytes = stackalloc byte[32];
        Nibbles.ToBytes(pathNibbles).CopyTo(pathBytes);
        path.Path = new Hash256(pathBytes);
        path.Length = pathNibbles.Length;
        return path;
    }

    public void Append(params byte[] data)
    {
        foreach (byte b in data)
        {
            this[Length] = b;
            Length++;
        }
    }

    // TODO: Someone will optimize this
    public byte this[int childPathLength]
    {
        get
        {
            if (childPathLength >= 64) throw new IndexOutOfRangeException();
            if (childPathLength % 2 == 0)
            {
                return (byte)(Path.Bytes[childPathLength / 2] & 0xf0);
            }
            else
            {
                return (byte)((Path.Bytes[childPathLength / 2] & 0x0f) << 4);
            }
        }
        set
        {
            if (childPathLength >= 64) throw new IndexOutOfRangeException();
            if (childPathLength % 2 == 0)
            {
                Path.BytesAsSpan[childPathLength / 2] =
                    (byte)((Path.Bytes[childPathLength / 2] & 0x0f) |
                           (value & 0x0f) << 4);
            }
            else
            {
                Path.BytesAsSpan[childPathLength / 2] =
                    (byte)((Path.Bytes[childPathLength / 2] & 0xf0) |
                           ((value & 0x0f)));
            }
        }
    }

    public override string ToString()
    {
        return $"{Length} {Path}";
    }
}
