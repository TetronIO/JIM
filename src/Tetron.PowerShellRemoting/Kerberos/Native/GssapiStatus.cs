// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;
using System.Text;

namespace Tetron.PowerShellRemoting.Kerberos.Native;

/// <summary>Turns GSSAPI status codes into exceptions that carry GSSAPI's own description of the failure.</summary>
internal static class GssapiStatus
{
    internal static void ThrowIfError(string operation, uint major, uint minor)
    {
        if (GssapiNative.IsError(major))
            throw new KerberosException(operation, major, minor, Describe(major, minor));
    }

    internal static string Describe(uint major, uint minor)
    {
        var parts = new List<string>();
        parts.AddRange(Messages(major, GssapiNative.GSS_C_GSS_CODE, mechanism: false));
        if (minor != 0)
            parts.AddRange(Messages(minor, GssapiNative.GSS_C_MECH_CODE, mechanism: true));
        return parts.Count == 0 ? "no description available" : string.Join("; ", parts);
    }

    private static IEnumerable<string> Messages(uint status, int statusType, bool mechanism)
    {
        var messages = new List<string>();
        uint messageContext = 0;
        var oid = NativeOids.KerberosMechanism;
        var oidHandle = GCHandle.Alloc(oid, GCHandleType.Pinned);
        try
        {
            // A status can carry several messages; GSSAPI hands them out one per call until the context is 0.
            for (var guard = 0; guard < 16; guard++)
            {
                var major = GssapiNative.DisplayStatus(out _, status, statusType,
                    mechanism ? oidHandle.AddrOfPinnedObject() : IntPtr.Zero, ref messageContext, out var text);
                if (GssapiNative.IsError(major))
                    break;
                try
                {
                    if (text.Value != IntPtr.Zero && text.Length > 0)
                        messages.Add(Encoding.UTF8.GetString(ToSpan(text)).TrimEnd('\0'));
                }
                finally
                {
                    GssapiNative.ReleaseBuffer(out _, ref text);
                }

                if (messageContext == 0)
                    break;
            }
        }
        finally
        {
            oidHandle.Free();
        }

        return messages;
    }

    private static unsafe ReadOnlySpan<byte> ToSpan(GssapiNative.GssBuffer buffer) =>
        new((void*)buffer.Value, checked((int)buffer.Length));
}
