// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.Sql;
using JIM.Models.Connectors;
using NUnit.Framework;
using Serilog;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How JIM reaches the certificate a Microsoft SQL Server presents, which it never offers to a bare TLS handshake.
/// <para>
/// A TDS 7.x client first agrees encryption in a PRELOGIN exchange, then carries the TLS handshake inside PRELOGIN
/// packets until the handshake completes. JIM never completes it: the probe only looks at the certificate and
/// refuses, so all that has to be right is the framing up to that point. A real SQL Server proves the same thing in
/// the <c>RequiresSqlTls</c> tier (<see cref="SqlServerTlsCertificateValidationTests"/>); these tests pin the framing
/// itself so a regression shows up in every build rather than only where a database server is standing by.
/// </para>
/// </summary>
[TestFixture]
public class TdsPreLoginTests
{
    private const byte PreLoginPacketType = 0x12;
    private const byte TabularResultPacketType = 0x04;
    private const int HeaderLength = 8;

    #region TdsPreLoginTlsStream

    [Test]
    public void Write_WrapsTheBytesInOnePreLoginPacketMarkedEndOfMessage()
    {
        var transport = new ScriptedTransport();
        using var stream = new TdsPreLoginTlsStream(transport);

        stream.Write([0x16, 0x03, 0x03]);

        var written = transport.Written.ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(written[0], Is.EqualTo(PreLoginPacketType));
            Assert.That(written[1], Is.EqualTo(0x01), "A single packet is the whole message, so it carries the end-of-message status.");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(written.AsSpan(2)), Is.EqualTo(HeaderLength + 3), "The length covers the header as well as the payload.");
            Assert.That(written[HeaderLength..], Is.EqualTo(new byte[] { 0x16, 0x03, 0x03 }));
        }
    }

    [Test]
    public void Write_MoreThanOnePacketHolds_SplitsItMarkingOnlyTheLastPacketEndOfMessage()
    {
        var transport = new ScriptedTransport();
        using var stream = new TdsPreLoginTlsStream(transport);
        var payload = RandomNumberGenerator.GetBytes(5000);

        stream.Write(payload);

        var packets = ReadPackets(transport.Written.ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(packets, Has.Count.EqualTo(2), "SQL Server's packet size before login is 4,096 bytes, header included.");
            Assert.That(packets.Select(p => p.Status), Is.EqualTo(new byte[] { 0x00, 0x01 }));
            Assert.That(packets.SelectMany(p => p.Payload), Is.EqualTo(payload));
            Assert.That(packets.Max(p => p.Payload.Length + HeaderLength), Is.LessThanOrEqualTo(4096));
        }
    }

    [Test]
    public void Read_HandshakeSpreadOverSeveralPackets_ReturnsItAsOneContiguousStream()
    {
        // A server's certificate chain routinely outgrows a single 4 KB packet.
        var first = RandomNumberGenerator.GetBytes(4000);
        var second = RandomNumberGenerator.GetBytes(1500);
        var transport = new ScriptedTransport([.. Packet(PreLoginPacketType, 0x00, first), .. Packet(PreLoginPacketType, 0x01, second)]);
        using var stream = new TdsPreLoginTlsStream(transport);

        var received = new byte[first.Length + second.Length];
        stream.ReadExactly(received);

        Assert.That(received, Is.EqualTo(first.Concat(second).ToArray()));
    }

    [Test]
    public void Read_PacketOfAnotherType_ThrowsRatherThanHandingItToTls()
    {
        // 0x01 is a SQL batch. Whatever arrived, it is not handshake data, and passing it on would have the
        // TLS layer report a garbled record instead of the protocol mismatch it really is.
        var transport = new ScriptedTransport(Packet(0x01, 0x01, [0x16]));
        using var stream = new TdsPreLoginTlsStream(transport);

        Assert.That(() => stream.ReadExactly(new byte[1]), Throws.TypeOf<IOException>());
    }

    [Test]
    public void Read_HeaderClaimingLessThanItself_Throws()
    {
        var malformed = new byte[] { PreLoginPacketType, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 };
        using var stream = new TdsPreLoginTlsStream(new ScriptedTransport(malformed));

        Assert.That(() => stream.ReadExactly(new byte[1]), Throws.TypeOf<IOException>());
    }

    #endregion

    #region TdsPreLogin.NegotiateEncryption

    [Test]
    public void NegotiateEncryption_SendsAPreLoginRequestingEncryption()
    {
        var transport = new ScriptedTransport(PreLoginResponse(TdsEncryption.On));

        TdsPreLogin.NegotiateEncryption(transport);

        var request = ReadPackets(transport.Written.ToArray()).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Type, Is.EqualTo(PreLoginPacketType));
            Assert.That(ReadOption(request.Payload, 0x00), Has.Length.EqualTo(6), "VERSION must lead the options; SQL Server rejects a PRELOGIN without it.");
            Assert.That(ReadOption(request.Payload, 0x01), Is.EqualTo(new[] { (byte)TdsEncryption.On }));
        }
    }

    // The values as MS-TDS spells them (OFF, ON, NOT_SUP, REQ); TdsEncryption is internal, so it cannot be a
    // parameter of a public test method.
    [TestCase((byte)0x00)]
    [TestCase((byte)0x01)]
    [TestCase((byte)0x02)]
    [TestCase((byte)0x03)]
    public void NegotiateEncryption_ReturnsTheServersAnswer(byte value)
    {
        var answer = (TdsEncryption)value;
        var transport = new ScriptedTransport(PreLoginResponse(answer));

        Assert.That(TdsPreLogin.NegotiateEncryption(transport), Is.EqualTo(answer));
    }

    [Test]
    public void NegotiateEncryption_ResponseWithoutAnEncryptionOption_Throws()
    {
        var payload = BuildOptions((0x00, new byte[6]));
        var transport = new ScriptedTransport(Packet(TabularResultPacketType, 0x01, payload));

        Assert.That(() => TdsPreLogin.NegotiateEncryption(transport), Throws.TypeOf<IOException>());
    }

    [Test]
    public void NegotiateEncryption_OptionPointingPastTheEndOfTheResponse_Throws()
    {
        // The table says the ENCRYPTION byte lives at offset 200 of an 11-byte payload. Trusting it would read
        // out of bounds; refusing it reports the server as not speaking TDS, which is what it amounts to.
        var payload = new byte[] { 0x01, 0x00, 0xC8, 0x00, 0x01, 0xFF, 0, 0, 0, 0, 0 };
        var transport = new ScriptedTransport(Packet(TabularResultPacketType, 0x01, payload));

        Assert.That(() => TdsPreLogin.NegotiateEncryption(transport), Throws.TypeOf<IOException>());
    }

    #endregion

    #region ServerCertificateProbe against a TDS server

    [Test]
    public void Read_TdsPreLoginFraming_ReadsTheCertificateTheServerPresents()
    {
        using var certificate = CreateServerCertificate("localhost");
        using var server = new FakeTdsServer(certificate, TdsEncryption.On);

        var reading = ServerCertificateProbe.Read("localhost", server.Port, [], TimeSpan.FromSeconds(10), new LoggerConfiguration().CreateLogger(),
            "database server", "TLS", SecureHandshakeFraming.TdsPreLogin);

        Assert.That(reading, Is.Not.Null, "The server answered, so the probe must have seen its certificate.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading!.Chain?.Leaf?.Thumbprint, Is.EqualTo(certificate.Thumbprint));
            Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer),
                "The certificate is self-signed and in no store, so it is reported as untrusted, not as a connectivity failure.");
        }
    }

    [Test]
    public void Read_DirectTlsAgainstATdsServer_SeesNothing()
    {
        // The defect this framing exists to fix: SQL Server answers a bare ClientHello by dropping the connection,
        // so the probe finds no certificate and the original "cannot connect" is all an administrator is shown.
        using var certificate = CreateServerCertificate("localhost");
        using var server = new FakeTdsServer(certificate, TdsEncryption.On);

        var reading = ServerCertificateProbe.Read("localhost", server.Port, [], TimeSpan.FromSeconds(10), new LoggerConfiguration().CreateLogger(),
            "database server", "TLS", SecureHandshakeFraming.DirectTls);

        Assert.That(reading, Is.Null);
    }

    [Test]
    public void Read_TdsServerWithoutEncryptionSupport_ReportsThatNoCertificateWasPresented()
    {
        using var certificate = CreateServerCertificate("localhost");
        using var server = new FakeTdsServer(certificate, TdsEncryption.NotSupported);

        var reading = ServerCertificateProbe.Read("localhost", server.Port, [], TimeSpan.FromSeconds(10), new LoggerConfiguration().CreateLogger(),
            "database server", "TLS", SecureHandshakeFraming.TdsPreLogin);

        Assert.That(reading?.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.NoCertificatePresented));
    }

    #endregion

    #region Helpers

    private static byte[] Packet(byte type, byte status, byte[] payload)
    {
        var packet = new byte[HeaderLength + payload.Length];
        packet[0] = type;
        packet[1] = status;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        payload.CopyTo(packet, HeaderLength);
        return packet;
    }

    private static List<(byte Type, byte Status, byte[] Payload)> ReadPackets(byte[] bytes)
    {
        var packets = new List<(byte, byte, byte[])>();
        var position = 0;
        while (position < bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2));
            packets.Add((bytes[position], bytes[position + 1], bytes[(position + HeaderLength)..(position + length)]));
            position += length;
        }

        return packets;
    }

    private static byte[] BuildOptions(params (byte Token, byte[] Data)[] options)
    {
        var tableLength = options.Length * 5 + 1;
        var payload = new List<byte>();
        var offset = tableLength;
        foreach (var (token, data) in options)
        {
            payload.Add(token);
            payload.Add((byte)(offset >> 8));
            payload.Add((byte)offset);
            payload.Add((byte)(data.Length >> 8));
            payload.Add((byte)data.Length);
            offset += data.Length;
        }

        payload.Add(0xFF);
        foreach (var (_, data) in options)
            payload.AddRange(data);

        return [.. payload];
    }

    private static byte[] ReadOption(byte[] payload, byte token)
    {
        for (var position = 0; payload[position] != 0xFF; position += 5)
        {
            if (payload[position] != token)
                continue;

            var offset = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position + 1));
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position + 3));
            return payload[offset..(offset + length)];
        }

        throw new AssertionException($"Option 0x{token:X2} is not in the PRELOGIN payload.");
    }

    private static byte[] PreLoginResponse(TdsEncryption encryption) =>
        Packet(TabularResultPacketType, 0x01, BuildOptions((0x00, new byte[6]), (0x01, [(byte)encryption])));

    private static X509Certificate2 CreateServerCertificate(string hostName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // SslStream on Linux needs the key in an exportable, persisted form to serve with it.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// A transport whose reads come from a script and whose writes are recorded.
    /// </summary>
    private sealed class ScriptedTransport(byte[]? toRead = null) : Stream
    {
        private readonly MemoryStream _toRead = new(toRead ?? []);

        internal MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _toRead.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Behaves as SQL Server does up to the end of the server's handshake flight: answers PRELOGIN, then serves TLS
    /// inside PRELOGIN packets. A bare ClientHello is answered by closing the connection, as SQL Server does.
    /// </summary>
    private sealed class FakeTdsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly TdsEncryption _encryption;
        private readonly Task _serving;

        internal FakeTdsServer(X509Certificate2 certificate, TdsEncryption encryption)
        {
            _certificate = certificate;
            _encryption = encryption;
            _listener.Start();
            _serving = Task.Run(ServeOneAsync);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task ServeOneAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var network = client.GetStream();

                var header = new byte[HeaderLength];
                await network.ReadExactlyAsync(header);
                if (header[0] != PreLoginPacketType)
                    return;

                var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - HeaderLength];
                await network.ReadExactlyAsync(payload);
                await network.WriteAsync(PreLoginResponse(_encryption));

                if (_encryption == TdsEncryption.NotSupported)
                    return;

                using var tls = new SslStream(new TdsPreLoginTlsStream(network), false);
                await tls.AuthenticateAsServerAsync(_certificate);
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // The probe refuses every certificate, so the handshake never completes.
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _serving.Wait(TimeSpan.FromSeconds(10));
        }
    }

    #endregion
}
