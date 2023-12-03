// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TreePathTests
{
    [Test]
    public void TestIndexer()
    {
        TreePath path = new TreePath();
        for (int i = 0; i < 64; i++)
        {
            path[i] = (byte)(i % 16);
        }

        string asHex = path.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }
}
