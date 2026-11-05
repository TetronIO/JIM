// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using Tetron.PowerShellRemoting;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Classifies a failed Exchange command (#2021, requirement 26), which decides whether the delivery lane retries it or
/// parks it for a person, and words it for the administrator.
/// </summary>
/// <remarks>
/// Exchange's role-based access hides a command, or a parameter, the account may not use: the endpoint answers as if it
/// did not exist. So "not recognised" and "a parameter cannot be found" mean insufficient permissions, never a mistake in
/// JIM's command. Exchange's own failure category is read where it gives one; its message is kept as Exchange wrote it.
/// </remarks>
internal static class LdapExchangeFailureClassifier
{
    private const string RoleAdvice = " The account JIM signs in as cannot see this command or one of its parameters: add it to Exchange's Recipient Management role group.";

    /// <summary>The kind of <paramref name="failure"/> and the words to show, naming <paramref name="command"/>.</summary>
    /// <param name="command">The cmdlet that failed.</param>
    /// <param name="failure">The failure, as the remoting library classified it.</param>
    /// <param name="database">
    /// The mailbox database the change declared, if any. Exchange reports a database it cannot find under the same
    /// category as an object it cannot find, but a missing database will not appear by retrying (requirement 20).
    /// </param>
    internal static (ExchangeChangeFailureKind Kind, string Message) Classify(string command, RemoteFailure failure, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var words = $"{command}: {failure.Message}";

        return failure.Kind switch
        {
            RemoteFailureKind.Authentication => (ExchangeChangeFailureKind.AuthenticationFailed, words),
            RemoteFailureKind.Command when IsHidden(failure) => (ExchangeChangeFailureKind.InsufficientPermissions, words + RoleAdvice),
            RemoteFailureKind.Command when Is(failure, "ManagementObjectNotFoundException") && Names(failure, database) => (ExchangeChangeFailureKind.Refused, words),
            RemoteFailureKind.Command when Is(failure, "ManagementObjectNotFoundException") => (ExchangeChangeFailureKind.ObjectNotFound, words),
            RemoteFailureKind.Command when Is(failure, "ADServerUnavailableException") || Is(failure, "ADTransientException") =>
                (ExchangeChangeFailureKind.Unreachable, words),
            RemoteFailureKind.Command => (ExchangeChangeFailureKind.Refused, words),

            // Connection, timeout, a protocol fault and an untrusted certificate all retry, then park after 24 hours: the
            // first three often clear by themselves, and a certificate an administrator fixes is picked up by the retry.
            _ => (ExchangeChangeFailureKind.Unreachable, words)
        };
    }

    private static bool IsHidden(RemoteFailure failure) =>
        string.Equals(failure.Reason, "CommandNotFoundException", StringComparison.Ordinal) ||
        string.Equals(failure.Reason, "ParameterBindingException", StringComparison.Ordinal) ||
        failure.Message.Contains("is not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase) ||
        failure.Message.Contains("A parameter cannot be found that matches parameter name", StringComparison.OrdinalIgnoreCase);

    private static bool Names(RemoteFailure failure, string? database) =>
        !string.IsNullOrWhiteSpace(database) && failure.Message.Contains(database, StringComparison.OrdinalIgnoreCase);

    private static bool Is(RemoteFailure failure, string category) =>
        failure.FailureCategory?.EndsWith(category, StringComparison.Ordinal) == true ||
        string.Equals(failure.Reason, category, StringComparison.Ordinal);
}
