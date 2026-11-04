// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The Active Directory attributes only Exchange may set (#2755). Since Exchange 2007 there is no Recipient Update
/// Service, so writing these over LDAP creates no recipient: it leaves an object that looks mail-enabled with no mailbox
/// behind it, a state Exchange does not support. Only <c>Enable-Mailbox</c> and its siblings set them, so the LDAP
/// Connector declares them read-only and no export Attribute Flow can target them. They are still imported.
/// </summary>
/// <remarks>
/// Deliberately not listed, because writing them directly is supported or established practice:
/// <list type="bullet">
/// <item><c>mailNickname</c>, <c>proxyAddresses</c>, <c>targetAddress</c> and <c>msExchHideFromAddressLists</c>, which
/// may be written on an existing recipient, and which JIM's Standard Mappings already flow.</item>
/// <item><c>msExchMailboxGuid</c> and <c>msExchArchiveGUID</c>, which identity-sync tools write onto a mail user when
/// preparing it for a cross-forest mailbox move, and which on their own make no half-provisioned recipient.</item>
/// </list>
/// The list applies whether or not the Connected System manages Exchange recipients: the harm is the same either way.
/// </remarks>
internal static class LdapExchangeOwnedAttributes
{
    private const string OwnedDescription = "Set by Exchange when it creates or changes the recipient. JIM imports it, but writes it only through Exchange.";

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "msExchRecipientTypeDetails",
        "msExchRecipientDisplayType",
        "msExchRemoteRecipientType",
        "homeMDB",
        "homeMTA",
        "msExchHomeServerName",
        "legacyExchangeDN"
    };

    /// <summary>
    /// Declares <paramref name="attribute"/> read-only, saying why, when only Exchange may set it; leaves any other
    /// attribute as discovery found it.
    /// </summary>
    internal static void Declare(ConnectorSchemaAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (!Names.Contains(attribute.Name))
            return;

        attribute.Writability = AttributeWritability.ReadOnly;
        attribute.Description = OwnedDescription;
    }
}
