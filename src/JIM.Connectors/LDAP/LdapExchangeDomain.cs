// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The domain the Connected System is connected to, as the Exchange sign-in needs it: its naming context, its
/// NetBIOS name (null when the directory would not say), and the DNS name of the domain controller the bind reached,
/// which is the KDC the sign-in asks for tickets.
/// </summary>
internal sealed record LdapExchangeDomain(string NamingContext, string? NetBiosName, string? KdcHost);
