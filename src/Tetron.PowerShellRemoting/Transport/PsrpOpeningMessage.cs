// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// Gathers the PSRP fragments that open a shell or a pipeline until their opening message is complete. WinRM needs
/// the whole of it in one Create or Command, while PowerShell writes it in packets of at most 32 KiB.
/// </summary>
/// <remarks>
/// A PSRP fragment (MS-PSRP 2.2.4) is an object id and a fragment id (big-endian), a flags byte (0x1 start, 0x2 end),
/// a blob length (big-endian) and the blob. The start fragment's blob begins with the message: destination and
/// message type (little-endian), then the runspace pool and pipeline ids.
/// </remarks>
internal sealed class PsrpOpeningMessage(uint? requiredMessageType)
{
    private const int FragmentHeaderLength = 21;
    private const byte StartFlag = 0x1;
    private const byte EndFlag = 0x2;

    private readonly MemoryStream _buffer = new();
    private int _parsed;
    private ulong? _awaitedObject;

    /// <summary>
    /// The message type that completes the opening (INIT_RUNSPACEPOOL for a shell), or null when the first message
    /// of any type does (a pipeline, whose first message is usually CREATE_PIPELINE).
    /// </summary>
    public uint? RequiredMessageType { get; } = requiredMessageType;

    public bool IsComplete { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (IsComplete)
            throw new InvalidOperationException("The opening message is already complete.");

        _buffer.Write(data);
        var bytes = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
        while (!IsComplete && bytes.Length - _parsed >= FragmentHeaderLength)
        {
            var fragment = bytes[_parsed..];
            var objectId = BinaryPrimitives.ReadUInt64BigEndian(fragment);
            var flags = fragment[16];
            var blobLength = BinaryPrimitives.ReadInt32BigEndian(fragment[17..]);
            if (blobLength < 0)
                throw new InvalidDataException("A PSRP fragment declares a negative length.");
            if (fragment.Length < FragmentHeaderLength + blobLength)
                break;

            if ((flags & StartFlag) != 0 && _awaitedObject is null)
            {
                var blob = fragment.Slice(FragmentHeaderLength, blobLength);
                var messageType = blob.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]) : 0;
                if (RequiredMessageType is null || messageType == RequiredMessageType)
                    _awaitedObject = objectId;
            }

            if ((flags & EndFlag) != 0 && objectId == _awaitedObject)
                IsComplete = true;

            _parsed += FragmentHeaderLength + blobLength;
        }
    }

    /// <summary>All the bytes gathered, which may run past the opening message into the next.</summary>
    public byte[] TakeAll()
    {
        if (!IsComplete)
            throw new InvalidOperationException("The opening message is not complete yet.");
        return _buffer.ToArray();
    }
}
