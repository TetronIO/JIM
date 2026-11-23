// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Resources;
using JIM.Models.Localisation;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The rules for the LDAP Connector's Exchange settings (#2021) that do not need Exchange itself, kept apart from the
/// Connector so they can be tested without a directory.
/// </summary>
internal static class LdapExchangeSettings
{
    /// <summary>
    /// Why the Exchange settings cannot be saved against this directory, or null when they can. Exchange recipients
    /// exist only in Active Directory, so managing them is refused on any other directory, Samba AD included: it serves
    /// Active Directory's protocols, but Exchange cannot run against it.
    /// </summary>
    internal static string? DirectoryTypeRefusal(bool manageExchangeRecipients, LdapDirectoryType directoryType) =>
        DirectoryTypeRefusalMessage(manageExchangeRecipients, directoryType)?.ToEnglish();

    /// <summary>The same refusal as <see cref="DirectoryTypeRefusal"/>, held as a message the portal shows in the viewer's language.</summary>
    internal static JimMessage? DirectoryTypeRefusalMessage(bool manageExchangeRecipients, LdapDirectoryType directoryType) =>
        manageExchangeRecipients && directoryType != LdapDirectoryType.ActiveDirectory
            ? JimMessage.Create(ConnectorMessageCatalogues.Connectors, nameof(ConnectorMessages.LdapExchangeDirectoryTypeRefusal),
                Describe(directoryType), LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS)
            : null;

    private static JimMessage Describe(LdapDirectoryType directoryType) => JimMessage.Create(ConnectorMessageCatalogues.Connectors, directoryType switch
    {
        LdapDirectoryType.SambaAD => nameof(ConnectorMessages.LdapDirectoryKindSambaAD),
        LdapDirectoryType.OpenLDAP => nameof(ConnectorMessages.LdapDirectoryKindOpenLDAP),
        LdapDirectoryType.DirectoryServer389 => nameof(ConnectorMessages.LdapDirectoryKindDirectoryServer389),
        _ => nameof(ConnectorMessages.LdapDirectoryKindUnrecognised)
    });
}
