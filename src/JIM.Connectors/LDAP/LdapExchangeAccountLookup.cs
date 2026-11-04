// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// How to find the account a Username names (#2021): the form it was written in, the value to look for, and for
/// <c>DOMAIN\name</c> the NetBIOS domain it named.
/// </summary>
internal sealed record LdapExchangeAccountLookup(LdapExchangeAccountLookupKind Kind, string Value, string? NetBiosDomain);
