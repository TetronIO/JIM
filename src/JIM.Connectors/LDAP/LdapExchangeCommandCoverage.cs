// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Which Exchange commands the LDAP Connector needs for each recipient type it manages (#2021, PRD requirement 3), and
/// what to tell an administrator when the account may not run some of them. Exchange hides a command the account's
/// roles do not grant rather than refusing it, so the commands the account can see are the commands it may run.
/// </summary>
internal static class LdapExchangeCommandCoverage
{
    // Read first, then enable or update: a recipient is read before anything is changed (requirement 9).
    private static readonly string[] Mailboxes = ["Get-Recipient", "Get-User", "Enable-Mailbox", "Set-Mailbox"];

    /// <summary>The commands each recipient type a Synchronisation Rule may declare needs, in the order types are named.</summary>
    private static readonly (string Type, string[] Commands)[] CommandsByType =
    [
        (ExchangeRecipientTypes.Mailbox, Mailboxes),
        (ExchangeRecipientTypes.SharedMailbox, Mailboxes),
        (ExchangeRecipientTypes.RoomMailbox, Mailboxes),
        (ExchangeRecipientTypes.EquipmentMailbox, Mailboxes),
        (ExchangeRecipientTypes.MailUser, ["Get-Recipient", "Get-User", "Enable-MailUser", "Set-MailUser"]),
        (ExchangeRecipientTypes.MailContact, ["Get-Recipient", "Get-Contact", "Enable-MailContact", "Set-MailContact"]),
        (ExchangeRecipientTypes.MailEnabledGroup, ["Get-Recipient", "Get-Group", "Enable-DistributionGroup", "Set-DistributionGroup"])
    ];

    /// <summary>Every command JIM needs, each once: what the check asks Exchange whether the account can see.</summary>
    internal static IReadOnlyList<string> RequiredCommands { get; } =
        CommandsByType.SelectMany(entry => entry.Commands).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The warning to give when the account cannot see some of the commands JIM needs, or null when it can see them all.
    /// </summary>
    /// <param name="principal">The account, as it signed in.</param>
    /// <param name="visibleCommands">The commands the account can see. Compared ignoring case, as PowerShell does.</param>
    internal static string? WarningFor(string principal, IReadOnlySet<string> visibleCommands)
    {
        var visible = new HashSet<string>(visibleCommands, StringComparer.OrdinalIgnoreCase);
        var missing = RequiredCommands.Where(command => !visible.Contains(command)).ToList();
        if (missing.Count == 0)
            return null;

        var affectedTypes = CommandsByType
            .Where(entry => entry.Commands.Any(command => !visible.Contains(command)))
            .Select(entry => entry.Type)
            .ToList();

        return $"The account {principal} cannot run {JoinNames(missing)} in Exchange, so JIM cannot manage these recipient types: " +
               $"{string.Join(", ", affectedTypes)}. Their changes will fail for insufficient permissions until it can. " +
               "Add the account to Exchange's Recipient Management role group, or give it a role that includes those commands.";
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count == 1
        ? names[0]
        : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}";
}
