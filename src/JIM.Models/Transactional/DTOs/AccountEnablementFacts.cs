// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// The two facts a password delivery's enabled-state decision reads at the attempt (#2064), as they stand in the
/// database then rather than when the change was claimed: an export can mark a claimed change, or withdraw a held
/// enable, while the delivery lane is busy with the accounts ahead of it.
/// </summary>
/// <param name="HoldsWithheldEnable">
/// Whether the account still holds an enabled value JIM withheld until a password exists (#2039): a Pending Export
/// change with <see cref="PendingExportAttributeValueChange.AwaitsInitialPassword"/> set, still awaiting confirmation.
/// </param>
/// <param name="EnabledStateSetByAttributeFlow">
/// Whether an export has written the account's enabled state, as recorded on the queued change
/// (<see cref="PendingPasswordChange.EnabledStateSetByAttributeFlow"/>). False where the change no longer exists.
/// </param>
public sealed record AccountEnablementFacts(bool HoldsWithheldEnable, bool EnabledStateSetByAttributeFlow);
