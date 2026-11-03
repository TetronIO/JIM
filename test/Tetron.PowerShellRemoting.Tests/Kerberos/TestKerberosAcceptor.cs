// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Encryption;
using Tetron.PowerShellRemoting.Kerberos.Native;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

/// <summary>
/// The service side of a Kerberos context, from the keytab in <c>KRB5_KTNAME</c>: what a WinRM server does, so the
/// client context can be tested against a real KDC without one.
/// </summary>
internal sealed class TestKerberosAcceptor : IWinRMMessageSealer, IDisposable
{
    private IntPtr _credential;
    private IntPtr _context;

    public TestKerberosAcceptor()
    {
        var major = GssapiNative.AcquireCredential(out var minor, IntPtr.Zero, GssapiNative.GSS_C_INDEFINITE, IntPtr.Zero,
            GssapiNative.GSS_C_ACCEPT, out _credential, IntPtr.Zero, IntPtr.Zero);
        GssapiStatus.ThrowIfError("gss_acquire_cred (accept)", major, minor);
    }

    public bool IsEstablished { get; private set; }

    public unsafe byte[]? Accept(byte[] clientToken)
    {
        fixed (byte* input = clientToken)
        {
            var buffer = new GssapiNative.GssBuffer { Length = (nuint)clientToken.Length, Value = (IntPtr)input };
            var major = GssapiNative.AcceptSecurityContext(out var minor, ref _context, _credential, ref buffer, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, out var output, out _, IntPtr.Zero, IntPtr.Zero);
            try
            {
                GssapiStatus.ThrowIfError("gss_accept_sec_context", major, minor);
                IsEstablished = major == GssapiNative.GSS_S_COMPLETE;
                return output.Length == 0 ? null : new ReadOnlySpan<byte>((void*)output.Value, checked((int)output.Length)).ToArray();
            }
            finally
            {
                GssapiNative.ReleaseBuffer(out _, ref output);
            }
        }
    }

    public SealedMessage Wrap(ReadOnlySpan<byte> plaintext) => GssapiMessageSealing.Wrap(_context, plaintext);

    public byte[] Unwrap(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data) => GssapiMessageSealing.Unwrap(_context, header, data);

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
            GssapiNative.DeleteSecurityContext(out _, ref _context, IntPtr.Zero);
        if (_credential != IntPtr.Zero)
            GssapiNative.ReleaseCredential(out _, ref _credential);
    }
}
