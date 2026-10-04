// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;

namespace JIM.Connectors.Sql;

/// <summary>
/// The PRELOGIN exchange a Microsoft SQL Server expects before it will start a TLS handshake (MS-TDS 2.2.6.5).
/// </summary>
/// <remarks>
/// JIM speaks only as much of this as it takes to reach the server's certificate, for
/// <see cref="ServerCertificateProbe"/>. Connections themselves are made by <c>Microsoft.Data.SqlClient</c>, which
/// does all of this on its own; nothing here is on the path that carries data.
/// </remarks>
internal static class TdsPreLogin
{
    internal const byte PreLoginPacketType = 0x12;
    internal const byte TabularResultPacketType = 0x04;
    internal const int HeaderLength = 8;

    /// <summary>
    /// The packet size a client uses before login negotiates one, header included; what JIM splits its own writes to.
    /// </summary>
    internal const int MaximumPacketLength = 4096;

    private const byte EndOfMessageStatus = 0x01;
    private const byte VersionToken = 0x00;
    private const byte EncryptionToken = 0x01;
    private const byte TerminatorToken = 0xFF;
    private const int OptionEntryLength = 5;

    /// <summary>
    /// Asks the server to encrypt, and returns what it answered.
    /// </summary>
    /// <param name="transport">A connection to the server on which nothing has been sent yet.</param>
    /// <exception cref="IOException">The server's answer is not a PRELOGIN response, which means whatever is listening does not speak TDS.</exception>
    internal static TdsEncryption NegotiateEncryption(Stream transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        transport.Write(BuildRequest());
        transport.Flush();

        return ReadEncryptionOption(ReadMessage(transport));
    }

    /// <summary>
    /// The smallest PRELOGIN a server accepts: VERSION, which must come first, and ENCRYPTION set to on. The
    /// version is the client's own and carries no meaning to the server, so it is left as zeroes.
    /// </summary>
    private static byte[] BuildRequest()
    {
        const int tableLength = 2 * OptionEntryLength + 1;
        const int versionLength = 6;
        const int encryptionLength = 1;

        var payload = new byte[tableLength + versionLength + encryptionLength];
        WriteOptionEntry(payload, 0, VersionToken, tableLength, versionLength);
        WriteOptionEntry(payload, OptionEntryLength, EncryptionToken, tableLength + versionLength, encryptionLength);
        payload[2 * OptionEntryLength] = TerminatorToken;
        payload[tableLength + versionLength] = (byte)TdsEncryption.On;

        var packet = new byte[HeaderLength + payload.Length];
        WriteHeader(packet, PreLoginPacketType, EndOfMessageStatus, packet.Length);
        payload.CopyTo(packet, HeaderLength);
        return packet;
    }

    private static void WriteOptionEntry(byte[] payload, int position, byte token, int offset, int length)
    {
        payload[position] = token;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(position + 1), (ushort)offset);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(position + 3), (ushort)length);
    }

    /// <summary>
    /// Writes a TDS packet header. SPID, packet number and window are all zero, which is what a client sends before
    /// login.
    /// </summary>
    internal static void WriteHeader(Span<byte> destination, byte type, byte status, int packetLength)
    {
        destination[0] = type;
        destination[1] = status;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)packetLength);
        destination[4..HeaderLength].Clear();
    }

    /// <summary>
    /// Reads a packet header, returning its type, whether it ends the message, and its payload length.
    /// </summary>
    /// <exception cref="IOException">The header is not one a TDS server would send.</exception>
    internal static (byte Type, bool EndOfMessage, int PayloadLength) ReadHeader(Stream transport)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        transport.ReadExactly(header);

        // Only the lower bound is checked. The upper one is the field's own, and a server configured with a larger
        // network packet size than the default is entitled to use it.
        var packetLength = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (packetLength < HeaderLength)
            throw new IOException($"The server sent a TDS packet header declaring {packetLength} bytes, fewer than the header itself.");

        return (header[0], (header[1] & EndOfMessageStatus) != 0, packetLength - HeaderLength);
    }

    private static byte[] ReadMessage(Stream transport)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var (type, endOfMessage, payloadLength) = ReadHeader(transport);
            if (type != TabularResultPacketType)
                throw new IOException($"The server answered PRELOGIN with a TDS packet of type 0x{type:X2} rather than a response.");

            var payload = new byte[payloadLength];
            transport.ReadExactly(payload);
            message.Write(payload);

            if (endOfMessage)
                return message.ToArray();
        }
    }

    /// <summary>
    /// Finds the ENCRYPTION option in a PRELOGIN response, checking every offset against the payload rather than
    /// trusting what the server declared.
    /// </summary>
    private static TdsEncryption ReadEncryptionOption(byte[] payload)
    {
        for (var position = 0; position < payload.Length && payload[position] != TerminatorToken; position += OptionEntryLength)
        {
            if (position + OptionEntryLength > payload.Length)
                break;

            if (payload[position] != EncryptionToken)
                continue;

            var offset = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position + 1));
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position + 3));
            if (length < 1 || offset + length > payload.Length)
                throw new IOException("The server's PRELOGIN response points its ENCRYPTION option outside the response.");

            return (TdsEncryption)payload[offset];
        }

        throw new IOException("The server's PRELOGIN response carries no ENCRYPTION option.");
    }
}
