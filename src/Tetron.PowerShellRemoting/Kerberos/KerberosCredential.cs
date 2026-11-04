// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tetron.PowerShellRemoting.Kerberos.Native;

namespace Tetron.PowerShellRemoting.Kerberos;

/// <summary>
/// A ticket-granting ticket obtained once with a password and held in memory by GSSAPI, from which any number of
/// security contexts are built, so a session logs in to the KDC once rather than once per connection.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/>, so each <see cref="KerberosClientContext"/> built from it holds a reference: disposing
/// the credential while a handshake is still using it defers the release until that handshake is done. MIT GSSAPI
/// locks a credential internally, so contexts may be built from it on several threads at once.
/// </remarks>
internal sealed class KerberosCredential : SafeHandleZeroOrMinusOneIsInvalid
{
    private KerberosCredential(IntPtr handle)
        : base(ownsHandle: true) => SetHandle(handle);

    /// <summary>
    /// Obtains a ticket-granting ticket for <paramref name="userPrincipalName"/>. The password is copied into native
    /// memory only for the call, then cleared.
    /// </summary>
    /// <exception cref="KerberosException">The KDC refused the credential, or the name could not be parsed.</exception>
    public static KerberosCredential Acquire(string userPrincipalName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrincipalName);
        ArgumentNullException.ThrowIfNull(password);

        var userName = KerberosClientContext.ImportName(userPrincipalName, NativeOids.KerberosPrincipalName);
        try
        {
            return new KerberosCredential(AcquireHandle(userName, password));
        }
        finally
        {
            GssapiNative.ReleaseName(out _, ref userName);
        }
    }

    protected override bool ReleaseHandle()
    {
        var credential = handle;
        GssapiNative.ReleaseCredential(out _, ref credential);
        return true;
    }

    private static IntPtr AcquireHandle(IntPtr userName, string password)
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
