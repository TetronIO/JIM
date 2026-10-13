// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What delivering a password may write to the account's flags, and why: whether it is enabled (#2064), and whether its
/// password never expires (#2159). One decision over one ownership ladder, because on Active Directory both flags live
/// in the one attribute (userAccountControl), whose owner decides both.
/// </summary>
/// <param name="EnableAccount">
/// True to enable the account as the password is set, false to disable it, or null to leave its enabled state alone.
/// Passed to the Connector as <see cref="Staging.PasswordSetOptions.EnableAccount"/>.
/// </param>
/// <param name="EnablementBasis">Which rung of the ladder decided the enabled state, stated on the delivery's Activity.</param>
/// <param name="PasswordNeverExpires">
/// True to set "Password never expires", false to clear it, or null to leave it as the account has it. Always agrees
/// with the expiry behaviour sent where it is not null (true exactly for Never expires); null reaches the Connector as
/// <see cref="Staging.PasswordSetOptions.LeavePasswordNeverExpiresAsIs"/>.
/// </param>
/// <param name="PasswordNeverExpiresBasis">Which rung of the ladder decided "Password never expires".</param>
public sealed record AccountFlagsDecision(
    bool? EnableAccount,
    AccountFlagBasis EnablementBasis,
    bool? PasswordNeverExpires,
    AccountFlagBasis PasswordNeverExpiresBasis);
