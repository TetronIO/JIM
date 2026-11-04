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

    private readonly KerberosCredential _credential;
    private readonly bool _ownsCredential;
    private bool _credentialReferenced;
    private IntPtr _targetName;
    private IntPtr _context;
    private bool _disposed;

    private KerberosClientContext(KerberosCredential credential, bool ownsCredential, IntPtr targetName)
    {
        _credential = credential;
        _ownsCredential = ownsCredential;
        _targetName = targetName;
        // Holds the credential's handle for the life of this context, however soon its owner disposes of it.
        credential.DangerousAddRef(ref _credentialReferenced);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceClass);

        var credential = KerberosCredential.Acquire(userPrincipalName, password);
        try
        {
            return Create(credential, ownsCredential: true, serviceHost, serviceClass);
        }
        catch
        {
            credential.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prepares a context with the service <c>serviceClass/serviceHost</c> from a credential already obtained, so no
    /// new login is needed. The context does not own the credential.
    /// </summary>
    internal static KerberosClientContext Create(KerberosCredential credential, string serviceHost, string serviceClass = "HTTP") =>
        Create(credential, ownsCredential: false, serviceHost, serviceClass);

    /// <summary>
    /// Advances the handshake. Call first with an empty token; send what it returns to the service; call again with
    /// the service's reply until <see cref="IsEstablished"/>. Returns <c>null</c> when there is nothing to send.
    /// </summary>
    public byte[]? Step(ReadOnlySpan<byte> serviceToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEstablished)
            throw new InvalidOperationException("The Kerberos context is already established.");

        unsafe
        {
            fixed (byte* input = serviceToken)
            {
                var inputBuffer = new GssapiNative.GssBuffer { Length = (nuint)serviceToken.Length, Value = (IntPtr)input };
                var mechanism = NativeOids.KerberosMechanism;
                var major = GssapiNative.InitSecurityContext(out var minor, _credential.DangerousGetHandle(), ref _context, _targetName, ref mechanism,
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
        if (_disposed)
            return;

        _disposed = true;
        if (_context != IntPtr.Zero)
            GssapiNative.DeleteSecurityContext(out _, ref _context, IntPtr.Zero);
        if (_targetName != IntPtr.Zero)
            GssapiNative.ReleaseName(out _, ref _targetName);
        if (_credentialReferenced)
        {
            _credentialReferenced = false;
            _credential.DangerousRelease();
        }

        if (_ownsCredential)
            _credential.Dispose();
    }

    private static KerberosClientContext Create(KerberosCredential credential, bool ownsCredential, string serviceHost, string serviceClass)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceClass);

        var targetName = ImportName($"{serviceClass}@{serviceHost}", NativeOids.HostBasedServiceName);
        try
        {
            return new KerberosClientContext(credential, ownsCredential, targetName);
        }
        catch
        {
            GssapiNative.ReleaseName(out _, ref targetName);
            throw;
        }
    }

    private void RequireEstablished()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    internal static IntPtr ImportName(string name, GssapiNative.GssOid nameType)
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
}
