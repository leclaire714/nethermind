// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;
using Org.BouncyCastle.Utilities.Encoders;

namespace Nethermind.Trie.Test;

public class NibbleTests
{

    [TestCase("")]
    [TestCase("01")]
    [TestCase("0102")]
    [TestCase("010203")]
    [TestCase("01020304")]
    public void CanEncodeAndDecode(string nibbleStr)
    {
        byte[] nibble = Hex.Decode(nibbleStr);

        byte[] encoded = Nibbles.EncodePath(nibble);
        byte[] decoded = Nibbles.DecodePath(encoded);

        decoded.Should().BeEquivalentTo(nibble);
    }
}
