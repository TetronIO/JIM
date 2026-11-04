// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;

namespace Tetron.PowerShellRemoting.Kerberos.Native;

/// <summary>
/// The MIT GSSAPI calls the library needs (RFC 2743/2744, and MIT's IOV extensions for WinRM message encryption).
/// Linux only: Windows uses SSPI, which this library does not target.
/// </summary>
internal static partial class GssapiNative
{
    private const string Library = "libgssapi_krb5.so.2";

    // Major status: the routine-error and calling-error fields; any bit set there is a failure.
    internal const uint GSS_S_COMPLETE = 0;
    internal const uint GSS_S_CONTINUE_NEEDED = 1;
    internal const uint GssErrorMask = 0xFFFF0000;
    internal const uint GssRoutineErrorMask = 0x00FF0000;
    internal const uint GSS_S_CREDENTIALS_EXPIRED = 11 << 16;

    // MIT krb5's error table code for an expired ticket (ERROR_TABLE_BASE_krb5 + 32), as GSSAPI's minor status.
    internal const uint KRB5KRB_AP_ERR_TKT_EXPIRED = 0x96C73A20;

    internal const int GSS_C_INITIATE = 1;
    internal const int GSS_C_ACCEPT = 2;
    internal const int GSS_C_GSS_CODE = 1;
    internal const int GSS_C_MECH_CODE = 2;
    internal const uint GSS_C_INDEFINITE = 0xFFFFFFFF;

    internal const uint GSS_C_MUTUAL_FLAG = 2;
    internal const uint GSS_C_REPLAY_FLAG = 4;
    internal const uint GSS_C_SEQUENCE_FLAG = 8;
    internal const uint GSS_C_CONF_FLAG = 16;
    internal const uint GSS_C_INTEG_FLAG = 32;

    internal const uint GSS_IOV_BUFFER_TYPE_DATA = 1;
    internal const uint GSS_IOV_BUFFER_TYPE_HEADER = 2;
    internal const uint GSS_IOV_BUFFER_TYPE_PADDING = 9;
    internal const uint GSS_IOV_BUFFER_FLAG_ALLOCATE = 0x00010000;
    internal const uint GSS_IOV_BUFFER_FLAG_ALLOCATED = 0x00020000;

    /// <summary>The Kerberos V5 mechanism, 1.2.840.113554.1.2.2.</summary>
    internal static readonly byte[] KerberosMechanism = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x12, 0x01, 0x02, 0x02];

    /// <summary>A Kerberos principal name (user@REALM), 1.2.840.113554.1.2.2.1.</summary>
    internal static readonly byte[] KerberosPrincipalName = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x12, 0x01, 0x02, 0x02, 0x01];

    /// <summary>A host-based service name (HTTP@host), 1.2.840.113554.1.2.1.4.</summary>
    internal static readonly byte[] HostBasedServiceName = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x12, 0x01, 0x02, 0x01, 0x04];

    [StructLayout(LayoutKind.Sequential)]
    internal struct GssBuffer
    {
        public nuint Length;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GssOid
    {
        public uint Length;
        public IntPtr Elements;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GssIovBuffer
    {
        public uint Type;
        public GssBuffer Buffer;
    }

    [LibraryImport(Library, EntryPoint = "gss_import_name")]
    internal static partial uint ImportName(out uint minor, ref GssBuffer inputName, ref GssOid nameType, out IntPtr outputName);

    [LibraryImport(Library, EntryPoint = "gss_release_name")]
    internal static partial uint ReleaseName(out uint minor, ref IntPtr name);

    [LibraryImport(Library, EntryPoint = "gss_create_empty_oid_set")]
    internal static partial uint CreateEmptyOidSet(out uint minor, out IntPtr oidSet);

    [LibraryImport(Library, EntryPoint = "gss_add_oid_set_member")]
    internal static partial uint AddOidSetMember(out uint minor, ref GssOid member, ref IntPtr oidSet);

    [LibraryImport(Library, EntryPoint = "gss_release_oid_set")]
    internal static partial uint ReleaseOidSet(out uint minor, ref IntPtr oidSet);

    [LibraryImport(Library, EntryPoint = "gss_acquire_cred_with_password")]
    internal static partial uint AcquireCredentialWithPassword(out uint minor, IntPtr desiredName, ref GssBuffer password, uint timeRequired,
        IntPtr desiredMechanisms, int usage, out IntPtr credential, IntPtr actualMechanisms, IntPtr timeReceived);

    [LibraryImport(Library, EntryPoint = "gss_acquire_cred")]
    internal static partial uint AcquireCredential(out uint minor, IntPtr desiredName, uint timeRequired,
        IntPtr desiredMechanisms, int usage, out IntPtr credential, IntPtr actualMechanisms, IntPtr timeReceived);

    [LibraryImport(Library, EntryPoint = "gss_release_cred")]
    internal static partial uint ReleaseCredential(out uint minor, ref IntPtr credential);

    [LibraryImport(Library, EntryPoint = "gss_init_sec_context")]
    internal static partial uint InitSecurityContext(out uint minor, IntPtr credential, ref IntPtr context, IntPtr targetName,
        ref GssOid mechanism, uint requestFlags, uint timeRequired, IntPtr channelBindings, ref GssBuffer inputToken,
        IntPtr actualMechanism, out GssBuffer outputToken, out uint returnedFlags, IntPtr timeReceived);

    [LibraryImport(Library, EntryPoint = "gss_accept_sec_context")]
    internal static partial uint AcceptSecurityContext(out uint minor, ref IntPtr context, IntPtr acceptorCredential, ref GssBuffer inputToken,
        IntPtr channelBindings, IntPtr sourceName, IntPtr mechanism, out GssBuffer outputToken, out uint returnedFlags,
        IntPtr timeReceived, IntPtr delegatedCredential);

    [LibraryImport(Library, EntryPoint = "gss_delete_sec_context")]
    internal static partial uint DeleteSecurityContext(out uint minor, ref IntPtr context, IntPtr outputToken);

    [LibraryImport(Library, EntryPoint = "gss_release_buffer")]
    internal static partial uint ReleaseBuffer(out uint minor, ref GssBuffer buffer);

    [LibraryImport(Library, EntryPoint = "gss_wrap_iov")]
    internal static unsafe partial uint WrapIov(out uint minor, IntPtr context, int confidentialityRequested, uint qualityOfProtection,
        out int confidentialityState, GssIovBuffer* iov, int iovCount);

    [LibraryImport(Library, EntryPoint = "gss_unwrap_iov")]
    internal static unsafe partial uint UnwrapIov(out uint minor, IntPtr context, out int confidentialityState, out uint qualityOfProtection,
        GssIovBuffer* iov, int iovCount);

    [LibraryImport(Library, EntryPoint = "gss_release_iov_buffer")]
    internal static unsafe partial uint ReleaseIovBuffer(out uint minor, GssIovBuffer* iov, int iovCount);

    [LibraryImport(Library, EntryPoint = "gss_display_status")]
    internal static partial uint DisplayStatus(out uint minor, uint statusValue, int statusType, IntPtr mechanism,
        ref uint messageContext, out GssBuffer statusString);

    internal static bool IsError(uint major) => (major & GssErrorMask) != 0;
}
