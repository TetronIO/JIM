// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// The names of the connector-defined attributes through which a Synchronisation Rule declares an object's Exchange
/// recipient state (#2021, PRD requirement 4). The one source of these names: the LDAP Connector adds attributes by
/// them, and export staging reads a Pending Export's declared state by them.
/// </summary>
/// <remarks>
/// They contain spaces, which no LDAP attribute name can, so a directory attribute can never be mistaken for one.
/// </remarks>
public static class ExchangeRecipientAttributes
{
    /// <summary>What kind of Exchange recipient the object is; one of <see cref="ExchangeRecipientTypes"/>.</summary>
    public const string RecipientType = "Exchange Recipient Type";

    /// <summary>The recipient's Exchange alias.</summary>
    public const string Alias = "Exchange Alias";

    /// <summary>The mailbox database for a mailbox, by name.</summary>
    public const string Database = "Exchange Database";

    /// <summary>The recipient's primary SMTP address.</summary>
    public const string PrimarySmtpAddress = "Exchange Primary SMTP Address";

    /// <summary>For a mail user or mail contact, the external SMTP address mail is delivered to.</summary>
    public const string ExternalAddress = "Exchange External Address";

    /// <summary>All five, in the order the portal lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [RecipientType, Alias, Database, PrimarySmtpAddress, ExternalAddress];
}
