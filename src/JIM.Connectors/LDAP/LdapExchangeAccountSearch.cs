// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The search that finds a Username's account: a whole-domain search by filter, or a read of one entry by its DN.
/// </summary>
internal sealed record LdapExchangeAccountSearch(string BaseDn, string Filter, bool WholeSubtree);
