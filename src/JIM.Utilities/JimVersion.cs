// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;

namespace JIM.Utilities;

/// <summary>
/// The version this process is running, read once from the entry assembly. Lives here so JIM.Web, JIM.Worker and
/// JIM.Scheduler all report the same string the same way: each service writes it into its heartbeat, and the portal
/// compares them with its own, so three private copies of the parsing would be three ways for that comparison to
/// lie.
/// </summary>
public static class JimVersion
{
    /// <summary>
    /// The informational version of the entry assembly with the Source Link commit suffix removed ("0.15.0" rather
    /// than "0.15.0+6444a6934e..."), or "unknown" when no entry assembly or attribute is available (unmanaged hosts
    /// and some test runners).
    /// </summary>
    public static string Current { get; } = Clean(Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion);

    /// <summary>
    /// Strips the "+commit" suffix Source Link appends to an informational version. Exposed so the parsing has one
    /// definition and one test.
    /// </summary>
    public static string Clean(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "unknown";

        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }
}
