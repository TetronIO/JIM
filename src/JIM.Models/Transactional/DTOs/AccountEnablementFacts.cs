// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// The facts a password delivery's enabled-state decision reads at the attempt (#2064, #2173), as they stand in the
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
/// <param name="ExportInFlight">
/// Whether an export to the account is being sent to the Connected System right now: its Pending Export is
/// <see cref="PendingExportStatus.Executing"/> (#2173). Such an export may be writing the enabled state, and records
/// that it did only once its batch has come back, so JIM's own decision to enable the account waits for it.
/// </param>
/// <param name="StrandedExportId">
/// The account's Pending Export where it has been Executing for longer than any batch takes, or null. A run that
/// failed or was cancelled can leave one there until the Worker restarts; it is reported rather than waited on.
/// </param>
/// <param name="CreateInFlight">
/// Whether the export in flight is the Create provisioning the account (#2160). The account's entry may already exist
/// in the Connected System part-way through that batch, and the export stages the account's Initial Password only once
/// the batch has come back; a password change delivered and removed in between would let that Initial Password be
/// queued over the person's own password. A change owing no first password therefore waits for the Create, and is
/// then given the account's first password by staging instead.
/// </param>
public sealed record AccountEnablementFacts(bool HoldsWithheldEnable, bool EnabledStateSetByAttributeFlow, bool ExportInFlight,
    Guid? StrandedExportId, bool CreateInFlight = false);
