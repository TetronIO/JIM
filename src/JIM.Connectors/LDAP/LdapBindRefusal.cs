// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Turns a bind the directory refused for want of stronger authentication into a message that names the remedy.
/// </summary>
/// <remarks>
/// <para>
/// A domain controller that enforces LDAP signing (the Windows Server 2025 default) refuses a simple bind over plain
/// LDAP with strongerAuthRequired, LDAP result 8. The platform LDAP client raises that refusal as a
/// <see cref="DirectoryOperationException"/> with no response attached, so the result code is not on the exception and
/// only its text identifies it; a refusal of any other operation arrives with a response that carries the code. Both
/// are recognised, as is an <see cref="LdapException"/> carrying the code, which is how a client library that treats
/// it as an error rather than a result would raise it (#2042).
/// </para>
/// <para>
/// The directory's own words are kept in the message, so a search for them still finds it.
/// </para>
/// </remarks>
internal static class LdapBindRefusal
{
    /// <summary>
    /// The text the platform LDAP client gives a strongerAuthRequired refusal, and the only part of a response-less
    /// refusal that says what it was.
    /// </summary>
    private const string StrongAuthenticationText = "Strong authentication is required";

    /// <summary>
    /// An actionable replacement for <paramref name="refusal"/> when it is a strongerAuthRequired refusal; otherwise
    /// null, and the caller rethrows the original unchanged.
    /// </summary>
    /// <param name="refusal">What the bind raised.</param>
    /// <param name="useSecureConnection">Whether the connection was LDAPS, which decides what the remedy is.</param>
    internal static DirectoryOperationException? Describe(DirectoryException refusal, bool useSecureConnection)
    {
        if (!IsStrongAuthenticationRequired(refusal))
            return null;

        var message = useSecureConnection
            ? "The directory refused JIM's sign-in even though the connection is encrypted (LDAPS), because it requires stronger authentication " +
              $"than JIM's bind provides ('{refusal.Message}'). A domain controller that enforces LDAP channel binding refuses a simple " +
              "bind this way; check the directory's LDAP signing and channel binding policy for the account JIM connects as."
            : "The directory refused JIM's sign-in over an unencrypted LDAP connection because it requires LDAP signing or encryption " +
              $"('{refusal.Message}'). Windows Server 2025 domain controllers enforce this by default. Enable 'Use Secure Connection (LDAPS)?' " +
              $"on the Connected System and set 'Port' to {LdapConnectorConstants.DEFAULT_LDAPS_PORT}, unless the directory listens for LDAPS elsewhere.";

        return new DirectoryOperationException((refusal as DirectoryOperationException)?.Response, message, refusal);
    }

    private static bool IsStrongAuthenticationRequired(DirectoryException refusal) => refusal switch
    {
        // A response is authoritative: its result code decides, whatever the message says.
        DirectoryOperationException { Response: { } response } => response.ResultCode == ResultCode.StrongAuthRequired,
        DirectoryOperationException operation => operation.Message.Contains(StrongAuthenticationText, StringComparison.OrdinalIgnoreCase),
        LdapException ldap => ldap.ErrorCode == (int)ResultCode.StrongAuthRequired,
        _ => false
    };
}
