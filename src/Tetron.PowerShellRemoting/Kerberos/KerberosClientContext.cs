// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;
using System.Text;
using Tetron.PowerShellRemoting.Encryption;
using Tetron.PowerShellRemoting.Kerberos.Native;

namespace Tetron.PowerShellRemoting.Kerberos;

/// <summary>
/// A Kerberos client security context with a service, built from a user principal and password (no keytab), and the
/// message sealer WinRM's <c>HTTP-Kerberos-session-encrypted</c> framing needs once the context is established.
/// </summary>
/// <remarks>
/// MIT GSSAPI only. The credential is held in memory by GSSAPI for the life of this object, and the password is
/// copied into native memory only for the duration of the call that uses it, then cleared.
/// </remarks>
public sealed class KerberosClientContext : IWinRMMessageSealer, IDisposable
{
    private const uint RequestFlags = GssapiNative.GSS_C_MUTUAL_FLAG | GssapiNative.GSS_C_REPLAY_FLAG
        | GssapiNative.GSS_C_SEQUENCE_FLAG | GssapiNative.GSS_C_CONF_FLAG | GssapiNative.GSS_C_INTEG_FLAG;

    private IntPtr _credential;
    private IntPtr _targetName;
    private IntPtr _context;

    private KerberosClientContext(IntPtr credential, IntPtr targetName)
    {
        _credential = credential;
        _targetName = targetName;
    }

    /// <summary>True once the service has authenticated back (mutual authentication) and messages can be sealed.</summary>
    public bool IsEstablished { get; private set; }

    /// <summary>
    /// Obtains a ticket-granting ticket for <paramref name="userPrincipalName"/> with <paramref name="password"/> and
    /// prepares a context with the service <c>serviceClass/serviceHost</c> (for WinRM, <c>HTTP/host</c>).
    /// </summary>
    /// <exception cref="KerberosException">The KDC refused the credential, or the names could not be parsed.</exception>
    public static KerberosClientContext Create(string userPrincipalName, string password, string serviceHost, string serviceClass = "HTTP")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrincipalName);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceClass);

        var userName = ImportName(userPrincipalName, NativeOids.KerberosPrincipalName);
        try
        {
            var credential = AcquireCredential(userName, password);
            try
            {
                var targetName = ImportName($"{serviceClass}@{serviceHost}", NativeOids.HostBasedServiceName);
                return new KerberosClientContext(credential, targetName);
            }
            catch
            {
                GssapiNative.ReleaseCredential(out _, ref credential);
                throw;
            }
        }
        finally
        {
            GssapiNative.ReleaseName(out _, ref userName);
        }
    }

    /// <summary>
    /// Advances the handshake. Call first with an empty token; send what it returns to the service; call again with
    /// the service's reply until <see cref="IsEstablished"/>. Returns <c>null</c> when there is nothing to send.
    /// </summary>
    public byte[]? Step(ReadOnlySpan<byte> serviceToken)
    {
        ObjectDisposedException.ThrowIf(_credential == IntPtr.Zero, this);
        if (IsEstablished)
            throw new InvalidOperationException("The Kerberos context is already established.");

        unsafe
        {
            fixed (byte* input = serviceToken)
            {
                var inputBuffer = new GssapiNative.GssBuffer { Length = (nuint)serviceToken.Length, Value = (IntPtr)input };
                var mechanism = NativeOids.KerberosMechanism;
                var major = GssapiNative.InitSecurityContext(out var minor, _credential, ref _context, _targetName, ref mechanism,
                    RequestFlags, 0, IntPtr.Zero, ref inputBuffer, IntPtr.Zero, out var output, out var returnedFlags, IntPtr.Zero);
                try
                {
                    GssapiStatus.ThrowIfError("gss_init_sec_context", major, minor);
                    if (major == GssapiNative.GSS_S_COMPLETE)
                    {
                        RequireFlags(returnedFlags);
                        IsEstablished = true;
                    }

                    return output.Length == 0 ? null : new ReadOnlySpan<byte>((void*)output.Value, checked((int)output.Length)).ToArray();
                }
                finally
                {
                    GssapiNative.ReleaseBuffer(out _, ref output);
                }
            }
        }
    }

    /// <inheritdoc />
    public SealedMessage Wrap(ReadOnlySpan<byte> plaintext)
    {
        RequireEstablished();
        return GssapiMessageSealing.Wrap(_context, plaintext);
    }

    /// <inheritdoc />
    public byte[] Unwrap(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data)
    {
        RequireEstablished();
        return GssapiMessageSealing.Unwrap(_context, header, data);
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
            GssapiNative.DeleteSecurityContext(out _, ref _context, IntPtr.Zero);
        if (_targetName != IntPtr.Zero)
            GssapiNative.ReleaseName(out _, ref _targetName);
        if (_credential != IntPtr.Zero)
            GssapiNative.ReleaseCredential(out _, ref _credential);
    }

    private void RequireEstablished()
    {
        ObjectDisposedException.ThrowIf(_credential == IntPtr.Zero, this);
        if (!IsEstablished)
            throw new InvalidOperationException("The Kerberos context is not established yet.");
    }

    private static void RequireFlags(uint returnedFlags)
    {
        const uint required = GssapiNative.GSS_C_MUTUAL_FLAG | GssapiNative.GSS_C_CONF_FLAG | GssapiNative.GSS_C_INTEG_FLAG;
        if ((returnedFlags & required) != required)
            throw new KerberosException("gss_init_sec_context", 0, 0,
                "the service did not agree to mutual authentication with confidentiality and integrity");
    }

    private static IntPtr ImportName(string name, GssapiNative.GssOid nameType)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                var buffer = new GssapiNative.GssBuffer { Length = (nuint)bytes.Length, Value = (IntPtr)pointer };
                var major = GssapiNative.ImportName(out var minor, ref buffer, ref nameType, out var output);
                GssapiStatus.ThrowIfError("gss_import_name", major, minor);
                return output;
            }
        }
    }

    private static IntPtr AcquireCredential(IntPtr userName, string password)
    {
        var mechanisms = IntPtr.Zero;
        var length = Encoding.UTF8.GetByteCount(password);
        var native = Marshal.AllocHGlobal(Math.Max(length, 1));
        try
        {
            unsafe
            {
                var passwordBytes = new Span<byte>((void*)native, length);
                Encoding.UTF8.GetBytes(password, passwordBytes);
                var buffer = new GssapiNative.GssBuffer { Length = (nuint)length, Value = native };

                var major = GssapiNative.CreateEmptyOidSet(out var minor, out mechanisms);
                GssapiStatus.ThrowIfError("gss_create_empty_oid_set", major, minor);
                var mechanism = NativeOids.KerberosMechanism;
                major = GssapiNative.AddOidSetMember(out minor, ref mechanism, ref mechanisms);
                GssapiStatus.ThrowIfError("gss_add_oid_set_member", major, minor);

                major = GssapiNative.AcquireCredentialWithPassword(out minor, userName, ref buffer, GssapiNative.GSS_C_INDEFINITE,
                    mechanisms, GssapiNative.GSS_C_INITIATE, out var credential, IntPtr.Zero, IntPtr.Zero);
                GssapiStatus.ThrowIfError("gss_acquire_cred_with_password", major, minor);
                return credential;
            }
        }
        finally
        {
            unsafe
            {
                new Span<byte>((void*)native, length).Clear();
            }

            Marshal.FreeHGlobal(native);
            if (mechanisms != IntPtr.Zero)
                GssapiNative.ReleaseOidSet(out _, ref mechanisms);
        }
    }
}
