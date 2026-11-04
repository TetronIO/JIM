// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;

namespace Tetron.PowerShellRemoting.Kerberos.Native;

/// <summary>
/// The OIDs passed to GSSAPI, held in native memory for the life of the process so their addresses never move.
/// </summary>
internal static class NativeOids
{
    internal static readonly GssapiNative.GssOid KerberosMechanism = Allocate(GssapiNative.KerberosMechanism);
    internal static readonly GssapiNative.GssOid KerberosPrincipalName = Allocate(GssapiNative.KerberosPrincipalName);
    internal static readonly GssapiNative.GssOid HostBasedServiceName = Allocate(GssapiNative.HostBasedServiceName);

    private static GssapiNative.GssOid Allocate(byte[] encoded)
    {
        var elements = Marshal.AllocHGlobal(encoded.Length);
        Marshal.Copy(encoded, 0, elements, encoded.Length);
        return new GssapiNative.GssOid { Length = (uint)encoded.Length, Elements = elements };
    }
}
