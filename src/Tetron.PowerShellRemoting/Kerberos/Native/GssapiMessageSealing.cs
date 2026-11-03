// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Encryption;

namespace Tetron.PowerShellRemoting.Kerberos.Native;

/// <summary>
/// WinRM message encryption over an established GSSAPI context: the token header and the encrypted data are produced
/// and consumed as separate buffers (<c>gss_wrap_iov</c>, <c>gss_unwrap_iov</c>), which a single-buffer wrap cannot do.
/// </summary>
internal static class GssapiMessageSealing
{
    internal static unsafe SealedMessage Wrap(IntPtr context, ReadOnlySpan<byte> plaintext)
    {
        var data = plaintext.ToArray();
        fixed (byte* dataPointer = data)
        {
            var iov = stackalloc GssapiNative.GssIovBuffer[3];
            iov[0].Type = GssapiNative.GSS_IOV_BUFFER_TYPE_HEADER | GssapiNative.GSS_IOV_BUFFER_FLAG_ALLOCATE;
            iov[1].Type = GssapiNative.GSS_IOV_BUFFER_TYPE_DATA;
            iov[1].Buffer = new GssapiNative.GssBuffer { Length = (nuint)data.Length, Value = (IntPtr)dataPointer };
            iov[2].Type = GssapiNative.GSS_IOV_BUFFER_TYPE_PADDING | GssapiNative.GSS_IOV_BUFFER_FLAG_ALLOCATE;
            try
            {
                var major = GssapiNative.WrapIov(out var minor, context, 1, 0, out var confidential, iov, 3);
                GssapiStatus.ThrowIfError("gss_wrap_iov", major, minor);
                if (confidential == 0)
                    throw new WinRMEncryptionException("Kerberos wrapped the message without encrypting it; it was not sent.");

                var header = new ReadOnlySpan<byte>((void*)iov[0].Buffer.Value, checked((int)iov[0].Buffer.Length)).ToArray();
                return new SealedMessage(header, data, checked((int)iov[2].Buffer.Length));
            }
            finally
            {
                GssapiNative.ReleaseIovBuffer(out _, iov, 3);
            }
        }
    }

    internal static unsafe byte[] Unwrap(IntPtr context, ReadOnlySpan<byte> header, ReadOnlySpan<byte> data)
    {
        var headerCopy = header.ToArray();
        var dataCopy = data.ToArray();
        fixed (byte* headerPointer = headerCopy)
        fixed (byte* dataPointer = dataCopy)
        {
            var iov = stackalloc GssapiNative.GssIovBuffer[2];
            iov[0].Type = GssapiNative.GSS_IOV_BUFFER_TYPE_HEADER;
            iov[0].Buffer = new GssapiNative.GssBuffer { Length = (nuint)headerCopy.Length, Value = (IntPtr)headerPointer };
            iov[1].Type = GssapiNative.GSS_IOV_BUFFER_TYPE_DATA;
            iov[1].Buffer = new GssapiNative.GssBuffer { Length = (nuint)dataCopy.Length, Value = (IntPtr)dataPointer };

            var major = GssapiNative.UnwrapIov(out var minor, context, out var confidential, out _, iov, 2);
            if (GssapiNative.IsError(major))
                throw new WinRMEncryptionException(
                    $"The WinRM message failed Kerberos decryption or integrity checking: {GssapiStatus.Describe(major, minor)}.");
            if (confidential == 0)
                throw new WinRMEncryptionException("The WinRM message was signed but not encrypted; it is refused.");

            return dataCopy;
        }
    }
}
