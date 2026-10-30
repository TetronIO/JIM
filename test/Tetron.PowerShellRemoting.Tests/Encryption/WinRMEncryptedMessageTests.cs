// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;
using System.Text;
using Tetron.PowerShellRemoting.Encryption;

namespace Tetron.PowerShellRemoting.Tests.Encryption;

public class WinRMEncryptedMessageTests
{
    private const string Boundary = "Encrypted Boundary";
    private const string Protocol = "application/HTTP-Kerberos-session-encrypted";

    [Test]
    public void Encode_AnyMessage_DeclaresKerberosMultipartEncryptedContent()
    {
        var encoded = WinRMEncryptedMessage.Encode("<s:Envelope/>"u8, new FakeSealer());

        Assert.That(encoded.ContentType,
            Is.EqualTo("multipart/encrypted;protocol=\"application/HTTP-Kerberos-session-encrypted\";boundary=\"Encrypted Boundary\""));
    }

    [Test]
    public void Encode_AnyMessage_LaysOutBothPartsAsWinRMExpects()
    {
        var plaintext = "<s:Envelope>hello</s:Envelope>"u8.ToArray();
        var sealer = new FakeSealer();

        var body = WinRMEncryptedMessage.Encode(plaintext, sealer).Body;

        var sealedMessage = sealer.Wrap(plaintext);
        var expected = Concat(
            Encoding.ASCII.GetBytes(
                $"--{Boundary}\r\n" +
                $"\tContent-Type: {Protocol}\r\n" +
                $"\tOriginalContent: type=application/soap+xml;charset=UTF-8;Length={plaintext.Length}\r\n" +
                $"--{Boundary}\r\n" +
                "\tContent-Type: application/octet-stream\r\n"),
            LittleEndian(sealedMessage.Header.Length),
            sealedMessage.Header,
            sealedMessage.Data,
            Encoding.ASCII.GetBytes($"--{Boundary}--\r\n"));
        Assert.That(body, Is.EqualTo(expected));
    }

    [Test]
    public void Encode_SealerReportsPadding_CountsItInTheOriginalLength()
    {
        var body = WinRMEncryptedMessage.Encode("abc"u8, new FakeSealer(paddingLength: 5)).Body;

        Assert.That(Encoding.ASCII.GetString(body), Does.Contain(";Length=8\r\n"));
    }

    [Test]
    public void Decode_WhatEncodeProduced_ReturnsTheOriginalMessage()
    {
        var plaintext = Encoding.UTF8.GetBytes("<s:Envelope>" + new string('x', 70_000) + "</s:Envelope>");
        var sealer = new FakeSealer();
        var encoded = WinRMEncryptedMessage.Encode(plaintext, sealer);

        var decoded = WinRMEncryptedMessage.Decode(encoded.Body, encoded.ContentType, sealer);

        Assert.That(decoded, Is.EqualTo(plaintext));
    }

    [Test]
    public void Decode_EncryptedDataContainingTheBoundaryText_StillReturnsTheWholeMessage()
    {
        // The ciphertext is arbitrary bytes, so it can contain the boundary text itself.
        var plaintext = Encoding.ASCII.GetBytes($"before--{Boundary}--\r\nafter");
        var sealer = new FakeSealer(identity: true);
        var encoded = WinRMEncryptedMessage.Encode(plaintext, sealer);

        var decoded = WinRMEncryptedMessage.Decode(encoded.Body, encoded.ContentType, sealer);

        Assert.That(decoded, Is.EqualTo(plaintext));
    }

    [Test]
    public void Decode_ContentTypeParametersInAnyCaseOrOrder_AreAccepted()
    {
        var sealer = new FakeSealer();
        var encoded = WinRMEncryptedMessage.Encode("<x/>"u8, sealer);

        var decoded = WinRMEncryptedMessage.Decode(encoded.Body,
            "Multipart/Encrypted; Boundary=\"Encrypted Boundary\"; Protocol=\"application/HTTP-Kerberos-session-encrypted\"", sealer);

        Assert.That(decoded, Is.EqualTo("<x/>"u8.ToArray()));
    }

    [TestCase("application/soap+xml;charset=UTF-8")]
    [TestCase("multipart/encrypted;protocol=\"application/HTTP-SPNEGO-session-encrypted\";boundary=\"Encrypted Boundary\"")]
    [TestCase("")]
    public void Decode_ResponseThatIsNotKerberosEncrypted_IsRefused(string contentType)
    {
        Assert.That(() => WinRMEncryptedMessage.Decode("<s:Envelope/>"u8.ToArray(), contentType, new FakeSealer()),
            Throws.TypeOf<WinRMEncryptionException>());
    }

    [Test]
    public void Decode_LengthDisagreesWithTheDecryptedMessage_IsRefused()
    {
        var sealer = new FakeSealer();
        var encoded = WinRMEncryptedMessage.Encode("<s:Envelope/>"u8, sealer);
        var tampered = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(encoded.Body).Replace(";Length=13\r\n", ";Length=14\r\n"));

        Assert.That(() => WinRMEncryptedMessage.Decode(tampered, encoded.ContentType, sealer),
            Throws.TypeOf<WinRMEncryptionException>().With.Message.Contains("length"));
    }

    [Test]
    public void Decode_HeaderLengthPastTheEndOfThePayload_IsRefused()
    {
        var sealer = new FakeSealer();
        var encoded = WinRMEncryptedMessage.Encode("<s:Envelope/>"u8, sealer);
        var body = encoded.Body.ToArray();
        var marker = Encoding.ASCII.GetBytes("application/octet-stream\r\n");
        var lengthOffset = IndexOf(body, marker) + marker.Length;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(lengthOffset), 1_000_000);

        Assert.That(() => WinRMEncryptedMessage.Decode(body, encoded.ContentType, sealer), Throws.TypeOf<WinRMEncryptionException>());
    }

    [Test]
    public void Decode_TruncatedBody_IsRefused()
    {
        var sealer = new FakeSealer();
        var encoded = WinRMEncryptedMessage.Encode("<s:Envelope/>"u8, sealer);

        Assert.That(() => WinRMEncryptedMessage.Decode(encoded.Body.AsSpan(0, encoded.Body.Length / 2).ToArray(), encoded.ContentType, sealer),
            Throws.TypeOf<WinRMEncryptionException>());
    }

    private static byte[] LittleEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    /// <summary>
    /// Stands in for Kerberos: a fixed 12-byte header, and data XORed with a constant (or left alone, so tests can
    /// control the ciphertext exactly).
    /// </summary>
    private sealed class FakeSealer(int paddingLength = 0, bool identity = false) : IWinRMMessageSealer
    {
        private static readonly byte[] FixedHeader = "HDR-12-BYTES"u8.ToArray();

        public SealedMessage Wrap(ReadOnlySpan<byte> plaintext) =>
            new(FixedHeader, plaintext.ToArray().Select(b => identity ? b : (byte)(b ^ 0x5A)).ToArray(), paddingLength);

        public byte[] Unwrap(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data)
        {
            if (!header.SequenceEqual(FixedHeader))
                throw new WinRMEncryptionException("bad header");
            return data.ToArray().Select(b => identity ? b : (byte)(b ^ 0x5A)).ToArray();
        }
    }
}
