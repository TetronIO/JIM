// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How <see cref="LdapExchangeRecipientDeliveryLabTests"/> removes one of its fixtures: an Enable-* to run first when
/// the fixture is not a recipient (Exchange's Remove-* cmdlets remove recipients only), then the Remove-* that deletes
/// it from the directory. Every cmdlet named is in the Recipient Management role the lab's svc-jim holds.
/// </summary>
/// <param name="EnableFirst">The Enable-* to run first, or null when the fixture is already a recipient.</param>
/// <param name="Remove">The Remove-* that deletes it.</param>
internal sealed record LabFixtureRemoval(string? EnableFirst, string Remove)
{
    /// <summary>
    /// The removal for a fixture of <paramref name="kind"/> ("user", "contact" or "group") whose Get-Recipient reads
    /// <paramref name="recipientTypeDetails"/>, or null when Get-Recipient does not find it (a plain directory object).
    /// </summary>
    internal static LabFixtureRemoval For(string kind, string? recipientTypeDetails) => (kind, recipientTypeDetails) switch
    {
        // Still a plain directory object: made the simplest recipient of its kind, then removed as one.
        ("user", null) => new("Enable-MailUser", "Remove-MailUser"),
        ("contact", null) => new("Enable-MailContact", "Remove-MailContact"),
        ("group", null) => new("Enable-DistributionGroup", "Remove-DistributionGroup"),
        // Already a recipient: removed as whatever it is.
        (_, "MailUser") => new(null, "Remove-MailUser"),
        (_, "MailContact") => new(null, "Remove-MailContact"),
        (_, { } details) when details.StartsWith("Mail", StringComparison.Ordinal) && details.EndsWith("Group", StringComparison.Ordinal) =>
            new(null, "Remove-DistributionGroup"),
        (_, { } details) when details.EndsWith("Mailbox", StringComparison.Ordinal) => new(null, "Remove-Mailbox"),
        _ => throw new ArgumentException($"No removal for a {kind} that Exchange reads as {recipientTypeDetails}.")
    };
}
