// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;

namespace Tetron.PowerShellRemoting.Kerberos;

/// <summary>
/// Points the process's Kerberos library at a configuration file. MIT Kerberos reads <c>KRB5_CONFIG</c> from the
/// native environment each time it builds a context, so a rewritten file takes effect without a restart.
/// </summary>
public static partial class KerberosEnvironment
{
    /// <summary>
    /// Sets <c>KRB5_CONFIG</c> to <paramref name="configurationPath"/> for this process. .NET's
    /// <see cref="Environment.SetEnvironmentVariable(string, string)"/> only updates its own copy on Unix, which
    /// native code never reads, so this sets the native environment directly as well.
    /// </summary>
    public static void UseConfiguration(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Kerberos configuration files are a MIT Kerberos concept; Windows uses SSPI.");

        Environment.SetEnvironmentVariable("KRB5_CONFIG", configurationPath);
        if (SetNativeEnvironmentVariable("KRB5_CONFIG", configurationPath, 1) != 0)
            throw new InvalidOperationException($"Could not set KRB5_CONFIG (errno {Marshal.GetLastPInvokeError()}).");
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetNativeEnvironmentVariable(string name, string value, int overwrite);
}
