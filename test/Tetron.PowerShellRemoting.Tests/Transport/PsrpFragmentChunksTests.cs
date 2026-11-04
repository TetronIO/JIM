// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;
using Tetron.PowerShellRemoting.Transport;

namespace Tetron.PowerShellRemoting.Tests.Transport;

/// <summary>
/// Splitting PSRP fragments into the pieces sent in WinRM requests. Windows refuses a request over its maximum envelope
/// size (HTTP 413), so a message larger than one request holds, a command carrying a 1 MB parameter for one, goes in
/// several; each piece is whole fragments, as Windows' own client sends them.
/// </summary>
public class PsrpFragmentChunksTests
{
    [Test]
    public void Split_EverythingFits_IsOnePiece()
    {
        var data = Concat(Fragment(1, 0, 100), Fragment(1, 1, 100));

        var pieces = PsrpFragmentChunks.Split(data, maxBytes: 1000).ToList();

        Assert.That(pieces.Select(p => p.ToArray()), Is.EqualTo(new[] { data }));
    }

    [Test]
    public void Split_MoreThanFits_IsWholeFragmentsEachWithinTheLimitAndNothingLost()
    {
        var fragments = Enumerable.Range(0, 10).Select(i => Fragment(1, (ulong)i, 300)).ToArray();
        var data = Concat(fragments);

        var pieces = PsrpFragmentChunks.Split(data, maxBytes: 1000).Select(p => p.ToArray()).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pieces, Has.Count.EqualTo(4), "three 321-byte fragments fit in 1000 bytes, so ten make four pieces");
            Assert.That(pieces, Has.All.Matches<byte[]>(p => p.Length <= 1000));
            Assert.That(pieces, Has.All.Matches<byte[]>(p => p.Length % fragments[0].Length == 0), "whole fragments only");
            Assert.That(Concat([.. pieces]), Is.EqualTo(data), "in order, every byte once");
        }
    }

    [Test]
    public void Split_OneFragmentLargerThanTheLimit_GoesAloneRatherThanBeingCut()
    {
        var large = Fragment(1, 0, 2000);
        var small = Fragment(1, 1, 10);

        var pieces = PsrpFragmentChunks.Split(Concat(large, small), maxBytes: 1000).Select(p => p.ToArray()).ToList();

        Assert.That(pieces, Is.EqualTo(new[] { large, small }));
    }

    [Test]
    public void Split_TrailingBytesThatAreNotAWholeFragment_AreKeptInTheLastPiece()
    {
        var whole = Fragment(1, 0, 100);
        var partial = Fragment(1, 1, 100)[..30];

        var pieces = PsrpFragmentChunks.Split(Concat(whole, partial), maxBytes: 1000).Select(p => p.ToArray()).ToList();

        Assert.That(Concat([.. pieces]), Is.EqualTo(Concat(whole, partial)), "nothing is dropped, even what cannot be parsed");
    }

    [Test]
    public void Split_Nothing_IsNoPieces()
    {
        Assert.That(PsrpFragmentChunks.Split(ReadOnlyMemory<byte>.Empty, maxBytes: 1000), Is.Empty);
    }

    /// <summary>A PSRP fragment: object id and fragment id (BE), flags, blob length (BE) and the blob.</summary>
    private static byte[] Fragment(ulong objectId, ulong fragmentId, int blobLength)
    {
        var fragment = new byte[21 + blobLength];
        BinaryPrimitives.WriteUInt64BigEndian(fragment, objectId);
        BinaryPrimitives.WriteUInt64BigEndian(fragment.AsSpan(8), fragmentId);
        BinaryPrimitives.WriteInt32BigEndian(fragment.AsSpan(17), blobLength);
        for (var i = 0; i < blobLength; i++)
            fragment[21 + i] = (byte)(fragmentId + (ulong)i);
        return fragment;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
