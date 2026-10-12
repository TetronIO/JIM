// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What delivering a password does to the account's enabled state, and why (#2064).
/// </summary>
/// <param name="EnableAccount">
/// True to enable the account as the password is set, false to disable it, or null to leave its enabled state alone.
/// Passed to the Connector as <see cref="Staging.PasswordSetOptions.EnableAccount"/>.
/// </param>
/// <param name="Basis">Which rule of the precedence decided it, stated on the delivery's Activity.</param>
public sealed record AccountEnablementDecision(bool? EnableAccount, AccountEnablementBasis Basis);
