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

    private const string DataPartContentType = "application/octet-stream\r\n";

    /// <summary>How far past the data part's boundary its header line may end.</summary>
    private const int MaximumPartHeaderLength = 256;

    /// <summary>How much of an unreadable response an error shows.</summary>
    private const int ExcerptLength = 240;

    private static readonly byte[] DataPartMarker =
        Encoding.ASCII.GetBytes($"--{Boundary}\r\n\tContent-Type: application/octet-stream\r\n");

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
    /// unencrypted response can never be accepted in place of an encrypted one. The framing is read as WinRM servers
    /// write it, not only as the WinRM service does: the boundary is the one the Content-Type names, and part headers
    /// may or may not be indented.
    /// </summary>
    /// <exception cref="WinRMEncryptionException">The response is not Kerberos-encrypted, is malformed, or fails to decrypt.</exception>
    public static byte[] Decode(byte[] body, string contentType, IWinRMMessageSealer sealer)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sealer);
        var boundary = RequireKerberosContentType(contentType);
        if (body.Length == 0)
            throw new WinRMEncryptionException("The encrypted WinRM response is empty: it has no body at all.");

        var span = body.AsSpan();
        var partStart = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
        var descriptionStart = span.IndexOf(partStart);
        var dataPartStart = descriptionStart < 0 ? -1 : IndexOfAfter(span, partStart, descriptionStart + partStart.Length);
        if (dataPartStart < 0)
            throw Malformed("has no data part", body);

        var expectedLength = ReadOriginalLength(span[descriptionStart..dataPartStart], body);

        // The data part's own header names application/octet-stream; the encrypted payload follows its line directly.
        var headerStart = dataPartStart + partStart.Length;
        var headerWindow = Encoding.Latin1.GetString(span.Slice(headerStart, Math.Min(MaximumPartHeaderLength, span.Length - headerStart)));
        var headerEnd = headerWindow.IndexOf(DataPartContentType, StringComparison.OrdinalIgnoreCase);
        if (headerEnd < 0)
            throw Malformed("has no application/octet-stream data part", body);

        var payloadStart = headerStart + headerEnd + DataPartContentType.Length;
        var payloadEnd = span.LastIndexOf(Encoding.ASCII.GetBytes($"--{boundary}--"));
        if (payloadEnd < payloadStart + 4)
            throw Malformed("is truncated: its closing boundary is missing", body);

        var payload = span[payloadStart..payloadEnd];
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (headerLength < 0 || headerLength > payload.Length - 4)
            throw new WinRMEncryptionException("The encrypted WinRM response declares a token header longer than the message.");

        var plaintext = sealer.Unwrap(payload.Slice(4, headerLength), payload[(4 + headerLength)..]);
        if (plaintext.Length != expectedLength)
            throw new WinRMEncryptionException(
                $"The decrypted WinRM response is {plaintext.Length} bytes, but its declared length is {expectedLength}.");

        return plaintext;
    }

    /// <summary>
    /// Reports a response this client cannot read, with enough of it to see why: its length and the start of its
    /// framing, which is plain text (the encrypted payload that may follow it is unreadable without the session key).
    /// </summary>
    private static WinRMEncryptionException Malformed(string problem, byte[] body) =>
        new($"The encrypted WinRM response {problem}. It was {body.Length} bytes, beginning: {Excerpt(body)}");

    private static string Excerpt(byte[] body)
    {
        var text = new StringBuilder();
        foreach (var b in body.AsSpan(0, Math.Min(body.Length, ExcerptLength)))
        {
            text.Append(b switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n",
                (byte)'\t' => "\\t",
                >= 0x20 and < 0x7F => ((char)b).ToString(),
                _ => $"\\x{b:X2}"
            });
        }

        return body.Length > ExcerptLength ? text.Append('…').ToString() : text.ToString();
    }

    private static int IndexOfAfter(ReadOnlySpan<byte> span, ReadOnlySpan<byte> value, int from)
    {
        var index = span[from..].IndexOf(value);
        return index < 0 ? -1 : from + index;
    }

    /// <returns>The boundary the Content-Type names, or WinRM's own when it names none.</returns>
    private static string RequireKerberosContentType(string contentType)
    {
        var parts = (contentType ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var isMultipartEncrypted = parts.Length > 0 && parts[0].Equals("multipart/encrypted", StringComparison.OrdinalIgnoreCase);
        var parameters = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .Select(kv => (Name: kv[0].Trim(), Value: kv[1].Trim().Trim('"')))
            .ToList();
        var protocol = parameters.FirstOrDefault(p => p.Name.Equals("protocol", StringComparison.OrdinalIgnoreCase)).Value;

        if (!isMultipartEncrypted || !KerberosProtocol.Equals(protocol, StringComparison.OrdinalIgnoreCase))
            throw new WinRMEncryptionException(
                $"The WinRM response was not Kerberos-encrypted (Content-Type '{contentType}'); it is refused rather than read unencrypted.");

        var boundary = parameters.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrEmpty(boundary) ? Boundary : boundary;
    }

    private static int ReadOriginalLength(ReadOnlySpan<byte> descriptionPart, byte[] body)
    {
        var text = Encoding.ASCII.GetString(descriptionPart);
        const string key = ";Length=";
        var start = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw Malformed("does not declare its original length", body);

        start += key.Length;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;

        if (!int.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw new WinRMEncryptionException("The encrypted WinRM response declares an unreadable original length.");

        return length;
    }
}
