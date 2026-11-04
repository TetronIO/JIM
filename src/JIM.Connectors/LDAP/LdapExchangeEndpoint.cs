// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The remote PowerShell endpoint an Exchange server setting names, or why it names none: exactly one is set.
/// </summary>
internal sealed record LdapExchangeEndpoint(Uri? Endpoint, string? Refusal);
