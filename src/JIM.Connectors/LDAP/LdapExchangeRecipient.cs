// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What Exchange holds for one object now, as the LDAP Connector reads it before converging (#2021, requirement 9): its
/// recipient type in JIM's terms (None when it is no recipient, Unmanaged when it is one JIM does not manage), and the
/// three values JIM can converge.
/// </summary>
internal sealed record LdapExchangeRecipient(string RecipientType, string? Alias, string? PrimarySmtpAddress, string? ExternalAddress);
