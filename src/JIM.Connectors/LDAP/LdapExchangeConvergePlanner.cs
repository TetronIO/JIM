// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Decides how the LDAP Connector converges one recipient (#2021, requirements 8, 9 and 15 to 20): from what Exchange
/// holds now and what the Synchronisation Rules declare, the commands that bring the one to the other, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so every rule is table-tested without Exchange. Each command names the domain controller the export wrote the
/// object to (requirement 8), because Exchange otherwise picks one that may not have it yet.
/// </para>
/// <para>
/// An empty declared value means Exchange decides: the alias falls back to <c>mailNickname</c>, the primary address to
/// the Email Address Policies, and the database to automatic distribution. So an empty value is never compared or set.
/// A database is chosen only when the mailbox is created (requirement 20): moving a mailbox is a move request, not
/// convergence.
/// </para>
/// </remarks>
internal static class LdapExchangeConvergePlanner
{
    private const string User = "user";
    private const string Contact = "contact";
    private const string Group = "group";

    /// <summary>Exchange's <c>RecipientTypeDetails</c>, as Get-User, Get-Contact or Get-Group report it, in JIM's terms.</summary>
    private static readonly Dictionary<string, string> RecipientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserMailbox"] = ExchangeRecipientTypes.Mailbox,
        ["SharedMailbox"] = ExchangeRecipientTypes.SharedMailbox,
        ["RoomMailbox"] = ExchangeRecipientTypes.RoomMailbox,
        ["EquipmentMailbox"] = ExchangeRecipientTypes.EquipmentMailbox,
        ["MailUser"] = ExchangeRecipientTypes.MailUser,
        ["MailContact"] = ExchangeRecipientTypes.MailContact,
        ["MailUniversalDistributionGroup"] = ExchangeRecipientTypes.MailEnabledGroup,
        ["MailUniversalSecurityGroup"] = ExchangeRecipientTypes.MailEnabledGroup,
        ["MailNonUniversalGroup"] = ExchangeRecipientTypes.MailEnabledGroup,
        ["User"] = ExchangeRecipientTypes.None,
        ["DisabledUser"] = ExchangeRecipientTypes.None,
        ["Contact"] = ExchangeRecipientTypes.None,
        ["UniversalDistributionGroup"] = ExchangeRecipientTypes.None,
        ["UniversalSecurityGroup"] = ExchangeRecipientTypes.None,
        ["NonUniversalGroup"] = ExchangeRecipientTypes.None
    };

    private static readonly Dictionary<string, string> MailboxSwitches = new(StringComparer.Ordinal)
    {
        [ExchangeRecipientTypes.SharedMailbox] = "Shared",
        [ExchangeRecipientTypes.RoomMailbox] = "Room",
        [ExchangeRecipientTypes.EquipmentMailbox] = "Equipment"
    };

    /// <summary>
    /// JIM's recipient type for Exchange's <c>RecipientTypeDetails</c>: None for an object that is no recipient, and
    /// Unmanaged for a recipient this release does not manage (a remote or linked mailbox, a dynamic group), which JIM
    /// must never try to enable again.
    /// </summary>
    internal static string RecipientTypeOf(string? recipientTypeDetails) =>
        recipientTypeDetails != null && RecipientTypes.TryGetValue(recipientTypeDetails, out var type) ? type : ExchangeRecipientTypes.Unmanaged;

    /// <summary>
    /// The cmdlet that reads an object of <paramref name="objectType"/> whether or not it is a recipient yet: Get-Recipient
    /// finds only recipients, so it cannot tell "not a recipient" from "not there".
    /// </summary>
    internal static string ReadCommandFor(string objectType) => objectType.ToLowerInvariant() switch
    {
        Contact => "Get-Contact",
        Group => "Get-Group",
        _ => "Get-User"
    };

    /// <summary>
    /// The next step towards <paramref name="declared"/> for an object Exchange currently holds as
    /// <paramref name="current"/>.
    /// </summary>
    internal static LdapExchangeConvergePlan Plan(ExchangeRecipientTarget target, LdapExchangeRecipient current, ExchangeDeclaredState declared)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(declared);

        var declaredType = string.IsNullOrEmpty(declared.RecipientType) ? ExchangeRecipientTypes.None : declared.RecipientType;

        if (current.RecipientType == ExchangeRecipientTypes.Unmanaged)
            return LdapExchangeConvergePlan.Refuse($"Exchange holds {target} as a kind of recipient JIM does not manage, so JIM leaves it alone.");

        if (declaredType != ExchangeRecipientTypes.None && !Fits(target.ObjectType, declaredType))
            return LdapExchangeConvergePlan.Refuse($"A {target.ObjectType} cannot be a {declaredType} recipient.");

        if (current.RecipientType == ExchangeRecipientTypes.None)
            return declaredType == ExchangeRecipientTypes.None ? LdapExchangeConvergePlan.Nothing : Enable(target, declaredType, declared);

        if (current.RecipientType != declaredType)
            return LdapExchangeConvergePlan.Refuse(
                $"Exchange holds {target} as {current.RecipientType}, and the Synchronisation Rules declare {declaredType}. " +
                "JIM does not change an existing recipient's type in this release, so nothing was changed.");

        return Update(target, current, declared, declaredType);
    }

    private static bool Fits(string objectType, string recipientType) => objectType.ToLowerInvariant() switch
    {
        User => recipientType is ExchangeRecipientTypes.Mailbox or ExchangeRecipientTypes.SharedMailbox or ExchangeRecipientTypes.RoomMailbox
            or ExchangeRecipientTypes.EquipmentMailbox or ExchangeRecipientTypes.MailUser,
        Contact => recipientType == ExchangeRecipientTypes.MailContact,
        Group => recipientType == ExchangeRecipientTypes.MailEnabledGroup,
        _ => false
    };

    private static LdapExchangeConvergePlan Enable(ExchangeRecipientTarget target, string declaredType, ExchangeDeclaredState declared)
    {
        var parameters = Identify(target);
        string command;
        switch (declaredType)
        {
            case ExchangeRecipientTypes.MailUser or ExchangeRecipientTypes.MailContact:
                if (string.IsNullOrWhiteSpace(declared.ExternalAddress))
                    return LdapExchangeConvergePlan.Refuse($"A {declaredType} needs an external address, and none is declared for {target}.");
                command = declaredType == ExchangeRecipientTypes.MailUser ? "Enable-MailUser" : "Enable-MailContact";
                parameters.Add(new("ExternalEmailAddress", declared.ExternalAddress));
                break;
            case ExchangeRecipientTypes.MailEnabledGroup:
                command = "Enable-DistributionGroup";
                break;
            default:
                command = "Enable-Mailbox";
                if (MailboxSwitches.TryGetValue(declaredType, out var mailboxSwitch))
                    parameters.Add(new(mailboxSwitch, null));
                if (!string.IsNullOrWhiteSpace(declared.Database))
                    parameters.Add(new("Database", declared.Database));
                break;
        }

        if (!string.IsNullOrWhiteSpace(declared.Alias))
            parameters.Add(new("Alias", declared.Alias));

        return new LdapExchangeConvergePlan([new LdapExchangeCommand(command, parameters)], ReadAgainAfter: true, Refusal: null);
    }

    private static LdapExchangeConvergePlan Update(ExchangeRecipientTarget target, LdapExchangeRecipient current, ExchangeDeclaredState declared, string declaredType)
    {
        var parameters = Identify(target);
        var identifying = parameters.Count;

        if (Differs(declared.Alias, current.Alias))
            parameters.Add(new("Alias", declared.Alias));

        if (Differs(declared.PrimarySmtpAddress, current.PrimarySmtpAddress))
        {
            parameters.Add(new("PrimarySmtpAddress", declared.PrimarySmtpAddress));
            parameters.Add(new("EmailAddressPolicyEnabled", false));
        }

        if (declaredType is ExchangeRecipientTypes.MailUser or ExchangeRecipientTypes.MailContact &&
            Differs(WithoutSmtpPrefix(declared.ExternalAddress), WithoutSmtpPrefix(current.ExternalAddress)))
            parameters.Add(new("ExternalEmailAddress", declared.ExternalAddress));

        if (parameters.Count == identifying)
            return LdapExchangeConvergePlan.Nothing;

        var command = declaredType switch
        {
            ExchangeRecipientTypes.MailUser => "Set-MailUser",
            ExchangeRecipientTypes.MailContact => "Set-MailContact",
            ExchangeRecipientTypes.MailEnabledGroup => "Set-DistributionGroup",
            _ => "Set-Mailbox"
        };
        return new LdapExchangeConvergePlan([new LdapExchangeCommand(command, parameters)], ReadAgainAfter: false, Refusal: null);
    }

    /// <summary>
    /// The object, by its GUID where JIM has it (it survives a rename), else its DN, and the domain controller the export
    /// wrote it to.
    /// </summary>
    internal static List<KeyValuePair<string, object?>> Identify(ExchangeRecipientTarget target)
    {
        var parameters = new List<KeyValuePair<string, object?>>
        {
            new("Identity", target.ObjectGuid?.ToString() ?? target.DistinguishedName)
        };
        if (!string.IsNullOrWhiteSpace(target.DomainController))
            parameters.Add(new("DomainController", target.DomainController));
        return parameters;
    }

    /// <summary>Whether a declared value differs from Exchange's; an empty declared value leaves Exchange's choice alone.</summary>
    private static bool Differs(string? declared, string? current) =>
        !string.IsNullOrWhiteSpace(declared) && !string.Equals(declared.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? WithoutSmtpPrefix(string? address) =>
        address != null && address.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase) ? address[5..] : address;
}
