// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;
using Tetron.PowerShellRemoting.Transport;

namespace Tetron.PowerShellRemoting.Tests.Transport;

public class PsrpOpeningMessageTests
{
    private const uint SessionCapability = 0x00010002;
    private const uint InitRunspacePool = 0x00010004;
    private const uint CreatePipeline = 0x00021006;

    [Test]
    public void Session_CapabilityAndInitInOneChunk_IsCompleteWithAllTheBytes()
    {
        var bytes = Concat(Fragment(1, 0, start: true, end: true, Message(SessionCapability, 50)),
                           Fragment(2, 0, start: true, end: true, Message(InitRunspacePool, 80)));
        var opening = new PsrpOpeningMessage(InitRunspacePool);

        opening.Append(bytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opening.IsComplete, Is.True);
            Assert.That(opening.TakeAll(), Is.EqualTo(bytes));
        }
    }

    [Test]
    public void Session_InitSplitAcrossPackets_IsCompleteOnlyAfterItsEndFragment()
    {
        var init = Message(InitRunspacePool, 200);
        var first = Concat(Fragment(1, 0, true, true, Message(SessionCapability, 50)), Fragment(2, 0, start: true, end: false, init[..120]));
        var second = Fragment(2, 1, start: false, end: true, init[120..]);
        var opening = new PsrpOpeningMessage(InitRunspacePool);

        opening.Append(first);
        var afterFirst = opening.IsComplete;
        opening.Append(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirst, Is.False);
            Assert.That(opening.IsComplete, Is.True);
            Assert.That(opening.TakeAll(), Is.EqualTo(Concat(first, second)));
        }
    }

    [Test]
    public void Session_FragmentHeaderSplitAcrossPackets_IsStillReadCorrectly()
    {
        var bytes = Concat(Fragment(1, 0, true, true, Message(SessionCapability, 50)), Fragment(2, 0, true, true, Message(InitRunspacePool, 80)));
        var opening = new PsrpOpeningMessage(InitRunspacePool);

        opening.Append(bytes[..10]);
        opening.Append(bytes[10..]);

        Assert.That(opening.IsComplete, Is.True);
    }

    [Test]
    public void Session_OnlyCapability_IsNotComplete()
    {
        var opening = new PsrpOpeningMessage(InitRunspacePool);

        opening.Append(Fragment(1, 0, true, true, Message(SessionCapability, 50)));

        Assert.That(opening.IsComplete, Is.False);
    }

    [Test]
    public void Pipeline_FirstMessageOfAnyType_CompletesAtItsEndFragment()
    {
        var create = Message(CreatePipeline, 300);
        var opening = new PsrpOpeningMessage(requiredMessageType: null);

        opening.Append(Fragment(7, 0, start: true, end: false, create[..100]));
        var afterStart = opening.IsComplete;
        opening.Append(Fragment(7, 1, start: false, end: true, create[100..]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterStart, Is.False);
            Assert.That(opening.IsComplete, Is.True);
        }
    }

    [Test]
    public void TakeAll_BeforeComplete_IsRefused()
    {
        var opening = new PsrpOpeningMessage(InitRunspacePool);

        Assert.That(() => opening.TakeAll(), Throws.InvalidOperationException);
    }

    /// <summary>A PSRP message: destination (LE), message type (LE), RPID, PID and padding data.</summary>
    private static byte[] Message(uint type, int dataLength)
    {
        var message = new byte[40 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(message, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), type);
        return message;
    }

    /// <summary>A PSRP fragment: object id and fragment id (BE), flags, blob length (BE) and the blob.</summary>
    private static byte[] Fragment(ulong objectId, ulong fragmentId, bool start, bool end, byte[] blob)
    {
        var fragment = new byte[21 + blob.Length];
        BinaryPrimitives.WriteUInt64BigEndian(fragment, objectId);
        BinaryPrimitives.WriteUInt64BigEndian(fragment.AsSpan(8), fragmentId);
        fragment[16] = (byte)((start ? 1 : 0) | (end ? 2 : 0));
        BinaryPrimitives.WriteInt32BigEndian(fragment.AsSpan(17), blob.Length);
        blob.CopyTo(fragment, 21);
        return fragment;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
