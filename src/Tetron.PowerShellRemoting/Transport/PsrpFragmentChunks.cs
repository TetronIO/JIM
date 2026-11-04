// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// Splits PSRP fragments into the pieces sent in separate WinRM requests. Windows refuses a request larger than its
/// maximum envelope size with HTTP 413, so a message bigger than one request (a command carrying a large parameter) has
/// to go in several, and each piece is whole fragments, as Windows' own client sends them.
/// </summary>
/// <remarks>
/// A PSRP fragment (MS-PSRP 2.2.4) is an object id and a fragment id (8 bytes each), a flags byte, a big-endian blob
/// length and the blob. PowerShell fragments at 32 KiB, well within any envelope, so a fragment larger than a piece is
/// not expected; one is sent alone rather than cut, which leaves the server to refuse it if it must.
/// </remarks>
internal static class PsrpFragmentChunks
{
    private const int FragmentHeaderLength = 21;
    private const int BlobLengthOffset = 17;

    /// <summary>
    /// <paramref name="data"/> in order, in pieces of whole fragments of at most <paramref name="maxBytes"/> each. Bytes
    /// after the last whole fragment, which a caller should never hold, are kept in the last piece rather than dropped.
    /// </summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> data, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var start = 0;
        var end = 0;
        while (end < data.Length)
        {
            var next = end + FragmentLength(data.Span[end..]);
            if (next - start > maxBytes && end > start)
            {
                yield return data[start..end];
                start = end;
            }

            end = next;
        }

        if (end > start)
            yield return data[start..end];
    }

    /// <summary>The length of the fragment at the start of <paramref name="data"/>, or all of it if it is not a whole one.</summary>
    private static int FragmentLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < FragmentHeaderLength)
            return data.Length;

        var blobLength = BinaryPrimitives.ReadInt32BigEndian(data[BlobLengthOffset..]);
        return blobLength < 0 || blobLength > data.Length - FragmentHeaderLength ? data.Length : FragmentHeaderLength + blobLength;
    }
}
