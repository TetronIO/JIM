// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The directory account the Exchange sign-in uses, as the directory holds it.
/// </summary>
internal sealed record LdapExchangeAccount(string DistinguishedName, string SamAccountName);
