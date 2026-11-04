// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The Kerberos sign-in to Exchange: the principal (<c>sAMAccountName@REALM</c>), its realm, and the KDC to ask.
/// </summary>
internal sealed record LdapExchangeKerberosSignIn(string Principal, string Realm, string Kdc);
