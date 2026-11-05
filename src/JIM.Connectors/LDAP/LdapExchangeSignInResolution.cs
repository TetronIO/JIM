// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The Exchange sign-in worked out from the settings, or why it cannot be: exactly one is set.
/// </summary>
internal sealed record LdapExchangeSignInResolution(LdapExchangeKerberosSignIn? SignIn, string? Refusal);
