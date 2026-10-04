// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// Carries a TLS handshake inside TDS PRELOGIN packets, which is how a TDS 7.x server expects to receive one once
/// encryption has been agreed (MS-TDS 2.2.6.5).
/// </summary>
/// <remarks>
/// Good for the handshake only. Once a handshake completes, TDS 7.x sends TLS records unwrapped, but
/// <see cref="ServerCertificateProbe"/> never completes one: it looks at the certificate and refuses, so nothing here
/// ever needs to switch over.
/// </remarks>
internal sealed class TdsPreLoginTlsStream : Stream
{
    private const int MaximumPayloadLength = TdsPreLogin.MaximumPacketLength - TdsPreLogin.HeaderLength;

    private readonly Stream _transport;

    /// <summary>
    /// What is left unread of the packet currently being read.
    /// </summary>
    private int _remainingInPacket;

    internal TdsPreLoginTlsStream(Stream transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (count == 0)
            return 0;

        // An empty packet carries nothing, so keep reading until one does.
        while (_remainingInPacket == 0)
        {
            var (type, _, payloadLength) = TdsPreLogin.ReadHeader(_transport);

            // SQL Server answers handshake data with PRELOGIN packets; anything else means the exchange has gone
            // somewhere this stream cannot follow, and handing it to TLS would report a garbled record instead.
            if (type != TdsPreLogin.PreLoginPacketType && type != TdsPreLogin.TabularResultPacketType)
                throw new IOException($"The server sent a TDS packet of type 0x{type:X2} during the TLS handshake.");

            _remainingInPacket = payloadLength;
        }

        var read = _transport.Read(buffer, offset, Math.Min(count, _remainingInPacket));
        if (read == 0)
            throw new EndOfStreamException("The server closed the connection part way through a TDS packet.");

        _remainingInPacket -= read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        var header = new byte[TdsPreLogin.HeaderLength];
        do
        {
            var payloadLength = Math.Min(count, MaximumPayloadLength);
            var isLast = payloadLength == count;

            TdsPreLogin.WriteHeader(header, TdsPreLogin.PreLoginPacketType, isLast ? (byte)0x01 : (byte)0x00, TdsPreLogin.HeaderLength + payloadLength);
            _transport.Write(header);
            _transport.Write(buffer, offset, payloadLength);

            offset += payloadLength;
            count -= payloadLength;
        }
        while (count > 0);

        _transport.Flush();
    }

    public override void Flush() => _transport.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
