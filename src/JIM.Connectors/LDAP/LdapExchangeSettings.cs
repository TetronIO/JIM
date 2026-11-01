// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
        manageExchangeRecipients && directoryType != LdapDirectoryType.ActiveDirectory
            ? $"Exchange recipients can be managed only on Active Directory, and this directory is {Describe(directoryType)}. Turn off {LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS}, or connect to an Active Directory domain controller."
            : null;

    private static string Describe(LdapDirectoryType directoryType) => directoryType switch
    {
        LdapDirectoryType.SambaAD => "Samba AD",
        LdapDirectoryType.OpenLDAP => "OpenLDAP",
        LdapDirectoryType.DirectoryServer389 => "389 Directory Server",
        _ => "not one JIM recognises"
    };
}
