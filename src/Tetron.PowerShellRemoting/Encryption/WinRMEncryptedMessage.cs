// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Tetron.PowerShellRemoting.Encryption;

/// <summary>
/// The WinRM <c>HTTP-Kerberos-session-encrypted</c> message framing (MS-WSMV 2.2.9.1): a two-part
/// <c>multipart/encrypted</c> body whose first part declares the original content and length, and whose second
/// carries the token header length (four bytes, little-endian), the token header and the encrypted data.
/// </summary>
public static class WinRMEncryptedMessage
{
    /// <summary>The encryption protocol WinRM names for Kerberos message encryption.</summary>
    public const string KerberosProtocol = "application/HTTP-Kerberos-session-encrypted";

    private const string Boundary = "Encrypted Boundary";
    private const string OriginalContentType = "application/soap+xml;charset=UTF-8";

    private static readonly byte[] DataPartMarker =
        Encoding.ASCII.GetBytes($"--{Boundary}\r\n\tContent-Type: application/octet-stream\r\n");

    private static readonly byte[] EndMarker = Encoding.ASCII.GetBytes($"--{Boundary}--");

    /// <summary>Encrypts <paramref name="plaintext"/> (a SOAP envelope) into a WinRM encrypted request body.</summary>
    public static EncodedMessage Encode(ReadOnlySpan<byte> plaintext, IWinRMMessageSealer sealer)
    {
        ArgumentNullException.ThrowIfNull(sealer);
        var sealedMessage = sealer.Wrap(plaintext);

        var originalLength = plaintext.Length + sealedMessage.PaddingLength;
        var description = Encoding.ASCII.GetBytes(
            $"--{Boundary}\r\n" +
            $"\tContent-Type: {KerberosProtocol}\r\n" +
            $"\tOriginalContent: type={OriginalContentType};Length={originalLength.ToString(CultureInfo.InvariantCulture)}\r\n");
        var terminator = Encoding.ASCII.GetBytes($"--{Boundary}--\r\n");

        var body = new byte[description.Length + DataPartMarker.Length + 4 + sealedMessage.Header.Length + sealedMessage.Data.Length + terminator.Length];
        var span = body.AsSpan();
        description.CopyTo(span);
        span = span[description.Length..];
        DataPartMarker.CopyTo(span);
        span = span[DataPartMarker.Length..];
        BinaryPrimitives.WriteInt32LittleEndian(span, sealedMessage.Header.Length);
        span = span[4..];
        sealedMessage.Header.CopyTo(span);
        span = span[sealedMessage.Header.Length..];
        sealedMessage.Data.CopyTo(span);
        span = span[sealedMessage.Data.Length..];
        terminator.CopyTo(span);

        return new EncodedMessage(body, $"multipart/encrypted;protocol=\"{KerberosProtocol}\";boundary=\"{Boundary}\"");
    }

    /// <summary>
    /// Decrypts a WinRM encrypted response body. Anything that is not a Kerberos-encrypted message is refused, so an
    /// unencrypted response can never be accepted in place of an encrypted one.
    /// </summary>
    /// <exception cref="WinRMEncryptionException">The response is not Kerberos-encrypted, is malformed, or fails to decrypt.</exception>
    public static byte[] Decode(byte[] body, string contentType, IWinRMMessageSealer sealer)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sealer);
        RequireKerberosContentType(contentType);

        var dataPartStart = body.AsSpan().IndexOf(DataPartMarker);
        if (dataPartStart < 0)
            throw new WinRMEncryptionException("The encrypted WinRM response has no data part.");

        var expectedLength = ReadOriginalLength(body.AsSpan(0, dataPartStart));

        var payloadStart = dataPartStart + DataPartMarker.Length;
        var payloadEnd = body.AsSpan().LastIndexOf(EndMarker);
        if (payloadEnd < payloadStart + 4)
            throw new WinRMEncryptionException("The encrypted WinRM response is truncated: its closing boundary is missing.");

        var payload = body.AsSpan(payloadStart, payloadEnd - payloadStart);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (headerLength < 0 || headerLength > payload.Length - 4)
            throw new WinRMEncryptionException("The encrypted WinRM response declares a token header longer than the message.");

        var plaintext = sealer.Unwrap(payload.Slice(4, headerLength), payload[(4 + headerLength)..]);
        if (plaintext.Length != expectedLength)
            throw new WinRMEncryptionException(
                $"The decrypted WinRM response is {plaintext.Length} bytes, but its declared length is {expectedLength}.");

        return plaintext;
    }

    private static void RequireKerberosContentType(string contentType)
    {
        var parts = (contentType ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var isMultipartEncrypted = parts.Length > 0 && parts[0].Equals("multipart/encrypted", StringComparison.OrdinalIgnoreCase);
        var protocol = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("protocol", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim().Trim('"'))
            .FirstOrDefault();

        if (!isMultipartEncrypted || !KerberosProtocol.Equals(protocol, StringComparison.OrdinalIgnoreCase))
            throw new WinRMEncryptionException(
                $"The WinRM response was not Kerberos-encrypted (Content-Type '{contentType}'); it is refused rather than read unencrypted.");
    }

    private static int ReadOriginalLength(ReadOnlySpan<byte> descriptionPart)
    {
        var text = Encoding.ASCII.GetString(descriptionPart);
        const string key = ";Length=";
        var start = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new WinRMEncryptionException("The encrypted WinRM response does not declare its original length.");

        start += key.Length;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;

        if (!int.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw new WinRMEncryptionException("The encrypted WinRM response declares an unreadable original length.");

        return length;
    }
}
