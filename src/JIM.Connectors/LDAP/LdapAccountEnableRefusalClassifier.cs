// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Tells Active Directory's refusal to create or enable an account that has no password apart from every other export
/// failure (#2039).
/// <para>
/// Active Directory will not hold an enabled user (<c>userAccountControl</c> without ACCOUNTDISABLE) that has no
/// password, and says so with the Win32 code ERROR_PASSWORD_RESTRICTIONS (<c>0000052D</c>) under WILL_NOT_PERFORM.
/// Reported as it arrives, that reads as a bare directory error, and the execution item it lands on reads as a JIM
/// defect. Classified, it names what to change: an Initial Password on the provisioning Synchronisation Rule (so JIM
/// holds the account disabled and enables it once the password is set), an account created disabled, or, for an
/// account that already exists, a password set with the set-password action.
/// </para>
/// <para>
/// The same recognition lets the Connector hold an enabling Update rather than fail it (#2158): where a password is
/// still owed to the account, the Update is sent again disabled and the delivery completes the enable. So an Update
/// that reaches the failure is one no password is owed to, or one whose resend was refused too.
/// </para>
/// <para>
/// The same code also means "this password fails the policy", so the change decides as well as the code: a Create
/// (which carries no password, since JIM sets passwords on a separate channel once the account exists) or an Update
/// writing <c>userAccountControl</c>. An Update that does not touch the enabled state met some other restriction and is
/// left alone. Classified for the whole Active Directory family, since Samba AD speaks the same codes, although today
/// it accepts an enabled account with no password.
/// </para>
/// </summary>
internal static class LdapAccountEnableRefusalClassifier
{
    /// <summary>
    /// Whether an export failure is Active Directory refusing to create or enable an account that has no password.
    /// </summary>
    /// <param name="exception">The exception the export failed with: the library's <see cref="DirectoryOperationException"/>
    /// carrying the response, or the <see cref="LdapException"/> the Connector raised itself from a non-success one.</param>
    /// <param name="pendingExport">The export that failed.</param>
    /// <param name="directoryType">The kind of directory the Connector detected.</param>
    internal static bool IsAccountEnableRefusal(Exception exception, PendingExport pendingExport, LdapDirectoryType directoryType)
    {
        if (directoryType is not (LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD))
            return false;

        if (!CouldEnableAnAccount(pendingExport))
            return false;

        return DiagnosticMessages(exception)
            .Any(message => message.Contains(LdapConnectorConstants.AD_ERROR_PASSWORD_RESTRICTIONS, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The result for a refusal <see cref="IsAccountEnableRefusal"/> recognised: the remedies first, then the directory's
    /// own words, kept whole so an administrator can see what it said.
    /// </summary>
    internal static ConnectedSystemExportResult FailureFor(Exception exception) =>
        ConnectedSystemExportResult.Failed(
            "Active Directory will not enable an account that has no password " +
            $"({LdapConnectorConstants.AD_ERROR_PASSWORD_RESTRICTIONS}). Give the provisioning Synchronisation Rule an " +
            "Initial Password, so that JIM holds the account disabled until the password is set and then enables it, or " +
            $"create the account disabled ({LdapConnectorConstants.UserAccountControlAttributeName} with ACCOUNTDISABLE, " +
            $"0x{LdapConnectorConstants.UAC_ACCOUNTDISABLE:X}, set). For an account that already exists, set a password on " +
            "it with the set-password action, enabling it there. If the Synchronisation Rule already has an Initial " +
            "Password, look under Operations > Passwords for one that expired or was cancelled before it was set. " +
            $"The directory said: {DirectoryMessage(exception)}",
            ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount);

    /// <summary>
    /// A Create always could: the account comes into existence with whatever enabled state the add carries, and never
    /// with a password. An Update could only by writing the attribute that holds the enabled state.
    /// </summary>
    private static bool CouldEnableAnAccount(PendingExport pendingExport) => pendingExport.ChangeType switch
    {
        PendingExportChangeType.Create => true,
        PendingExportChangeType.Update => pendingExport.AttributeValueChanges.Any(change =>
            string.Equals(change.Attribute?.Name, LdapConnectorConstants.UserAccountControlAttributeName, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    /// <summary>
    /// Every message the failure carries the directory's diagnostic in: the response's own, the server message the
    /// library recorded, and the exception's message, which the Connector builds around the server's text.
    /// </summary>
    private static IEnumerable<string> DiagnosticMessages(Exception exception)
    {
        string?[] messages = exception switch
        {
            DirectoryOperationException directoryOperationException => [directoryOperationException.Response?.ErrorMessage, exception.Message],
            LdapException ldapException => [ldapException.ServerErrorMessage, exception.Message],
            _ => []
        };

        return messages.Where(message => !string.IsNullOrEmpty(message)).Select(message => message!);
    }

    private static string DirectoryMessage(Exception exception) => exception switch
    {
        DirectoryOperationException { Response.ErrorMessage: { Length: > 0 } serverMessage } => serverMessage.Trim(),
        _ => exception.Message.Trim()
    };
}
